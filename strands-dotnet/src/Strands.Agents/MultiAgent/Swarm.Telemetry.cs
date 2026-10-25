namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm
{
    /// <inheritdoc />
    public IAsyncEnumerable<IMultiAgentStreamEvent> StreamAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default)
        => _telemetry.ObserveAsync("swarm", Id, input, (trace, token) => StreamObservedAsync(input, options, trace, token), cancellationToken);
}
