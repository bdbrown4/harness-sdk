namespace Strands.Agents.Conversation;

/// <summary>Selects the source policy for reactive tool-result truncation only; it does not change sliding-window lifecycle or trim boundaries.</summary>
public enum ClassicToolResultTruncationPolicy
{
    /// <summary>Preserve Python's text/image policy and Unicode-character head/tail truncation. This is the default.</summary>
    Python,
    /// <summary>Use TypeScript's rich media/JSON policy and 450 UTF-16-unit text threshold, preserving valid surrogate boundaries.</summary>
    TypeScript
}
