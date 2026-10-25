using System.Runtime.CompilerServices;
using Amazon.S3.Model;
using Strands.Agents.S3;
using Strands.Agents.Storage;

namespace Strands.S3.Tests;

public sealed class S3StorageCancellationStackTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OriginalBackendCancellationRetainsItsIdentityTokenAndThrowSite(bool matchingCaller)
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        var token = matchingCaller ? caller.Token : independent.Token;
        var original = new OperationCanceledException("backend cancellation", null, token);
        using var client = new TestS3Client();
        client.OnRead = (_, _) =>
        {
            if (matchingCaller) caller.Cancel();
            return ThrowBackendCancellation(original);
        };
        await using var storage = new S3Storage(client, "fixture-bucket");

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.ReadAsync("entry", caller.Token));

        Assert.Same(original, actual);
        Assert.Equal(token, actual.CancellationToken);
        Assert.Contains(nameof(ThrowBackendCancellation), actual.StackTrace ?? "");
    }


    [Theory]
    [InlineData("write", true)]
    [InlineData("write", false)]
    [InlineData("index", true)]
    [InlineData("index", false)]
    [InlineData("delete", true)]
    [InlineData("delete", false)]
    [InlineData("list", true)]
    [InlineData("list", false)]
    [InlineData("search", true)]
    [InlineData("search", false)]
    [InlineData("cleanup", true)]
    [InlineData("cleanup", false)]
    public async Task OtherClassifiedCancellationPathsRetainTheirBackendThrowSite(string path, bool matchingCaller)
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        var token = matchingCaller ? caller.Token : independent.Token;
        var original = new OperationCanceledException("backend cancellation", null, token);
        void BeforeThrow() { if (matchingCaller) caller.Cancel(); }
        using var client = new TestS3Client();
        client.OnWrite = (_, _) => { BeforeThrow(); return ThrowCancellationAtBackend<Task<PutObjectResponse>>(original); };
        client.OnDelete = (_, _) => { BeforeThrow(); return ThrowCancellationAtBackend<Task<DeleteObjectResponse>>(original); };
        client.OnList = (_, _) => { BeforeThrow(); return ThrowCancellationAtBackend<Task<ListObjectsV2Response>>(original); };
        if (path == "index") client.OnWrite = null;
        var strategy = new ThrowingStrategy(BeforeThrow, original, path == "index");
        client.OnRead = (_, _) => Task.FromResult(new GetObjectResponse { ResponseStream = new CleanupCancellationBody(BeforeThrow, original) });
        await using var storage = new S3Storage(client, "fixture-bucket", new() { SearchStrategy = strategy });

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (path)
            {
                case "write":
                case "index": await storage.WriteAsync("entry", new byte[] { 1 }, caller.Token); break;
                case "delete": await storage.DeleteAsync("entry", caller.Token); break;
                case "list": _ = await storage.ListAsync("", caller.Token); break;
                case "search": _ = await storage.SearchAsync("term", caller.Token); break;
                case "cleanup": _ = await storage.ReadAsync("entry", caller.Token); break;
                default: throw new InvalidOperationException("Unknown fixture operation.");
            }
        });

        Assert.Same(original, actual);
        Assert.Equal(token, actual.CancellationToken);
        Assert.Contains(nameof(ThrowCancellationAtBackend), actual.StackTrace ?? "");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadCancellationRemainsTheOriginalPrimaryWhenResponseCleanupFails(bool matchingCaller)
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        var token = matchingCaller ? caller.Token : independent.Token;
        var original = new OperationCanceledException("read cancellation", null, token);
        var cleanup = new IOException("response cleanup");
        void BeforeThrow() { if (matchingCaller) caller.Cancel(); }
        var body = new ReadCancellationBody(BeforeThrow, original, cleanup);
        using var client = new TestS3Client();
        client.OnRead = (_, _) => Task.FromResult(new GetObjectResponse { ResponseStream = body });
        await using var storage = new S3Storage(client, "fixture-bucket");

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await storage.ReadAsync("entry", caller.Token));

        Assert.Same(original, actual);
        Assert.Equal(token, actual.CancellationToken);
        Assert.Contains(nameof(ThrowCancellationAtBackend), actual.StackTrace ?? "");
        Assert.Same(cleanup, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Exception>>(actual.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, body.DisposeCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static T ThrowCancellationAtBackend<T>(OperationCanceledException error) => throw error;

    private sealed class ThrowingStrategy(Action beforeThrow, OperationCanceledException error, bool failIndex) : StorageSearchStrategy
    {
        public override ValueTask IndexAsync(IStorage storage, string key, byte[] data, CancellationToken cancellationToken = default)
        {
            if (!failIndex) return default;
            beforeThrow();
            return ThrowCancellationAtBackend<ValueTask>(error);
        }
        public override ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default)
        {
            beforeThrow();
            return ThrowCancellationAtBackend<ValueTask<IReadOnlyList<StorageSearchResult>>>(error);
        }
    }

    private sealed class CleanupCancellationBody(Action beforeThrow, OperationCanceledException error) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) { beforeThrow(); _ = ThrowCancellationAtBackend<object>(error); }
            base.Dispose(disposing);
        }
    }

    private sealed class ReadCancellationBody(Action beforeThrow, OperationCanceledException primary, IOException cleanup) : MemoryStream
    {
        internal int DisposeCount { get; private set; }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            beforeThrow();
            return ThrowCancellationAtBackend<Task>(primary);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposeCount++; throw cleanup; }
            base.Dispose(disposing);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<GetObjectResponse> ThrowBackendCancellation(OperationCanceledException error)
        => throw error;
}
