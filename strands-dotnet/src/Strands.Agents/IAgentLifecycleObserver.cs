namespace Strands.Agents;

/// <summary>Optionally observes an agent after plugins initialize and before InitializedEvent. Observer instances remain caller-owned.</summary>
public interface IAgentLifecycleObserver
{
    /// <summary>Registers agent-local lifecycle callbacks once. Resources acquired by the observer should be released through agent shutdown.</summary>
    ValueTask ObserveAgentAsync(Agent agent, CancellationToken cancellationToken = default);
}

internal interface IAgentObservationLifetime
{
    ValueTask RollbackObservationAsync(Agent agent);
}
