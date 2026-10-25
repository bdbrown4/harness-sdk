namespace Strands.Agents.MultiAgent;

/// <summary>A graph step or cooperative wall-clock budget was exhausted.</summary>
public sealed class GraphLimitException : InvalidOperationException
{
    internal GraphLimitException(string budget, string message) : base(message) => Budget = budget;
    /// <summary>The exhausted budget: steps, timeout, or nodeTimeout.</summary>
    public string Budget { get; }
    /// <summary>The partial aggregate after all active nodes have been drained.</summary>
    public MultiAgentResult? Result { get; internal set; }
}
