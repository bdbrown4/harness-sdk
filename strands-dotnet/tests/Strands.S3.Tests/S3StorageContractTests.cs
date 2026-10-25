using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Strands.Agents.S3;
using Strands.Agents.Storage;

namespace Strands.S3.Tests;

public sealed class S3StorageContractTests
{
    [Fact]
    public async Task NativeFourOperationsRoundTripArbitraryBytesAndOverwrite()
    {
        using var client = new TestS3Client();
        await using var storage = new S3Storage(client, "fixture-bucket");
        var bytes = new byte[] { 0, 255, 13, 10, 128 };
        await storage.WriteAsync("entry", bytes);
        Assert.Equal(bytes, await storage.ReadAsync("entry"));
        var returned = (await storage.ReadAsync("entry"))!; returned[0] = 73;
        Assert.Equal(bytes, await storage.ReadAsync("entry"));
        await storage.WriteAsync("entry", Array.Empty<byte>());
        Assert.Empty((await storage.ReadAsync("entry"))!);
        Assert.Equal(new[] { "entry" }, await storage.ListAsync());
        await storage.DeleteAsync("entry");
        await storage.DeleteAsync("missing");
        Assert.Null(await storage.ReadAsync("entry"));
        Assert.Empty(await storage.ListAsync());
    }

    [Fact]
    public async Task CanonicalBasePrefixAndNamespacesUseExactNativeRequestKeys()
    {
        using var client = new TestS3Client();
        await using var storage = new S3Storage(client, "fixture-bucket", new() { Prefix = "/agents///data/" });
        var scoped = storage.Namespace("//scope///").Namespace("child");
        await scoped.WriteAsync("//nested///entry//", new byte[] { 7 });
        Assert.Equal("agents/data/scope/child/nested/entry", Assert.Single(client.Values).Key);
        Assert.Equal(new[] { "nested/entry" }, await scoped.ListAsync("nested/"));
        Assert.Equal("agents/data/scope/child/nested/", client.LastList!.Prefix);
        Assert.Equal(1000, client.LastList.MaxKeys);
        Assert.Equal("fixture-bucket", client.LastList.BucketName);
        await scoped.DeleteAsync("nested/entry");
        Assert.Equal("agents/data/scope/child/nested/entry", client.LastDelete!.Key);
    }

    [Fact]
    public async Task MissingObjectCodesAreDistinctFromMissingBucketsOrPermissions()
    {
        using var client = new TestS3Client();
        await using var storage = new S3Storage(client, "fixture-bucket");
        Assert.Null(await storage.ReadAsync("missing"));
        var primary = TestS3Client.Failure("NoSuchBucket", HttpStatusCode.NotFound);
        client.ReadFailure = primary;
        var error = await Assert.ThrowsAsync<StorageException>(async () => await storage.ReadAsync("entry"));
        Assert.Same(primary, error.InnerException);
    }
}

internal sealed class TestS3Client : AmazonS3Client
{
    internal Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);
    internal Exception? ReadFailure { get; set; }
    internal Func<GetObjectRequest, CancellationToken, Task<GetObjectResponse>>? OnRead { get; set; }
    internal Func<PutObjectRequest, CancellationToken, Task<PutObjectResponse>>? OnWrite { get; set; }
    internal Func<ListObjectsV2Request, CancellationToken, Task<ListObjectsV2Response>>? OnList { get; set; }
    internal Func<DeleteObjectRequest, CancellationToken, Task<DeleteObjectResponse>>? OnDelete { get; set; }
    internal Action? OnDispose { get; set; }
    internal int WriteCount { get; private set; }
    internal int ListCount { get; private set; }
    internal int DisposeCount { get; private set; }
    internal ListObjectsV2Request? LastList { get; private set; }
    internal DeleteObjectRequest? LastDelete { get; private set; }
    internal TestS3Client() : base(new BasicAWSCredentials("fixture-access", "fixture-secret"), new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1, MaxErrorRetry = 0 }) { }
    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        if (OnWrite is { } handler) return await handler(request, cancellationToken);
        using var memory = new MemoryStream();
        await request.InputStream.CopyToAsync(memory, 81920, cancellationToken);
        Values[request.Key] = memory.ToArray();
        return new() { HttpStatusCode = HttpStatusCode.OK };
    }
    public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
    {
        if (OnRead is { } handler) return handler(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadFailure is { } error) throw error;
        if (!Values.TryGetValue(request.Key, out var bytes)) throw Failure("NoSuchKey", HttpStatusCode.NotFound);
        return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream((byte[])bytes.Clone()), HttpStatusCode = HttpStatusCode.OK });
    }
    public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
    {
        if (OnDelete is { } handler) return handler(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); LastDelete = request; Values.Remove(request.Key);
        return Task.FromResult(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.NoContent });
    }
    public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
    {
        ListCount++;
        if (OnList is { } handler) return handler(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); LastList = request;
        return Task.FromResult(new ListObjectsV2Response { S3Objects = Values.Keys.Where(key => key.StartsWith(request.Prefix ?? "", StringComparison.Ordinal)).Select(key => new S3Object { Key = key }).ToList(), IsTruncated = false, HttpStatusCode = HttpStatusCode.OK });
    }
    protected override void Dispose(bool disposing) { if (disposing) { DisposeCount++; OnDispose?.Invoke(); } base.Dispose(disposing); }
    internal static AmazonS3Exception Failure(string code, HttpStatusCode status) => new(code, ErrorType.Sender, code, "fixture-request", status);
}
