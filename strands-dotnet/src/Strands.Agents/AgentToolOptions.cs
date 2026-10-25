namespace Strands.Agents;

/// <summary>Source-compatible options for exposing a borrowed agent as a tool accepting an input string.</summary>
public sealed record AgentToolOptions
{
    /// <summary>An explicit registered name; null derives a valid name from the wrapped agent.</summary>
    public string? Name { get; init; }
    /// <summary>The model-facing description, defaulting to the wrapped agent's description.</summary>
    public string? Description { get; init; }
    /// <summary>Keeps the wrapped conversation between calls. False resets its captured baseline before each new call and requires no session store.</summary>
    public bool PreserveContext { get; init; }
    /// <summary>Returns a successful meaningful result as the parent agent's final answer when used as the only call in a turn.</summary>
    public bool Delegate { get; init; }
}
