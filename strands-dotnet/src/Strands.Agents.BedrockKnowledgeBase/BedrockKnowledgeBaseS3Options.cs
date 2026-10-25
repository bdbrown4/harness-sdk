using Amazon.S3;

namespace Strands.Agents.BedrockKnowledgeBase;

/// <summary>The existing bucket and opaque key prefix used for S3 document writes.</summary>
public sealed record BedrockKnowledgeBaseS3Options
{
    /// <summary>The existing destination bucket; provisioning is not performed.</summary>
    public required string Bucket { get; init; }
    /// <summary>The opaque prefix; a slash is appended only if it does not already end with one.</summary>
    public required string Prefix { get; init; }
    /// <summary>An optional borrowed official S3 client, never disposed or reconfigured.</summary>
    public IAmazonS3? Client { get; init; }
}
