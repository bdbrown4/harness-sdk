namespace Strands.Agents.A2A;

/// <summary>Per-context native-agent execution settings. Context identifiers are not an authentication boundary.</summary>
public sealed record A2AExecutorOptions
{
    /// <summary>Creates one independent native agent per context. The token includes executor shutdown.</summary>
    public required Func<string, CancellationToken, ValueTask<Agent>> AgentFactory { get; init; }
    /// <summary>Whether successful factory results transfer ownership to the executor. Defaults to true; borrowed agents remain caller-owned.</summary>
    public bool OwnsAgents { get; init; } = true;
    /// <summary>The maximum cached or initializing contexts. Inactive contexts are evicted in least-recently-used order; active and paused contexts are retained. Defaults to 1000.</summary>
    public int MaxContexts { get; init; } = 1000;
    /// <summary>An optional application callback selecting invocation options from the explicit request context. The SDK adds no invocation-state keys.</summary>
    public Func<A2ARequestContext, InvocationOptions>? InvocationOptionsFactory { get; init; }
}
