namespace Strands.Agents.MultiAgent;

/// <summary>A swarm execution limit, with its completed visits retained in Result.</summary>
public sealed class SwarmLimitException(string budget, string message) : InvalidOperationException(message)
{
    /// <summary>The exhausted budget, such as steps, timeout, or nodeTimeout.</summary>
    public string Budget { get; } = budget;
    /// <summary>The partial aggregate after execution and lifecycle cleanup have finished.</summary>
    public MultiAgentResult? Result { get; internal set; }
}
