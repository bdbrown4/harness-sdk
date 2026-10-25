namespace Strands.Agents.MultiAgent;

/// <summary>A borrowed named callback declaration initialized once for each receiving orchestrator.</summary>
public sealed class MultiAgentPlugin
{
    private readonly Func<IMultiAgent, MultiAgentHookRegistry, CancellationToken, ValueTask> _initialize;

    /// <summary>Creates a declaration whose callbacks are registered on the receiving orchestrator's local registry.</summary>
    /// <param name="name">The ordinal plugin identity. Empty and whitespace names are supported.</param>
    /// <param name="initialize">The awaited initializer. Captures and resources remain caller-owned.</param>
    /// <exception cref="ArgumentNullException">The name or initializer is null.</exception>
    public MultiAgentPlugin(string name, Func<IMultiAgent, MultiAgentHookRegistry, CancellationToken, ValueTask> initialize)
    {
        Guard.NotNull(name, nameof(name));
        Guard.NotNull(initialize, nameof(initialize));
        Name = name;
        _initialize = initialize;
    }

    /// <summary>The unmodified ordinal plugin identity.</summary>
    public string Name { get; }

    internal ValueTask InitializeAsync(IMultiAgent owner, MultiAgentHookRegistry hooks, CancellationToken cancellationToken)
        => _initialize(owner, hooks, cancellationToken);
}
