using Amazon.S3.Model;
using Strands.Agents.S3;
using Strands.Agents.Storage;

namespace Strands.S3.Tests;

public sealed class S3StorageLifetimeTests
{
    [Fact]
    public async Task ResponseCleanupThatAcceptsCallerCancellationCannotPublishSuccess()
    {
        using var client = new TestS3Client(); using var caller = new CancellationTokenSource();
        client.OnRead = (_, _) => Task.FromResult(new GetObjectResponse { ResponseStream = new CleanupBody(caller.Cancel) });
        await using var storage = new S3Storage(client, "bucket");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.ReadAsync("entry", caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task OwnedClientCleanupCannotWaitForTheDisposalItIsCompleting()
    {
        var client = new TestS3Client(); S3Storage? storage = null; Exception? observed = null;
        client.OnDispose = () => observed = Record.Exception(() => { _ = storage!.DisposeAsync(); });
        storage = new S3Storage("bucket", null, () => client);
        await storage.DeleteAsync("missing"); await storage.DisposeAsync();
        Assert.IsType<InvalidOperationException>(observed); Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task UnusedOwnedClientIsNotConstructedAndUsedClientIsDisposedOnce()
    {
        var created = 0; var client = new TestS3Client();
        var unused = new S3Storage("bucket", null, () => { created++; return client; }); await unused.DisposeAsync(); Assert.Equal(0, created);
        var used = new S3Storage("bucket", null, () => { created++; return client; });
        await used.DeleteAsync("missing"); await used.DeleteAsync("missing");
        await Task.WhenAll(used.DisposeAsync().AsTask(), used.DisposeAsync().AsTask()); used.Dispose();
        Assert.Equal(1, created); Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task BorrowedClientRemainsUsableAndItsConfigurationUnchanged()
    {
        using var client = new TestS3Client(); var configuration = client.Config;
        var storage = new S3Storage(client, "bucket"); await storage.DisposeAsync();
        using var bytes = new MemoryStream(new byte[] { 5 });
        await client.PutObjectAsync(new PutObjectRequest { BucketName = "bucket", Key = "entry", InputStream = bytes });
        Assert.Equal(new byte[] { 5 }, client.Values["entry"]); Assert.Same(configuration, client.Config); Assert.Equal(0, client.DisposeCount);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("list")]
    public async Task RequestedCallerCancellationWinsAMismatchedBackendFailureWithCause(string operation)
    {
        using var client = new TestS3Client(); using var caller = new CancellationTokenSource(); var primary = new IOException("backend failed while cancellation arrived");
        client.OnRead = (_, _) => { caller.Cancel(); throw primary; }; client.OnWrite = (_, _) => { caller.Cancel(); throw primary; };
        client.OnDelete = (_, _) => { caller.Cancel(); throw primary; }; client.OnList = (_, _) => { caller.Cancel(); throw primary; };
        await using var storage = new S3Storage(client, "bucket");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (operation == "read") await storage.ReadAsync("entry", caller.Token);
            else if (operation == "write") await storage.WriteAsync("entry", new byte[] { 1 }, caller.Token);
            else if (operation == "delete") await storage.DeleteAsync("entry", caller.Token);
            else await storage.ListAsync("", caller.Token);
        });
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Same(primary, error.InnerException);
    }

    [Fact]
    public async Task MatchingCallerCancellationAndIndependentBackendCancellationRetainIdentity()
    {
        using var client = new TestS3Client(); using var caller = new CancellationTokenSource();
        var original = new OperationCanceledException(caller.Token);
        client.OnRead = (_, _) => { caller.Cancel(); throw original; };
        await using var storage = new S3Storage(client, "bucket");
        Assert.Same(original, await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.ReadAsync("entry", caller.Token)));
        var independent = new OperationCanceledException("backend timeout"); client.OnRead = (_, _) => throw independent;
        Assert.Same(independent, await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.ReadAsync("entry")));
    }

    [Fact]
    public async Task CallerCancellationJoinsAnUncooperativePutAndDoesNotClaimItWasUncommitted()
    {
        using var client = new TestS3Client(); using var caller = new CancellationTokenSource();
        var entered = S3StorageSafetyTests.Signal(); var release = S3StorageSafetyTests.Signal(); var committed = false;
        client.OnWrite = async (_, _) => { entered.TrySetResult(true); await release.Task; committed = true; return new(); };
        await using var storage = new S3Storage(client, "bucket");
        var write = storage.WriteAsync("entry", new byte[] { 7 }, caller.Token).AsTask();
        try { await S3StorageSafetyTests.Ready(entered.Task); caller.Cancel(); Assert.False(write.IsCompleted); }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await write);
        Assert.Equal(caller.Token, error.CancellationToken); Assert.True(committed); Assert.Equal(1, client.WriteCount);
        await storage.DeleteAsync("next");
    }

    [Fact]
    public async Task AcceptedOperationsRunConcurrentlyAndCallbackSelfDisposalFailsFast()
    {
        using var client = new TestS3Client(); var both = S3StorageSafetyTests.Signal(); var release = S3StorageSafetyTests.Signal(); var entered = 0; S3Storage? storage = null;
        client.OnWrite = async (_, _) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await storage!.DisposeAsync());
            if (Interlocked.Increment(ref entered) == 2) both.TrySetResult(true);
            await release.Task; return new();
        };
        storage = new S3Storage(client, "bucket");
        var first = storage.WriteAsync("a", new byte[] { 1 }).AsTask(); var second = storage.WriteAsync("b", new byte[] { 2 }).AsTask();
        try { await S3StorageSafetyTests.Ready(both.Task); }
        finally { release.TrySetResult(true); await S3StorageSafetyTests.Ready(Task.WhenAll(first, second)); await storage.DisposeAsync(); }
    }

    [Fact]
    public async Task DisposingAnOwnedClientPreservesItsIndependentCleanupFailure()
    {
        var primary = new IOException("client cleanup"); var client = new TestS3Client { OnDispose = () => throw primary };
        var storage = new S3Storage("bucket", null, () => client); await storage.DeleteAsync("entry");
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await storage.DisposeAsync()));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await storage.DisposeAsync())); Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task ACompletedOperationCannotLeaveAReentrantDisposalWaitInItsCancellationRegistration()
    {
        using var client = new TestS3Client(); S3Storage? storage = null; Exception? observed = null; CancellationTokenRegistration registration = default; var callbacks = 0;
        client.OnRead = (request, token) => { registration = token.Register(() => { callbacks++; observed = Record.Exception(() => { _ = storage!.DisposeAsync(); }); }); return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream() }); };
        storage = new S3Storage(client, "bucket");
        try { Assert.Empty((await storage.ReadAsync("entry"))!); await storage.DisposeAsync(); Assert.Equal(1, callbacks); Assert.IsType<InvalidOperationException>(observed); }
        finally { registration.Dispose(); await storage.DisposeAsync(); }
    }

    [Fact]
    public async Task EscapedCompletedCallbackContextCannotReopenClosedAdmission()
    {
        using var client = new TestS3Client(); var release = S3StorageSafetyTests.Signal(); Task? escaped = null; S3Storage? storage = null;
        client.OnRead = (_, _) => { escaped = Task.Run(async () => { await release.Task; await Assert.ThrowsAsync<ObjectDisposedException>(async () => await storage!.ReadAsync("escaped")); }); return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream() }); };
        storage = new S3Storage(client, "bucket"); await storage.ReadAsync("entry"); await storage.DisposeAsync();
        release.TrySetResult(true); await S3StorageSafetyTests.Ready(escaped!);
    }

    [Fact]
    public async Task AnActiveCallbackCannotStartAnUnsignaledNewSdkCallAfterDisposalClosesAdmission()
    {
        using var client = new TestS3Client(); var entered = S3StorageSafetyTests.Signal(); var release = S3StorageSafetyTests.Signal(); S3Storage? storage = null; var lateCalls = 0; Exception? refusal = null;
        client.OnRead = async (request, _) =>
        {
            if (request.Key == "late") { lateCalls++; return new() { ResponseStream = new MemoryStream() }; }
            entered.TrySetResult(true); await release.Task;
            refusal = await Record.ExceptionAsync(async () => await storage!.ReadAsync("late", CancellationToken.None));
            return new() { ResponseStream = new MemoryStream() };
        };
        storage = new S3Storage(client, "bucket"); var read = storage.ReadAsync("active").AsTask(); Task? disposal = null;
        try { await S3StorageSafetyTests.Ready(entered.Task); disposal = storage.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted); }
        finally { release.TrySetResult(true); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read); if (disposal is not null) await S3StorageSafetyTests.Ready(disposal); await storage.DisposeAsync(); }
        Assert.Equal(0, lateCalls); Assert.IsType<ObjectDisposedException>(refusal);
    }

    private sealed class CleanupBody(Action cleanup) : MemoryStream(new byte[] { 1 })
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) cleanup(); }
    }
}
