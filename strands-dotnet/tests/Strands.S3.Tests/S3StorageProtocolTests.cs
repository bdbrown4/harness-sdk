using System.Net;
using System.Text;
using Amazon.S3.Model;
using Strands.Agents.S3;
using Strands.Agents.Storage;

namespace Strands.S3.Tests;

public sealed class S3StorageProtocolTests
{
    [Theory]
    [InlineData("NoSuchKey", S3MissingKeyPolicy.NoSuchKeyOrNotFound, true)]
    [InlineData("NotFound", S3MissingKeyPolicy.NoSuchKeyOrNotFound, true)]
    [InlineData("NoSuchKey", S3MissingKeyPolicy.NoSuchKeyOnly, true)]
    [InlineData("NotFound", S3MissingKeyPolicy.NoSuchKeyOnly, false)]
    [InlineData("NoSuchBucket", S3MissingKeyPolicy.NoSuchKeyOrNotFound, false)]
    [InlineData("AccessDenied", S3MissingKeyPolicy.NoSuchKeyOrNotFound, false)]
    [InlineData("", S3MissingKeyPolicy.NoSuchKeyOrNotFound, false)]
    [InlineData("nosuchkey", S3MissingKeyPolicy.NoSuchKeyOrNotFound, false)]
    public async Task MissingPolicyUsesExactCodesInsteadOfAnAmbiguous404(string code, S3MissingKeyPolicy policy, bool missing)
    {
        using var client = new TestS3Client();
        var cause = TestS3Client.Failure(code, HttpStatusCode.NotFound); client.ReadFailure = cause;
        await using var storage = new S3Storage(client, "fixture-bucket", new() { MissingKeyPolicy = policy });
        if (missing) Assert.Null(await storage.ReadAsync("entry"));
        else Assert.Same(cause, (await Assert.ThrowsAsync<StorageException>(async () => await storage.ReadAsync("entry"))).InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("a/../b")]
    [InlineData("a\\b")]
    public async Task InvalidKeysNeverCreateTheLazyClient(string key)
    {
        var created = 0;
        await using var storage = new S3Storage("fixture-bucket", null, () => { created++; return new TestS3Client(); });
        await Assert.ThrowsAsync<StorageException>(async () => await storage.WriteAsync(key, new byte[] { 1 }));
        await Assert.ThrowsAsync<StorageException>(async () => await storage.ReadAsync(key));
        await Assert.ThrowsAsync<StorageException>(async () => await storage.DeleteAsync(key));
        Assert.Equal(0, created);
    }

    [Fact]
    public void InvalidOptionsFailBeforeOwnedClientResolutionAndNeverAlterABorrowedClient()
    {
        Assert.Throws<ArgumentException>(() => new S3Storage(" "));
        Assert.Throws<StorageException>(() => new S3Storage("bucket", new() { Prefix = "../root" }));
        Assert.Throws<ArgumentNullException>(() => new S3Storage("bucket", new() { Prefix = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new S3Storage("bucket", new() { MissingKeyPolicy = (S3MissingKeyPolicy)900 }));
        Assert.Throws<ArgumentException>(() => new S3Storage("bucket", new() { Region = "" }));
        using var client = new TestS3Client(); var config = client.Config;
        Assert.Throws<ArgumentException>(() => new S3Storage(client, "bucket", new() { Region = "us-east-1" }));
        Assert.Throws<ArgumentNullException>(() => new S3Storage(null!, "bucket"));
        Assert.Same(config, client.Config); Assert.Equal(0, client.DisposeCount);
    }

    [Fact]
    public async Task MoreThanOneThousandKeysUseOpaquePageTokensAndOrdinalViewRelativeKeys()
    {
        using var client = new TestS3Client();
        var physical = Enumerable.Range(0, 1003).Select(i => "base/scope/k" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        client.OnList = (request, _) =>
        {
            Assert.Equal("bucket", request.BucketName); Assert.Equal(1000, request.MaxKeys); Assert.Null(request.Delimiter); Assert.Equal("base/scope/", request.Prefix);
            var second = request.ContinuationToken is not null;
            if (second) Assert.Equal("opaque + / = token", request.ContinuationToken);
            return Task.FromResult(new ListObjectsV2Response { S3Objects = (second ? physical.Skip(1000) : physical.Take(1000)).Reverse().Select(key => new S3Object { Key = key }).ToList(), IsTruncated = !second, NextContinuationToken = second ? null : "opaque + / = token" });
        };
        await using var storage = new S3Storage(client, "bucket", new() { Prefix = "base" });
        Assert.Equal(physical.Select(key => key.Substring(5)), await storage.ListAsync("scope/")); Assert.Equal(2, client.ListCount);
    }

    [Theory]
    [InlineData("missing", 1)]
    [InlineData("repeated", 2)]
    [InlineData("cyclic", 3)]
    public async Task TruncatedResponsesMustMakeFiniteTokenProgress(string mode, int calls)
    {
        using var client = new TestS3Client();
        client.OnList = (_, _) => Task.FromResult(new ListObjectsV2Response { IsTruncated = true, NextContinuationToken = mode == "missing" ? null : mode == "cyclic" && client.ListCount == 2 ? "b" : "a" });
        await using var storage = new S3Storage(client, "bucket");
        Assert.IsType<InvalidDataException>((await Assert.ThrowsAsync<StorageException>(async () => await storage.ListAsync())).InnerException); Assert.Equal(calls, client.ListCount);
    }

    [Fact]
    public async Task NullableListingsAreEmptyButForeignPrefixKeysAreNotPublished()
    {
        using var client = new TestS3Client();
        client.OnList = (_, _) => Task.FromResult(new ListObjectsV2Response { S3Objects = null, IsTruncated = null });
        await using var storage = new S3Storage(client, "bucket", new() { Prefix = "base" });
        Assert.Empty(await storage.ListAsync("scope/"));
        client.OnList = (_, _) => Task.FromResult(new ListObjectsV2Response { S3Objects = [new() { Key = "base/scope-other/secret" }], IsTruncated = false });
        await Assert.ThrowsAsync<StorageException>(async () => await storage.ListAsync("scope/"));
    }

    [Theory]
    [InlineData("root/a//b")]
    [InlineData("root/a/../b")]
    [InlineData("root/a/")]
    [InlineData("root/a\\b")]
    [InlineData("root/")]
    [InlineData("root//a")]
    public async Task NoncanonicalNativeListingAliasesAreRejectedBeforePublishingAWholeResult(string physicalKey)
    {
        using var client = new TestS3Client();
        client.OnList = (_, _) => Task.FromResult(new ListObjectsV2Response { IsTruncated = false, S3Objects = [new() { Key = "root/accepted" }, new() { Key = physicalKey }] });
        await using var storage = new S3Storage(client, "bucket", new() { Prefix = "root" });
        var error = await Assert.ThrowsAsync<StorageException>(async () => await storage.ListAsync());
        Assert.IsType<InvalidDataException>(error.InnerException); Assert.Equal(1, client.ListCount);
    }

    [Fact]
    public async Task CanonicalSlashSegmentsAndPercentLiteralsAreOpaqueAndRemainReadable()
    {
        using var client = new TestS3Client();
        await using var storage = new S3Storage(client, "bucket", new() { Prefix = "root" });
        var keys = new[] { "a/b", "a%2F..%2Fb", "a/./b", "雪 /%" };
        foreach (var key in keys) await storage.WriteAsync(key, System.Text.Encoding.UTF8.GetBytes(key));
        Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), await storage.ListAsync());
        foreach (var key in keys) Assert.Equal(System.Text.Encoding.UTF8.GetBytes(key), await storage.ReadAsync(key));
    }

    [Fact]
    public async Task SearchUsesNativeDistinctTokenRankingAndBorrowedNamespaceViews()
    {
        using var client = new TestS3Client();
        await using var storage = new S3Storage(client, "bucket", new() { Prefix = "physical" });
        Assert.False(storage.IsNamespaced); Assert.False(storage.IsEphemeral);
        await storage.WriteAsync("scope/a", Encoding.UTF8.GetBytes("red red blue")); await storage.WriteAsync("scope/b", Encoding.UTF8.GetBytes("red")); await storage.WriteAsync("other", Encoding.UTF8.GetBytes("red blue green"));
        var results = await storage.Namespace("scope").SearchAsync("red blue");
        Assert.Equal(new[] { "a", "b" }, results.Select(value => value.Key)); Assert.Equal(new double[] { 2, 1 }, results.Select(value => value.Score));
        Assert.Empty(await storage.SearchAsync("!!!"));
    }

    [Fact]
    public async Task WriteSnapshotsCallerBytesBeforeItsFirstAwaitAndIndexesOnlyAfterCommit()
    {
        using var client = new TestS3Client();
        var entered = S3StorageSafetyTests.Signal(); var release = S3StorageSafetyTests.Signal();
        byte[]? received = null; var index = new RecordingSearch();
        client.OnWrite = async (request, token) => { Assert.False(request.AutoCloseStream); entered.TrySetResult(true); await release.Task; using var memory = new MemoryStream(); await request.InputStream.CopyToAsync(memory, 81920, token); received = memory.ToArray(); return new(); };
        await using var storage = new S3Storage(client, "bucket", new() { SearchStrategy = index });
        var source = new byte[] { 1, 2, 3 }; var write = storage.WriteAsync("/entry//", source).AsTask();
        try { await S3StorageSafetyTests.Ready(entered.Task); source[0] = 99; Assert.Null(index.Data); }
        finally { release.TrySetResult(true); await S3StorageSafetyTests.Ready(write); }
        Assert.Equal(new byte[] { 1, 2, 3 }, received); Assert.Equal(new byte[] { 1, 2, 3 }, index.Data); Assert.Equal("entry", index.Key); Assert.NotSame(source, index.Data);
    }

    [Fact]
    public async Task IndexFailureDoesNotReplayOrUndoAnAlreadyCommittedWrite()
    {
        using var client = new TestS3Client(); var primary = new IOException("index offline");
        await using var storage = new S3Storage(client, "bucket", new() { SearchStrategy = new RecordingSearch { Error = primary } });
        var error = await Assert.ThrowsAsync<StorageException>(async () => await storage.WriteAsync("entry", new byte[] { 3 }));
        Assert.Same(primary, error.InnerException); Assert.Contains("already written", error.Message); Assert.Equal(1, client.WriteCount); Assert.Equal(new byte[] { 3 }, await storage.ReadAsync("entry"));
    }

    private sealed class RecordingSearch : StorageSearchStrategy
    {
        internal byte[]? Data { get; private set; }
        internal string? Key { get; private set; }
        internal Exception? Error { get; init; }
        public override ValueTask IndexAsync(IStorage storage, string key, byte[] data, CancellationToken cancellationToken = default) { Data = data; Key = key; if (Error is { } error) throw error; return default; }
        public override ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default) => new(Array.Empty<StorageSearchResult>());
    }
}
