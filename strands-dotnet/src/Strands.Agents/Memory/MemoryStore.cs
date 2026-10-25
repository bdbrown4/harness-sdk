using System.Text.Json;

namespace Strands.Agents.Memory;

/// <summary>A fact retrieved from a store. JSON metadata is copied at construction.</summary>
public sealed record MemoryEntry
{
    /// <summary>Creates an entry with optional provenance and object-valued metadata.</summary>
    public MemoryEntry(string content, string? storeName = null, JsonElement? metadata = null)
    {
        Guard.NotNull(content, nameof(content));
        if (metadata is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Memory metadata must be a JSON object.", nameof(metadata));
        Content = content;
        StoreName = storeName;
        Metadata = metadata?.Clone();
    }
    /// <summary>The fact's textual content.</summary>
    public string Content { get; }
    /// <summary>The originating store, supplied by the manager when searching.</summary>
    public string? StoreName { get; }
    /// <summary>Detached backend metadata, such as a source path or retrieval score.</summary>
    public JsonElement? Metadata { get; }
}

/// <summary>Retrieval settings understood by every memory backend.</summary>
public sealed record MemorySearchOptions
{
    /// <summary>A positive result limit per selected store, overriding each store's default.</summary>
    public int? MaxSearchResults { get; init; }
    /// <summary>Names to search. Null selects every store; an empty collection selects none.</summary>
    public IReadOnlyList<string>? Stores { get; init; }
}

/// <summary>Settings for a programmatic memory write.</summary>
public sealed record MemoryAddOptions
{
    /// <summary>Names to write. Null selects every writable entry store; an empty collection selects none.</summary>
    public IReadOnlyList<string>? Stores { get; init; }
    /// <summary>Optional JSON object copied before starting the write.</summary>
    public JsonElement? Metadata { get; init; }
}

/// <summary>A named retrieval backend. Optional writes are separate capabilities, so read-only stores expose no write method.</summary>
public abstract class MemoryStore
{
    /// <summary>The unique store name used by programmatic and tool-based routing.</summary>
    public abstract string Name { get; }
    /// <summary>A description included in the memory tools' instructions.</summary>
    public virtual string? Description => null;
    /// <summary>An optional positive result limit; managers otherwise request three entries per store.</summary>
    public virtual int? MaxSearchResults => null;
    /// <summary>Whether the backend permits writes. Defaults to false, independently of implemented capabilities.</summary>
    public virtual bool Writable => false;
    /// <summary>Optional background extraction configuration. Null disables automatic writes.</summary>
    public virtual MemoryExtractionOptions? Extraction => null;
    /// <summary>Initializes the store against the selected agent dependencies.</summary>
    public virtual ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => default;
    /// <summary>Searches this store using an explicit positive limit.</summary>
    public abstract ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default);
    /// <summary>Optional backend tools registered alongside the manager's own tools.</summary>
    public virtual IReadOnlyList<ITool> GetTools() => [];
}

/// <summary>An optional backend capability for writing individual extracted facts.</summary>
public interface IWritableMemoryStore
{
    /// <summary>Persists one fact and its detached object-valued metadata.</summary>
    ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default);
}

/// <summary>An optional backend capability for server-side extraction from a complete message batch.</summary>
public interface IMessageMemoryStore
{
    /// <summary>Persists a batch. Aligned sequence numbers remain stable when the batch is retried.</summary>
    ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default);
}
