namespace Strands.Agents;

/// <summary>Selects the source profile for an explicitly configured direct tool call.</summary>
public enum DirectToolCallProfile
{
    /// <summary>Records the TypeScript three-message direct-call sequence.</summary>
    TypeScript,
    /// <summary>Records the Python four-message sequence and applies configured classic management after a successful call.</summary>
    Python
}

/// <summary>Configures an explicit direct tool call without changing the original no-history overloads.</summary>
public sealed record DirectToolCallOptions
{
    /// <summary>The source profile. Defaults to TypeScript.</summary>
    public DirectToolCallProfile Profile { get; init; } = DirectToolCallProfile.TypeScript;
    /// <summary>Whether the completed accepted call is recorded in history. Defaults to true.</summary>
    public bool RecordHistory { get; init; } = true;
    /// <summary>An optional Python user-message prefix, separate from tool input.</summary>
    public string? UserMessageOverride { get; init; }
}
