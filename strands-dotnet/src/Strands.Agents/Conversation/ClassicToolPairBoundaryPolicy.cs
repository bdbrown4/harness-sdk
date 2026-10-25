namespace Strands.Agents.Conversation;

/// <summary>Controls the fallback boundary when a classic sliding window contains only tool exchanges at its tail.</summary>
public enum ClassicToolPairBoundaryPolicy
{
    /// <summary>Retains the first complete assistant tool-use/user tool-result pair at the fallback boundary.</summary>
    Python,
    /// <summary>Also retains a validated user anchor before the fallback pair, matching the pinned TypeScript policy.</summary>
    TypeScript
}
