namespace Strands.Agents.MultiAgent;

public sealed partial class Graph
{
    /// <inheritdoc />
    public IAsyncEnumerable<IMultiAgentStreamEvent> StreamAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default)
        => _telemetry.ObserveAsync("graph", Id, input, (trace, token) => StreamCoreAsync(input, options, trace, token), cancellationToken);
}
