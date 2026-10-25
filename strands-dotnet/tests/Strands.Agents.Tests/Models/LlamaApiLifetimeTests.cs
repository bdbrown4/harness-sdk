using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Strands.Agents.Models;
using static Strands.Agents.Tests.Models.LlamaApiModelTests;
using static Strands.Agents.Tests.Models.LlamaApiTestTransport;

namespace Strands.Agents.Tests.Models;

public sealed class LlamaApiLifetimeTests
{
    [Fact]
    public async Task ObservedCancellationRemainsPrimaryWhenResponseCleanupAlsoFails()
    {
        using var cancellation = new CancellationTokenSource(); var cleanup = new IOException("cleanup");
        var content = new TrackedContent(TextStream(), cleanup); using var transport = new LlamaApiTestTransport("") { ResponseFactory = () => new(HttpStatusCode.OK) { Content = content } }; using var client = new HttpClient(transport);
        await using var reader = new LlamaApiModel(client, new("meta", "key")).StreamAsync(new([Message.User("input")]), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await reader.MoveNextAsync()); Assert.IsType<ModelMessageStartEvent>(reader.Current); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Contains(cleanup, Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])); Assert.Equal(1, content.Disposals); Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task CancellationFromSuccessfulResponseCleanupCannotReturnASuccessfulCollection()
    {
        using var cancellation = new CancellationTokenSource(); var content = new TrackedContent(TextStream(), onDispose: cancellation.Cancel);
        using var transport = new LlamaApiTestTransport("") { ResponseFactory = () => new(HttpStatusCode.OK) { Content = content } }; using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(1, content.Disposals); Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task MalformedProtocolKeepsItsFailureWhenResponseDisposalThrows()
    {
        var cleanup = new IOException("cleanup"); var content = new TrackedContent(Frame("{bad json}"), cleanup);
        using var transport = new LlamaApiTestTransport("") { ResponseFactory = () => new(HttpStatusCode.OK) { Content = content } }; using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
        Assert.Contains("malformed JSON", error.Message); Assert.Contains(cleanup, Assert.IsAssignableFrom<IEnumerable<Exception>>(error.Data["Strands.CleanupErrors"])); Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task EarlyDisposalClosesTheResponseAndLeavesTheTransportBorrowed()
    {
        var content = new TrackedContent(TextStream()); using var transport = new LlamaApiTestTransport("") { ResponseFactory = () => new(HttpStatusCode.OK) { Content = content } }; using var client = new HttpClient(transport);
        var reader = new LlamaApiModel(client, new("meta", "key")).StreamAsync(new([Message.User("input")])).GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        await reader.DisposeAsync(); Assert.Equal(1, content.Disposals); Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task ByteFragmentationPreservesUtf8AndCrLfFraming()
    {
        var content = new TrackedContent(TextStream("雪 café").Replace("\n", "\r\n"), chunk: 1); using var transport = new LlamaApiTestTransport("") { ResponseFactory = () => new(HttpStatusCode.OK) { Content = content } }; using var client = new HttpClient(transport);
        Assert.Equal("雪 café", Complete(await CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]))).Message.Text); Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task CancellingAnActualHttpSendPreservesTheCallerToken()
    {
        using var cancellation = new CancellationTokenSource(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new LlamaApiTestTransport("") { BeforeResponse = async (_, token) => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); } }; using var client = new HttpClient(transport);
        var pending = CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]), cancellation.Token); Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000))); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(0, transport.Disposals);
    }

    [Fact]
    public async Task AnExactCallerCancellationExceptionRetainsItsIdentity()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("caller", cancellation.Token);
        using var transport = new ImmediateThrowTransport(() => { cancellation.Cancel(); throw original; }); using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]), cancellation.Token));
        Assert.Same(original, error);
    }

    [Fact]
    public async Task AnUnrequestedTransportCancellationRemainsATypedTimeoutWithItsOriginalCause()
    {
        var original = new OperationCanceledException("transport timeout");
        using var transport = new ImmediateThrowTransport(() => throw original); using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")])));
        Assert.Same(original, error.InnerException); Assert.Contains("timed out", error.Message);
    }

    [Fact]
    public async Task NormalizingATransportCancellationRetainsCleanupDiagnosticsAndOriginalCause()
    {
        using var cancellation = new CancellationTokenSource(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var original = new OperationCanceledException("linked", linked.Token); var cleanup = new IOException("cleanup"); original.Data["Strands.CleanupErrors"] = new List<Exception> { cleanup };
        using var transport = new ImmediateThrowTransport(() => { cancellation.Cancel(); throw original; }); using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Same(original, error.InnerException);
        // Modern HttpClient can add an exact-caller-token wrapper itself; its original cause still owns the diagnostics.
        Assert.Contains(cleanup, (error.Data["Strands.CleanupErrors"] ?? error.InnerException!.Data["Strands.CleanupErrors"]) as IEnumerable<Exception> ?? []);
    }

    private sealed class ImmediateThrowTransport(Action send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            send();
            throw new InvalidOperationException("The fixture callback must throw.");
        }
    }

    [Fact]
    public async Task NormalizingCancellationPreservesRuntimeOnlyCleanupTasksOnFramework()
    {
        using var cancellation = new CancellationTokenSource(); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var cleanup = Task.FromResult<IReadOnlyList<Exception>>(Array.Empty<Exception>());
        var original = new RuntimeDiagnosticCancellationException(linked.Token); original.Data["Strands.CleanupTask"] = cleanup;
        using var transport = new ImmediateThrowTransport(() => { cancellation.Cancel(); throw original; }); using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Same(original, error.InnerException);
        if (error is TaskCanceledException)
            Assert.Same(cleanup, error.InnerException!.Data["Strands.CleanupTask"]);
        else
            Assert.Same(cleanup, error.Data["Strands.CleanupTask"]);
    }

    private sealed class RuntimeDiagnosticCancellationException(CancellationToken cancellationToken) : OperationCanceledException(cancellationToken)
    {
        public override System.Collections.IDictionary Data { get; } = new System.Collections.Hashtable();
    }

    internal sealed class TrackedContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly Exception? _cleanup;
        private readonly Action? _onDispose;
        internal int Disposals;
        internal TrackedContent(string body, Exception? cleanup = null, Action? onDispose = null, int chunk = int.MaxValue)
        {
            _stream = new ChunkedStream(Encoding.UTF8.GetBytes(body), chunk); _cleanup = cleanup; _onDispose = onDispose;
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _stream.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing); if (!disposing) return; Disposals++; _onDispose?.Invoke(); if (_cleanup is { } error) throw error;
        }
    }
    private sealed class ChunkedStream(byte[] bytes, int chunk) : MemoryStream(bytes, false)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(count, chunk), cancellationToken);
    }
}
