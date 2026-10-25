namespace Strands.Agents.Storage;

/// <summary>Configures a host SQLite FTS5 index.</summary>
public sealed record Bm25SearchStrategyOptions
{
    /// <summary>A database filename; null selects a sibling database derived from the storage root. Empty and :memory: are temporary databases.</summary>
    public string? DatabasePath { get; init; }
}
