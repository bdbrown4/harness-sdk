namespace Strands.Agents;

/// <summary>A borrowed source of tools with one explicit consumer context per agent.</summary>
public interface IToolProvider
{
    /// <summary>Admits a consumer before loading. Removal is attempted even when admission fails.</summary>
    ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default);
    /// <summary>Loads an ordered batch for this consumer. The agent filters and validates the batch before publishing it.</summary>
    ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default);
    /// <summary>Removes a consumer. Implementations must be idempotent; cleanup receives a noncancelled token.</summary>
    ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default);
}

/// <summary>The stable context shared by every provider declaration on one agent. Default children receive a new context.</summary>
public sealed class ToolProviderContext
{
    internal ToolProviderContext(Agent agent) { Agent = agent; ConsumerId = Guid.NewGuid().ToString(); }
    /// <summary>The consuming agent. Lifecycle callbacks cannot await that agent's own initialization, shutdown or disposal.</summary>
    public Agent Agent { get; }
    /// <summary>An opaque consumer identifier independent of persisted agent and session identities.</summary>
    public string ConsumerId { get; }
}
