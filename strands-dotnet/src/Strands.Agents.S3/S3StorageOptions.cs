using Strands.Agents.Storage;

namespace Strands.Agents.S3;

/// <summary>Determines which explicit AWS object error codes represent a missing value.</summary>
public enum S3MissingKeyPolicy
{
    /// <summary>Accepts NoSuchKey and NotFound, matching the pinned TypeScript backend.</summary>
    NoSuchKeyOrNotFound,
    /// <summary>Accepts only NoSuchKey, matching the pinned Python backend.</summary>
    NoSuchKeyOnly
}

/// <summary>Immutable byte-store settings, independent of caller-owned AWS client configuration.</summary>
public sealed record S3StorageOptions
{
    /// <summary>An optional slash-separated root prefix within the bucket.</summary>
    public string Prefix { get; init; } = "";
    /// <summary>An owned-client region override; null retains the AWS SDK's standard resolution.</summary>
    public string? Region { get; init; }
    /// <summary>An optional borrowed search/index strategy; otherwise a native keyword scan is used.</summary>
    public StorageSearchStrategy? SearchStrategy { get; init; }
    /// <summary>The exact missing-object error-code profile.</summary>
    public S3MissingKeyPolicy MissingKeyPolicy { get; init; }
}
