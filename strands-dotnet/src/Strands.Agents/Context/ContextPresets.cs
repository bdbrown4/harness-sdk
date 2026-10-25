namespace Strands.Agents.Context;

/// <summary>Named immutable strategy defaults. Use Offload directly when specific thresholds must remain fixed.</summary>
public static class ContextPresets
{
    /// <summary>Summarize older messages at seventy percent utilization, retaining the latest seventy percent of matches.</summary>
    public static OffloadStrategy ProactiveSummarization => Offload.Summarize().When(new() { Utilization = 0.7, PreserveRecent = 0.7 });
    /// <summary>Preview successful tool results larger than 2,500 estimated tokens, retaining 1,000 preview tokens.</summary>
    public static OffloadStrategy LargeToolOffloading => Offload.Truncate(OffloadTarget.ToolResults, new() { PreviewTokens = 1_000 }).When(new() { Threshold = 2_500 });
    /// <summary>Remove older context at full utilization, retaining four recent matching messages.</summary>
    public static OffloadStrategy OverflowProtection => Offload.Truncate().When(new() { Utilization = 1, PreserveRecent = 4 });
    /// <summary>Drop successful tool results older than the latest five matching messages.</summary>
    public static OffloadStrategy StaleToolCleanup => Offload.Drop(OffloadTarget.ToolResults).When(new() { PreserveRecent = 5 });
}
