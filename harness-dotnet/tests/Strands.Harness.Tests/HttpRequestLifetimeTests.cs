using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class HttpRequestLifetimeTests
{
    [Fact]
    public async Task PreCanceledCallerKeepsItsExactTokenWithoutSending()
    {
        using var handler = new HttpTestHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), Input(), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task IndependentTransportCancellationIsNotChangedIntoOwnedCancellation()
    {
        using var independent = new CancellationTokenSource();
        var sentinel = new OperationCanceledException("independent", null, independent.Token);
        using var client = new HttpClient(new HttpTestHandler((_, _) => throw sentinel));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), Input()));
        Assert.Same(sentinel, error); Assert.Equal(independent.Token, error.CancellationToken);
        Assert.False(independent.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryReadFailureSurvivesSelfOrDuplicateResponseAndRequestCleanup(bool self)
    {
        var primary = new IOException("body read failure");
        var cleanup = self ? primary : new IOException("cleanup failure");
        var stream = new OwnedStream(primary);
        var content = new HttpTestContent(Task.FromResult<Stream>(stream), cleanup);
        var requestCleanup = new HttpTestContent(Task.FromResult<Stream>(new MemoryStream()), cleanup);
        HttpRequestMessage? sentRequest = null;
        // Framework HttpClient disposes outgoing content before returning the response.
        // Inject this cleanup only once the intended response-read failure is reached.
        stream.BeforeRead = () => sentRequest!.Content = requestCleanup;
        using var client = new HttpClient(new HttpTestHandler((request, _) =>
        {
            sentRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var failure = await Assert.ThrowsAsync<IOException>(() => HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), Input()));
        Assert.Same(primary, failure); Assert.Equal(1, stream.Disposals);
        Assert.Equal(1, content.Disposals); Assert.Equal(1, requestCleanup.Disposals);
        if (self) Assert.Null(failure.Data["Strands.CleanupErrors"]);
        else Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task CallerCancellationCallbackCannotThrowResponseCleanupAndPhysicallyJoinsRead()
    {
        var stream = new OwnedStream(); var cleanup = new IOException("throwing content cleanup");
        var content = new HttpTestContent(Task.FromResult<Stream>(stream), cleanup);
        using var client = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        using var caller = new CancellationTokenSource();
        var operation = HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), Input(), caller.Token);
        Exception? primary = null;
        try { await HttpTestSupport.Wait(stream.Entered.Task); Assert.Null(Record.Exception(caller.Cancel)); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            stream.Release.TrySetResult(true);
            if (primary is not null) await JoinAfterFailure(operation, primary);
        }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpTestSupport.Wait(operation));
        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, stream.Disposals); Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task ContextSignalAloneCancelsTheExecutionWithItsCapturedToken()
    {
        var model = new HoldingModel();
        await using var agent = new Agent(new() { Model = model });
        var invocation = agent.InvokeAsync("Hold a real invocation scope.");
        var stream = new OwnedStream();
        using var client = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new HttpTestContent(Task.FromResult<Stream>(stream)) })));
        Task<string>? operation = null;
        Exception? primary = null;
        try
        {
            await HttpTestSupport.Wait(model.Entered.Task);
            var tool = HttpRequest.Create(new() { HttpClient = client }); var input = JsonSerializer.SerializeToElement(Input());
            var context = new ToolContext(agent, new("context-only", tool.Spec.Name, input), new Dictionary<string, object?>());
            Assert.True(context.CancelSignal.CanBeCanceled);
            operation = tool.InvokeAsync(input, context, CancellationToken.None).AsTask();
            await HttpTestSupport.Wait(stream.Entered.Task);
            agent.Cancel("execution signal only");
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpTestSupport.Wait(operation));
            Assert.Equal(context.CancelSignal, failure.CancellationToken);
            Assert.Equal(1, stream.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            stream.Release.TrySetResult(true); model.Release.TrySetResult(true);
            if (operation is not null && primary is not null) await JoinAfterFailure(operation, primary);
            try { await HttpTestSupport.Wait(invocation); }
            catch (Exception cleanup) when (primary is not null) { if (!ReferenceEquals(primary, cleanup)) primary.Data["FixtureCleanup"] = cleanup; }
        }
    }

    [Fact]
    public async Task LateOpenFailureAfterCancellationRetainsTheCauseAndJoinsBeforeReturning()
    {
        var release = HttpTestSupport.Gate<Stream>();
        var content = new HttpTestContent(release.Task);
        var cause = new IOException("late opener failed");
        using var caller = new CancellationTokenSource();
        using var client = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var operation = HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), Input(), caller.Token);
        Exception? primary = null;
        try
        {
            await HttpTestSupport.Wait(content.Entered.Task); caller.Cancel();
            Assert.False(operation.IsCompleted); Assert.Equal(0, content.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { release.TrySetException(cause); if (primary is not null) await JoinAfterFailure(operation, primary); }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpTestSupport.Wait(operation));
        Assert.Equal(caller.Token, failure.CancellationToken); Assert.Same(cause, failure.InnerException);
        Assert.Equal(1, content.Disposals);
    }

    [Theory]
    [InlineData(HttpRequestBehavior.TypeScript)]
    [InlineData(HttpRequestBehavior.Python)]
    public async Task DefaultOwnedDeadlineActuallyTerminatesASilentLocalServer(HttpRequestBehavior behavior)
    {
        await using var server = new SilentServer();
        using var caller = new CancellationTokenSource();
        var operation = HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { Behavior = behavior }), new { method = "GET", url = server.Endpoint }, caller.Token);
        Exception? primary = null;
        Exception? observed = null;
        try
        {
            await HttpTestSupport.Wait(server.Accepted.Task);
            observed = await Assert.ThrowsAsync<TimeoutException>(() => HttpTestSupport.Wait(operation, 45));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            caller.Cancel();
            if (primary is not null) await JoinAfterFailure(operation, primary);
            else
            {
                try { await operation; }
                catch (Exception error) when (ReferenceEquals(error, observed)) { }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedTimeoutCapAndModelShorteningDoNotMutateTheClient(bool modelShorter)
    {
        using var client = new HttpClient(new HttpTestHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token); return HttpTestSupport.Ok();
        }))
        { Timeout = modelShorter ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100) };
        var original = client.Timeout;
        var operation = HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client, Behavior = HttpRequestBehavior.Python }), new { method = "GET", url = "http://fixture.test/", timeout = modelShorter ? 0.05 : 30 });
        var failure = await Record.ExceptionAsync(() => HttpTestSupport.Wait(operation, 3));
        // Native HttpClient has an independent send timer. Either timer may win at
        // an equal cap; neither outcome is caller cancellation or a successful result.
        Assert.True(failure is TimeoutException or OperationCanceledException, failure?.ToString());
        Assert.Equal(original, client.Timeout);
    }

    [Fact]
    public async Task ExplicitDeadlineAlsoCoversAnIgnoringCancellationBodyReadUntilItIsPhysicallyClosed()
    {
        var stream = new OwnedStream();
        using var client = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new HttpTestContent(Task.FromResult<Stream>(stream)) })));
        var operation = HttpRequestTests.InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), new { method = "GET", url = "http://fixture.test/", timeout = 0.1 });
        try { await HttpTestSupport.Wait(stream.Entered.Task); await Assert.ThrowsAsync<TimeoutException>(() => HttpTestSupport.Wait(operation)); }
        finally { stream.Release.TrySetResult(true); }
        Assert.Equal(1, stream.Disposals);
    }

    private static object Input() => new { method = "GET", url = "http://fixture.test/" };
    private static async Task JoinAfterFailure(Task operation, Exception primary)
    {
        try { await operation; }
        catch (Exception cleanup) { if (!ReferenceEquals(primary, cleanup)) primary.Data["FixtureCleanup"] = cleanup; }
    }
    private sealed class HoldingModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = HttpTestSupport.Gate<bool>();
        internal TaskCompletionSource<bool> Release { get; } = HttpTestSupport.Gate<bool>();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true); await Release.Task; cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent(); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
    private sealed class OwnedStream(Exception? readFailure = null) : Stream
    {
        internal int Disposals;
        internal Action? BeforeRead;
        internal TaskCompletionSource<bool> Entered { get; } = HttpTestSupport.Gate<bool>();
        internal TaskCompletionSource<bool> Release { get; } = HttpTestSupport.Gate<bool>();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            BeforeRead?.Invoke();
            if (readFailure is not null) throw readFailure;
            await Release.Task; // Deliberately ignores the token; owned response close releases it.
            if (Disposals != 0) throw new ObjectDisposedException(nameof(OwnedStream));
            return 0;
        }
        protected override void Dispose(bool disposing) { if (disposing) { Interlocked.Increment(ref Disposals); Release.TrySetResult(true); } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class SilentServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<bool> _release = HttpTestSupport.Gate<bool>();
        private readonly Task _physical;
        private TcpClient? _client;
        internal TaskCompletionSource<bool> Accepted { get; } = HttpTestSupport.Gate<bool>();
        internal string Endpoint { get; }
        internal SilentServer() { _listener.Start(); Endpoint = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/silent"; _physical = ServeAsync(); }
        private async Task ServeAsync() { using (_client = await _listener.AcceptTcpClientAsync()) { Accepted.TrySetResult(true); await _release.Task; } }
        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult(true); _listener.Stop(); _client?.Dispose();
            try { await _physical; } catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }
    }
}
