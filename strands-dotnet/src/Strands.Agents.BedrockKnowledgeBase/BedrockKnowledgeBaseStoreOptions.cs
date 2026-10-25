using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Strands.Agents.Memory;

namespace Strands.Agents.BedrockKnowledgeBase;

/// <summary>Selects the pinned source's metadata and empty-filter conventions.</summary>
public enum BedrockKnowledgeBaseProfile
{
    /// <summary>TypeScript metadata keys, numeric wrappers, and present empty filter precedence.</summary>
    TypeScript,
    /// <summary>Python metadata keys, opaque metadata values, and empty filter scope fallback.</summary>
    Python
}

/// <summary>Knowledge-base retrieval and optional CUSTOM or S3 writes using borrowed official clients.</summary>
public sealed record BedrockKnowledgeBaseStoreOptions
{
    /// <summary>The unique memory-store routing name.</summary>
    public required string Name { get; init; }
    /// <summary>The existing knowledge-base identifier; provisioning is not performed.</summary>
    public required string KnowledgeBaseId { get; init; }
    /// <summary>Optional model-facing store description.</summary>
    public string? Description { get; init; }
    /// <summary>A positive store result limit; direct calls otherwise request ten.</summary>
    public int? MaxSearchResults { get; init; }
    /// <summary>An inclusive lower score bound, mutually exclusive with MaxScore; unscored results survive.</summary>
    public double? MinScore { get; init; }
    /// <summary>An inclusive upper score bound, mutually exclusive with MinScore; unscored results survive.</summary>
    public double? MaxScore { get; init; }
    /// <summary>The source conventions; defaults to TypeScript.</summary>
    public BedrockKnowledgeBaseProfile Profile { get; init; }
    /// <summary>Optional scope value; an empty string supplies no scope filter.</summary>
    public string? Scope { get; init; }
    /// <summary>The scope metadata key; null defaults to namespace, as does empty under Python.</summary>
    public string? ScopeMetadataKey { get; init; }
    /// <summary>An optional official retrieval filter, detached at construction.</summary>
    public RetrievalFilter? Filter { get; init; }
    /// <summary>A known type avoids detection; only an ordinal MANAGED value selects managed search.</summary>
    public KnowledgeBaseType? KnownKnowledgeBaseType { get; init; }
    /// <summary>Enables CUSTOM or S3 document ingestion; defaults to false.</summary>
    public bool Writable { get; init; }
    /// <summary>The direct-ingestion source type; exactly CUSTOM or S3 is accepted.</summary>
    public ContentDataSourceType? DataSourceType { get; init; }
    /// <summary>The existing ingestion data source; required for writable stores.</summary>
    public string? DataSourceId { get; init; }
    /// <summary>Optional official ACL entries, detached at construction; null and empty have distinct source semantics.</summary>
    public IReadOnlyList<DocumentAccessControlEntry>? AccessControlList { get; init; }
    /// <summary>Optional existing manager extraction configuration; null disables automatic writes.</summary>
    public MemoryExtractionOptions? Extraction { get; init; }
    /// <summary>An optional borrowed runtime client, never disposed or reconfigured.</summary>
    public IAmazonBedrockAgentRuntime? RuntimeClient { get; init; }
    /// <summary>An optional borrowed type-detection client, never disposed or reconfigured.</summary>
    public IAmazonBedrockAgent? AgentClient { get; init; }
    /// <summary>Optional S3 upload configuration, required for S3 writes.</summary>
    public BedrockKnowledgeBaseS3Options? S3 { get; init; }
    /// <summary>An optional region applied only to lazily owned clients.</summary>
    public string? Region { get; init; }
}
