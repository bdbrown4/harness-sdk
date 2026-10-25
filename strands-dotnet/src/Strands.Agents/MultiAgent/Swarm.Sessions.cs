namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm
{
    private bool _snapshotLoaded;
    private bool _restoreInitialized;

    private ValueTask SaveSessionAsync(CancellationToken cancellationToken) => _options.SessionStore is { } store
        ? store.SaveAsync(_options.SessionId!, CreateSnapshot(new()), cancellationToken) : default;

    private async ValueTask CommitVisitAsync(NodeResult result, MultiAgentState state, CancellationToken cancellationToken)
    {
        if (result.Status == Status.Interrupted)
        {
            _pendingState = state;
            _pendingNode = result.NodeId;
        }
        else if (result.Status == Status.Completed && result.StructuredOutput is { } output && output.TryGetProperty("agentId", out var target))
        {
            _pendingState = state;
            _pendingNode = target.GetString()!;
            _pendingNodeInput = _options.Routing == SwarmRoutingMode.Tool
                ? ToolInput(_pendingInput!, _pendingNode, state, output.GetProperty("message").GetString()) : HandoffInput(output);
        }
        else ClearPending();
        if (result.Status != Status.Interrupted || !state.Nodes[result.NodeId].HasPendingBody) _toolTurns.Remove(result.NodeId);
        if (_options.SaveLatestOn == MultiAgentSaveLatestStrategy.Node) await SaveSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ClearPending()
    { _pendingState = null; _pendingInput = null; _pendingNode = null; _pendingNodeInput = null; _toolTurns.Clear(); }
}
