namespace Strands.Agents.Storage;

/// <summary>Configures the separate QMD-style host filesystem search index.</summary>
public sealed record QmdSearchStrategyOptions
{
    /// <summary>An explicit index filename; null selects a sibling .&lt;root&gt;-qmd.sqlite file. The :memory: filename selects an in-memory index.</summary>
    public string? DatabasePath { get; init; }
}
