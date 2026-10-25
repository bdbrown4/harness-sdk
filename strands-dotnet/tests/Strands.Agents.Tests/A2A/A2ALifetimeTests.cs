using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ALifetimeTests
{
    [Fact]
    public async Task EarlyIteratorDisposalClosesTheResponseAndDoesNotProduceATerminalResult()
    {
        var handler = new A2AErrorTests.Handler(id => A2AAgentTests.Frame(id, new { kind = "task", id = "task", contextId = "context", status = new { state = "working" } }), true);
        using var http = new HttpClient(handler);
        var iterator = new A2AAgent(new(http, new(new("https://agent.test/")))).StreamAsync("Question").GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<A2AStreamUpdateEvent>(iterator.Current);

        await iterator.DisposeAsync();

        Assert.True(handler.LastContent!.Disposed);
    }

    [Fact]
    public async Task RemoteRpcFailureRemainsPrimaryWhenClosingTheResponseAlsoFails()
    {
        var cleanup = new IOException("response cleanup");
        var handler = new A2AErrorTests.Handler(id => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32001, message = "Task not found", data = new { taskId = "gone" } } }), disposeFailure: cleanup);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => new A2AClient(http, new(new("https://agent.test/"))).GetTaskAsync("gone"));

        Assert.Equal(-32001, error.Code);
        Assert.Equal("gone", error.ErrorData!.Value.GetProperty("taskId").GetString());
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.True(handler.LastContent!.Disposed);
    }

    [Fact]
    public async Task ProjectionFailureRemainsPrimaryWhenDisposingTheNestedTransportStreamFails()
    {
        var cleanup = new IOException("response cleanup");
        var handler = new A2AErrorTests.Handler(id => string.Concat(Enumerable.Range(0, 10).Select(index => A2AAgentTests.Frame(id, new { kind = "artifact-update", taskId = "task", contextId = "context", artifact = new { artifactId = index.ToString(System.Globalization.CultureInfo.InvariantCulture), parts = Array.Empty<object>() } }))), true, cleanup);
        using var http = new HttpClient(handler);
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))), new() { MaxResultBytes = 128 });

        var error = await Assert.ThrowsAsync<A2AProtocolException>(() => agent.InvokeAsync("Question"));

        Assert.Contains("MaxResultBytes", error.Message);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task ResubscriptionCorrelationFailureRemainsPrimaryWhenClosingItsStreamFails()
    {
        var cleanup = new IOException("response cleanup");
        var handler = new A2AErrorTests.Handler(id => A2AAgentTests.Frame(id, new { kind = "task", id = "different-task", contextId = "context", status = new { state = "completed" } }), true, cleanup);
        using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(new("https://agent.test/")));

        var error = await Assert.ThrowsAsync<A2AProtocolException>(async () => { await foreach (var _ in client.ResubscribeAsync("requested-task")) { } });

        Assert.Contains("different task", error.Message);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task CancellationAfterTheLastRawUpdateRetainsCleanupAsASecondaryFailure()
    {
        var cleanup = new IOException("response cleanup");
        var handler = new A2AErrorTests.Handler(id => A2AAgentTests.Frame(id, new { kind = "message", messageId = "response", role = "agent", parts = new[] { new { kind = "text", text = "complete" } } }), true, cleanup);
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var iterator = new A2AAgent(new(http, new(new("https://agent.test/")))).StreamAsync("Question", cancellationToken: cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        await iterator.DisposeAsync();
    }

    [Fact]
    public async Task CancellationInterruptsABodyThatIgnoresReadCancellationOnEveryAsset()
    {
        var body = new PausingBody();
        using var http = new HttpClient(new BodyHandler(body));
        using var cancellation = new CancellationTokenSource();
        var operation = new A2AAgent(new(http, new(new("https://agent.test/")))).InvokeAsync("Question", cancellationToken: cancellation.Token);
        await A2AWireTests.WithDeadline(body.ReadStarted.Task);

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => A2AWireTests.WithDeadline(operation));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(body.Disposed);
    }

    private sealed class BodyHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(A2AClientTests.Card, Encoding.UTF8, "application/json") });
            var content = new StreamContent(body); content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class PausingBody : Stream
    {
        internal TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { ReadStarted.TrySetResult(true); return _read.Task; }
        protected override void Dispose(bool disposing) { Disposed = true; _read.TrySetException(new ObjectDisposedException(nameof(PausingBody))); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
