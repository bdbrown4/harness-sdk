using Strands.Agents.Memory;
using Strands.Agents.Storage;

namespace Strands.Harness;

/// <summary>Cross-session recall and extraction for the native harness.</summary>
public sealed record HarnessMemoryOptions
{
    /// <summary>Whether to build a memory manager when no explicit manager is supplied. Defaults to true.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>The selected filesystem directory for the default file store. Defaults to ./.agent/memory.</summary>
    public string Directory { get; init; } = "./.agent/memory";
    /// <summary>Explicit stores used as-is. An empty list builds the default file store.</summary>
    public IReadOnlyList<MemoryStore> Stores { get; init; } = [];
    /// <summary>An explicit manager used verbatim; core options and application plugins take precedence.</summary>
    public MemoryManager? Manager { get; init; }
    /// <summary>An explicit byte storage root for the default file store, otherwise the selected binary filesystem is required.</summary>
    public IStorage? Storage { get; init; }
}
