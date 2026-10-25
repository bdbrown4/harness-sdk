namespace Strands.Agents.MultiAgent;

/// <summary>The source SDK's automatic orchestration span and content policy.</summary>
public enum MultiAgentTelemetryProfile
{
    /// <summary>Internal orchestration and per-visit node spans, raw input attributes, and aggregate usage.</summary>
    TypeScript,
    /// <summary>One active client span, convention-aware input events, and no automatic node or aggregate usage spans.</summary>
    Python
}

/// <summary>Configures automatic orchestration activities without creating a telemetry provider or exporter.</summary>
public sealed record MultiAgentTelemetryOptions
{
    /// <summary>The source-compatible span policy. Defaults to TypeScript.</summary>
    public MultiAgentTelemetryProfile Profile { get; init; }
    /// <summary>Optional attributes copied at construction. As in both upstream SDKs, caller attributes override operation attributes.</summary>
    public IReadOnlyDictionary<string, object?>? TraceAttributes { get; init; }
}
