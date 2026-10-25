using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrowingCancellationCallbacksCannotSkipOwnedClientDisposal(bool disposalAlsoFails)
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        var callback = new InvalidOperationException("cancellation callback");
        var cleanup = disposalAlsoFails ? new IOException("client disposal") : null;
        var client = new CallbackFailureClient(body, callback, cleanup);
        var model = new BedrockModel(new(), client, true);
        await using var stream = model.StreamAsync(new([Message.User("hello")])).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());

        var error = Assert.Throws<AggregateException>(model.Dispose);

        Assert.Contains(callback, error.Flatten().InnerExceptions);
        Assert.Equal(1, client.DisposeCalls);
        if (cleanup is not null) Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        model.Dispose();
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task CancellationDisposesAResponseBodyThatIgnoresReadCancellation()
    {
        using var body = new BlockingBody();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        using var cancellation = new CancellationTokenSource();
        var operation = BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")]), cancellation.Token);
        await BedrockModelTests.Bounded(body.ReadStarted.Task, TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        try
        {
            Assert.True(body.Disposed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BedrockModelTests.Bounded(operation, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            body.Dispose();
            try { await operation; } catch (Exception) { }
        }
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task RequestDeadlineCancelsBlockedReadsAndReportsTimeout()
    {
        using var body = new BlockingBody();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) });
        var operation = BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")]));
        await BedrockModelTests.Bounded(body.ReadStarted.Task, TimeSpan.FromSeconds(5));

        try
        {
            var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.Bounded(operation, TimeSpan.FromSeconds(10)));
            Assert.IsType<TimeoutException>(error.InnerException);
        }
        finally
        {
            body.Dispose();
            try { await operation; } catch (Exception) { }
        }
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task DisposingModelCancelsItsActiveResponseAndRetainsCallerClient()
    {
        using var body = new BlockingBody();
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        var operation = BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")]));
        await BedrockModelTests.Bounded(body.ReadStarted.Task, TimeSpan.FromSeconds(5));

        model.Dispose();

        try
        {
            Assert.True(body.Disposed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BedrockModelTests.Bounded(operation, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            body.Dispose();
            try { await operation; } catch (Exception) { }
        }
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task EarlyConsumerExitDisposesNativeResponse()
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        var events = model.StreamAsync(new ModelRequest([Message.User("hello")])).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        await events.DisposeAsync();

        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task AlreadyEnumeratedNativeOutputPreservesFailureAndDisposesItsBody()
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var output = new Amazon.BedrockRuntime.Model.ConverseStreamOutput(body);
        var firstReader = output.GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await firstReader.MoveNextAsync());
        using var client = new BedrockModelTests.StubClient { ResponseStream = output };
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
        await firstReader.DisposeAsync();
    }

    [Fact]
    public async Task ProtocolFailureRemainsPrimaryWhenNativeBodyDisposalAlsoFails()
    {
        using var wire = BedrockModelTests.Wire(("messageStart", """{"role":"user"}"""));
        var cleanup = new IOException("body cleanup failed");
        using var body = new CleanupFailureStream(wire.ToArray(), cleanup);
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new ModelRequest([Message.User("hello")])));

        Assert.Contains("role", error.Message);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.True(body.Disposed);
        Assert.Equal(1, body.DisposeCalls);
        Assert.False(client.Disposed);
    }

    private sealed class CallbackFailureClient(Stream body, Exception callback, Exception? cleanup)
        : Amazon.BedrockRuntime.AmazonBedrockRuntimeClient(new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public int DisposeCalls { get; private set; }
        public override Task<Amazon.BedrockRuntime.Model.ConverseStreamResponse> ConverseStreamAsync(Amazon.BedrockRuntime.Model.ConverseStreamRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.Register(() => throw callback);
            return Task.FromResult(new Amazon.BedrockRuntime.Model.ConverseStreamResponse { Stream = new Amazon.BedrockRuntime.Model.ConverseStreamOutput(body) });
        }
        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            base.Dispose(disposing);
            if (cleanup is not null) throw cleanup;
        }
    }

    private sealed class CleanupFailureStream(byte[] bytes, Exception cleanup) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public int DisposeCalls { get; private set; }
        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            Disposed = true;
            base.Dispose(disposing);
            if (DisposeCalls == 1) throw cleanup;
        }
    }

    private sealed class BlockingBody : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult(true);
            return _read.Task;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _read.TrySetException(new ObjectDisposedException(nameof(BlockingBody)));
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
