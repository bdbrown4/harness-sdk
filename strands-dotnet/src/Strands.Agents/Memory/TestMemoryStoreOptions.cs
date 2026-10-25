using System.Text.Json.Serialization;

namespace Strands.Agents.Memory;

/// <summary>Configures a small JSON-record memory store with optional local persistence.</summary>
public sealed record TestMemoryStoreOptions
{
    /// <summary>The store's unique routing name.</summary>
    public required string Name { get; init; }
    /// <summary>An optional description used by the memory manager.</summary>
    public string? Description { get; init; }
    /// <summary>An optional positive search limit; direct searches otherwise return at most ten entries.</summary>
    public int? MaxSearchResults { get; init; }
    /// <summary>Whether facts may be added. Defaults to true.</summary>
    public bool Writable { get; init; } = true;
    /// <summary>Optional automatic extraction configuration.</summary>
    public MemoryExtractionOptions? Extraction { get; init; }
    /// <summary>Whether records persist to a local file. Defaults to true; false selects a private ephemeral backend.</summary>
    public bool Persist { get; init; } = true;
    /// <summary>An optional backing-file path, ignored for storage selection when persistence is disabled.</summary>
    public string? Path { get; init; }
}

/// <summary>The opaque identifier of a newly stored or deduplicated fact.</summary>
/// <param name="Id">The original record identifier.</param>
public sealed record TestMemoryAddResult([property: JsonPropertyName("id")] string Id);
