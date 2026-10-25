using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.SageMakerRuntime;
using Amazon.SageMakerRuntime.Model;
using Strands.Agents;
using Strands.Agents.SageMaker;
using static Strands.SageMaker.Tests.SageMakerModelTests;

namespace Strands.SageMaker.Tests;

public sealed class SageMakerLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationClosesNativeBodiesThatIgnoreReadCancellation(bool disposeModel)
    {
        using var body = new BlockingBody(); using var client = new StubClient(body); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" }); using var cancellation = new CancellationTokenSource();
        var operation = CollectAsync(model, cancellationToken: cancellation.Token);
        await Bounded(body.Started.Task);
        if (disposeModel) model.Dispose(); else cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(operation));
        if (!disposeModel) Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(body.Disposed); Assert.False(client.Disposed);
    }

    [Fact]
    public async Task ProtocolErrorKeepsBodyDisposalFailureAsSecondary()
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("data: {broken\n"));
        var cleanup = new IOException("cleanup"); using var body = new CleanupBody(wire.ToArray(), cleanup);
        using var client = new StubClient(body); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", MalformedEventPolicy = SageMakerMalformedEventPolicy.Throw });
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model));
        Assert.Contains("malformed", error.Message);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, body.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedClientDisposalStillRunsWhenCancellationCallbacksThrow(bool secondFailure)
    {
        using var wire = Wire(Encoding.UTF8.GetBytes("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var callback = new IOException("callback"); var cleanup = new IOException("client cleanup");
        var client = new CallbackClient(wire, callback, secondFailure ? cleanup : null);
        var model = new SageMakerAIModel(new() { EndpointName = "endpoint" }, client, true);
        var stream = model.StreamAsync(new([Strands.Agents.Message.User("hello")])).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var error = Assert.Throws<AggregateException>(model.Dispose);
        Assert.Same(callback, Assert.Single(error.Flatten().InnerExceptions)); Assert.Equal(1, client.DisposeCalls);
        if (secondFailure) Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        model.Dispose(); Assert.Equal(1, client.DisposeCalls); await stream.DisposeAsync();
    }

    [Fact]
    public async Task NativeModelNotReadyExceptionBecomesRetryableWithOriginalCauseRetained()
    {
        var original = new ModelNotReadyException("loading"); using var client = new FailureClient(original); using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint" });
        var error = await Assert.ThrowsAsync<ModelThrottledException>(() => CollectAsync(model));
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public async Task InvalidCompleteResponseStillClosesTheNativeBody()
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes("{broken")); using var client = new StubClient(body);
        using var model = new SageMakerAIModel(client, new() { EndpointName = "endpoint", Payload = new() { Stream = false } });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model)); Assert.True(body.Disposed);
    }

    private static async Task<T> Bounded<T>(Task<T> operation)
    {
        if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(5))) != operation) throw new TimeoutException("SageMaker fixture exceeded its safety deadline.");
        return await operation;
    }
    private sealed class FailureClient(Exception error) : AmazonSageMakerRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public override Task<InvokeEndpointWithResponseStreamResponse> InvokeEndpointWithResponseStreamAsync(InvokeEndpointWithResponseStreamRequest request, CancellationToken cancellationToken = default) => Task.FromException<InvokeEndpointWithResponseStreamResponse>(error);
    }
    private sealed class CallbackClient(Stream body, Exception callback, Exception? cleanup) : AmazonSageMakerRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        internal int DisposeCalls { get; private set; }
        public override Task<InvokeEndpointWithResponseStreamResponse> InvokeEndpointWithResponseStreamAsync(InvokeEndpointWithResponseStreamRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.Register(() => throw callback);
            return Task.FromResult(new InvokeEndpointWithResponseStreamResponse { Body = new ResponseStream(body) });
        }
        protected override void Dispose(bool disposing) { DisposeCalls++; base.Dispose(disposing); if (cleanup is not null) throw cleanup; }
    }
    private sealed class CleanupBody(byte[] bytes, Exception failure) : MemoryStream(bytes)
    {
        internal int Calls { get; private set; }
        protected override void Dispose(bool disposing) { Calls++; base.Dispose(disposing); if (Calls == 1) throw failure; }
    }
    private sealed class BlockingBody : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { Started.TrySetResult(true); return _read.Task; }
        protected override void Dispose(bool disposing) { Disposed = true; _read.TrySetException(new ObjectDisposedException(nameof(BlockingBody))); base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
