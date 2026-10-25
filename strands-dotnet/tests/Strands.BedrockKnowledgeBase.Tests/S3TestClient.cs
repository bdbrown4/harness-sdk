using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Strands.BedrockKnowledgeBase.Tests;

internal sealed class S3TestClient : AmazonS3Client
{
    internal Func<PutObjectRequest, CancellationToken, Task<PutObjectResponse>> OnPut { get; set; } = (_, _) => Task.FromResult(new PutObjectResponse());
    internal Action? OnDispose { get; set; }
    internal int Calls;
    internal int Deletes;
    internal int Disposals;
    internal S3TestClient(AmazonS3Config? config = null) : base(new BasicAWSCredentials("fixture", "secret"), config ?? new() { RegionEndpoint = Amazon.RegionEndpoint.USEast1, MaxErrorRetry = 0 }) { }
    public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken token = default) { Interlocked.Increment(ref Calls); return OnPut(request, token); }
    public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken token = default) { Interlocked.Increment(ref Deletes); return Task.FromResult(new DeleteObjectResponse()); }
    protected override void Dispose(bool disposing) { if (disposing) { Interlocked.Increment(ref Disposals); OnDispose?.Invoke(); } base.Dispose(disposing); }
}
