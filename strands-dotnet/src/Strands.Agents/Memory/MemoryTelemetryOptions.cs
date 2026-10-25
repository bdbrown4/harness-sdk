namespace Strands.Agents.Memory;

/// <summary>The upstream memory result naming and backend activity-context policy.</summary>
public enum MemoryTelemetryProfile
{
    /// <summary>Uses storeName and leaves backend execution in the caller's activity context.</summary>
    TypeScript,
    /// <summary>Uses store_name and activates the memory activity around backend execution.</summary>
    Python,
}

/// <summary>Controls automatic search and add spans without configuring an exporter.</summary>
/// <remarks>Memory query, fact, and result payloads follow the upstream raw-content contract; model-content redaction does not apply. Attributes-only and Langfuse conventions still select payload placement.</remarks>
public sealed record MemoryTelemetryOptions
{
    /// <summary>The result naming and context policy. Defaults to TypeScript.</summary>
    public MemoryTelemetryProfile Profile { get; init; }
    /// <summary>Optional explicit custom attributes, copied with array values at manager construction. Operation attributes remain authoritative; agent attributes are not inherited implicitly.</summary>
    public IReadOnlyDictionary<string, object?>? TraceAttributes { get; init; }
}
