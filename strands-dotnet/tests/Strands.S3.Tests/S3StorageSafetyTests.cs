using Amazon.S3.Model;
using Strands.Agents.S3;
using Strands.Agents.Storage;

namespace Strands.S3.Tests;

public sealed class S3StorageSafetyTests
{
    [Fact]
    public async Task DisposalWaitsForAnAcceptedUncooperativeReadAndClosesAdmission()
    {
        using var client = new TestS3Client();
        var entered = Signal(); var release = Signal();
        client.OnRead = async (_, _) => { entered.TrySetResult(true); await release.Task; return new() { ResponseStream = new MemoryStream(new byte[] { 1 }) }; };
        var storage = new S3Storage(client, "fixture-bucket");
        var read = storage.ReadAsync("entry").AsTask();
        Task? disposal = null;
        try
        {
            await Ready(entered.Task);
            disposal = storage.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage.ReadAsync("new"));
        }
        finally
        {
            release.TrySetResult(true);
            try { await read; } catch (OperationCanceledException) { }
            if (disposal is not null) await Ready(disposal);
            await storage.DisposeAsync();
        }
        Assert.Equal(0, client.DisposeCount);
    }

    [Fact]
    public async Task ReadFailureStaysPrimaryWhenItsOwnedResponseBodyAlsoFailsCleanup()
    {
        using var client = new TestS3Client();
        var primary = new IOException("body read"); var cleanup = new IOException("body cleanup");
        var body = new FaultingBody(primary, cleanup);
        client.OnRead = (_, _) => Task.FromResult(new GetObjectResponse { ResponseStream = body });
        await using var storage = new S3Storage(client, "fixture-bucket");
        var error = await Assert.ThrowsAsync<StorageException>(async () => await storage.ReadAsync("entry"));
        Assert.Same(primary, error.InnerException);
        Assert.Contains(cleanup, Assert.IsAssignableFrom<IReadOnlyList<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, body.DisposeCount);
    }

    [Fact]
    public async Task StrategyCannotReportSuccessAfterItRequestsCallerCancellation()
    {
        using var client = new TestS3Client(); using var caller = new CancellationTokenSource();
        await using var storage = new S3Storage(client, "fixture-bucket", new() { SearchStrategy = new CancelingSearch(caller) });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.SearchAsync("needle", caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task IdenticalReadAndCleanupFailureDoesNotBecomeItsOwnSecondaryError()
    {
        using var client = new TestS3Client(); var primary = new IOException("same read and close error"); var body = new FaultingBody(primary, primary);
        client.OnRead = (_, _) => Task.FromResult(new GetObjectResponse { ResponseStream = body });
        await using var storage = new S3Storage(client, "bucket");
        var error = await Assert.ThrowsAsync<StorageException>(async () => await storage.ReadAsync("entry")); Assert.Same(primary, error.InnerException); Assert.Null(error.Data["Strands.CleanupErrors"]); Assert.Equal(1, body.DisposeCount);
    }

    [Fact]
    public async Task OwnedClientIsDisposedOnlyAfterPhysicalBodyCopyAndResponseCleanupFinish()
    {
        var entered = Signal(); var release = Signal(); var body = new GatedBody(entered, release); var client = new TestS3Client();
        client.OnRead = (_, _) => Task.FromResult(new GetObjectResponse { ResponseStream = body }); client.OnDispose = () => Assert.Equal(1, body.DisposeCount);
        var storage = new S3Storage("bucket", null, () => client); var read = storage.ReadAsync("entry").AsTask(); Task? disposal = null;
        try { await Ready(entered.Task); disposal = storage.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted); Assert.Equal(0, client.DisposeCount); Assert.False(read.IsCompleted); }
        finally { release.TrySetResult(true); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read); if (disposal is not null) await Ready(disposal); await storage.DisposeAsync(); }
        Assert.Equal(1, client.DisposeCount); Assert.Equal(1, body.DisposeCount);
    }

    [Fact]
    public async Task MissingSuccessfulResponseBodyIsAProtocolFailureAndPreCanceledCallsHaveNoClientEffects()
    {
        using var client = new TestS3Client(); client.OnRead = (_, _) => Task.FromResult(new GetObjectResponse());
        await using var storage = new S3Storage(client, "bucket");
        Assert.IsType<InvalidDataException>((await Assert.ThrowsAsync<StorageException>(async () => await storage.ReadAsync("entry"))).InnerException);
        var creations = 0; await using var lazy = new S3Storage("bucket", null, () => { creations++; return new TestS3Client(); }); using var caller = new CancellationTokenSource(); caller.Cancel();
        var read = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lazy.ReadAsync("entry", caller.Token)); Assert.Equal(caller.Token, read.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lazy.WriteAsync("entry", new byte[] { 1 }, caller.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lazy.DeleteAsync("entry", caller.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lazy.ListAsync("", caller.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lazy.SearchAsync("term", caller.Token)); Assert.Equal(0, creations);
    }

    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Ready(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task) throw new TimeoutException("The deterministic fixture gate did not settle.");
        await task;
    }
    private sealed class CancelingSearch(CancellationTokenSource caller) : StorageSearchStrategy
    {
        public override ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default)
        { caller.Cancel(); return new(Array.Empty<StorageSearchResult>()); }
    }
    private sealed class FaultingBody(Exception primary, Exception cleanup) : MemoryStream
    {
        internal int DisposeCount { get; private set; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(primary);
#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Task.FromException<int>(primary));
#endif
        protected override void Dispose(bool disposing) { if (disposing) { DisposeCount++; throw cleanup; } base.Dispose(disposing); }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => Task.FromException(primary);
    }
    private sealed class GatedBody(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release) : MemoryStream
    {
        internal int DisposeCount { get; private set; }
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) { entered.TrySetResult(true); await release.Task; await destination.WriteAsync(new byte[] { 1 }, 0, 1); }
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
    }
}
