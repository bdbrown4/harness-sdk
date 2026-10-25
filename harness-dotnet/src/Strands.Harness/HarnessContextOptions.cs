using Strands.Agents.Context;

namespace Strands.Harness;

/// <summary>SDK context reduction and original-content retrieval, enabled in automatic mode by default.</summary>
public sealed record HarnessContextOptions
{
    /// <summary>Whether to create a manager when no explicit core or application manager is supplied.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>The automatic or agentic SDK policy profile.</summary>
    public ContextMode Mode { get; init; } = ContextMode.Auto;
    /// <summary>An explicit manager, taking precedence over Enabled and Mode. Its state belongs to one agent.</summary>
    public ContextManager? Manager { get; init; }
}
