namespace Strands.Agents.Memory;

/// <summary>A model-facing memory tool's optional name, description, and allowed store scope.</summary>
public sealed record MemoryToolOptions
{
    /// <summary>A custom registered tool name, or null for search_memory or add_memory.</summary>
    public string? Name { get; init; }
    /// <summary>A custom model-facing description.</summary>
    public string? Description { get; init; }
    /// <summary>An optional store allowlist. A tool cannot widen this scope through its input.</summary>
    public IReadOnlyList<string>? Stores { get; init; }
    /// <summary>Whether a write tool waits for persistence. Background writes are drained by FlushAsync.</summary>
    public bool WaitForWrites { get; init; } = true;
}

/// <summary>Cross-session memory retrieval, writes, and model input injection.</summary>
public sealed record MemoryManagerOptions
{
    /// <summary>The nonempty collection of distinct named stores, copied at construction.</summary>
    public required IReadOnlyList<MemoryStore> Stores { get; init; }
    /// <summary>The search tool's configuration. Null disables the tool.</summary>
    public MemoryToolOptions? SearchTool { get; init; } = new();
    /// <summary>An optional write tool. Null disables it; stores remain accessible through AddAsync.</summary>
    public MemoryToolOptions? AddTool { get; init; }
    /// <summary>Ephemeral retrieval on fresh user turns. Null disables automatic injection.</summary>
    public MemoryInjectionOptions? Injection { get; init; } = new();
    /// <summary>Automatic memory search/add tracing. Defaults to TypeScript-compatible payload naming and ambient context.</summary>
    public MemoryTelemetryOptions Telemetry { get; init; } = new();
}

/// <summary>When retrieved memory is appended to model input.</summary>
public enum MemoryInjectionTrigger
{
    /// <summary>Only a fresh user message without tool results.</summary>
    UserTurn,
    /// <summary>Every model call, including autonomous tool turns.</summary>
    EveryTurn
}

/// <summary>Controls automatic retrieval without adding retrieved text to durable conversation history.</summary>
public sealed record MemoryInjectionOptions
{
    /// <summary>The default gate when Predicate is absent.</summary>
    public MemoryInjectionTrigger Trigger { get; init; }
    /// <summary>An optional custom gate over the current model-visible messages. Failure skips injection.</summary>
    public Func<IReadOnlyList<Message>, bool>? Predicate { get; init; }
    /// <summary>The positive maximum number of retrieved entries included across all stores; defaults to five.</summary>
    public int MaxEntries { get; init; } = 5;
    /// <summary>Optional query derivation; otherwise uses the latest user text or the latest autonomous assistant text.</summary>
    public Func<IReadOnlyList<Message>, CancellationToken, ValueTask<string?>>? Query { get; init; }
    /// <summary>Optional entry rendering; otherwise emits escaped memory and entry XML elements.</summary>
    public Func<IReadOnlyList<MemoryEntry>, CancellationToken, ValueTask<string?>>? Format { get; init; }
}
