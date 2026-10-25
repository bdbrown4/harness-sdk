namespace Strands.Agents.Telemetry;

/// <summary>Which recorded usage supplies a collector's context-size estimates.</summary>
public enum ContextSizePolicy
{
    /// <summary>Retain the most recent reported usage across invocation and cycle boundaries, matching TypeScript.</summary>
    LastReportedUsage,
    /// <summary>Select the newest cycle of the latest invocation; an invocation without cycles has no estimate, matching Python.</summary>
    LatestInvocationCycle
}
