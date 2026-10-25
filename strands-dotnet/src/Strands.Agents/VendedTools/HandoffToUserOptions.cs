namespace Strands.Agents.VendedTools;

/// <summary>The pinned source profile used to validate questions and project JSON replies.</summary>
public enum HandoffToUserBehavior
{
    /// <summary>Use TypeScript blank-string and typed tool-result block projection.</summary>
    TypeScript,
    /// <summary>Use Python blank-string, JSON text, and explicit result-envelope projection.</summary>
    Python,
}

/// <summary>Application-selected metadata and reply behavior for the optional handoff tool.</summary>
public sealed record HandoffToUserOptions
{
    /// <summary>The model-facing name, defaulting to handoff_to_user.</summary>
    public string Name { get; init; } = "handoff_to_user";
    /// <summary>An optional replacement for the default description.</summary>
    public string? Description { get; init; }
    /// <summary>The explicit source behavior, defaulting to TypeScript.</summary>
    public HandoffToUserBehavior Behavior { get; init; } = HandoffToUserBehavior.TypeScript;
}
