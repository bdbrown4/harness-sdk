namespace Strands.Agents.VendedTools;

/// <summary>Selects the source completion profile of the optional stop tool.</summary>
public enum StopBehavior
{
    /// <summary>Requests deferred cancellation after the accepted tool batch completes.</summary>
    PythonCancellation,
    /// <summary>Requests an ordinary after-tools end turn, following the experimental TypeScript profile.</summary>
    TypeScriptEndTurn,
}

/// <summary>Application-selected metadata, message limit, and completion profile.</summary>
public sealed record StopOptions
{
    /// <summary>The model-facing name, defaulting to stop.</summary>
    public string Name { get; init; } = "stop";
    /// <summary>An optional replacement for the shared source description.</summary>
    public string? Description { get; init; }
    /// <summary>The positive maximum length of a supplied message, defaulting to 4096.</summary>
    public int MaximumMessageLength { get; init; } = 4096;
    /// <summary>The selected completion profile, defaulting to Python deferred cancellation.</summary>
    public StopBehavior Behavior { get; init; } = StopBehavior.PythonCancellation;
}
