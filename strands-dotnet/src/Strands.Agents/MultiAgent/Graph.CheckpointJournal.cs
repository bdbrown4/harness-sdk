namespace Strands.Agents.MultiAgent;

public sealed partial class Graph
{
    // Node bodies can finish concurrently. Only the scheduler advances this committed view,
    // after their after-hooks have completed, so a sibling never persists half of another visit.
    private sealed class CheckpointJournal
    {
        private readonly Dictionary<string, NodeCheckpoint> _nodes;
        private readonly List<NodeResult> _results;
        private readonly HashSet<string> _replacing = new(StringComparer.Ordinal);
        internal CheckpointJournal(MultiAgentState state)
        {
            _nodes = state.Nodes.ToDictionary(pair => pair.Key, pair => CaptureNode(pair.Key, pair.Value), StringComparer.Ordinal);
            _results = state.Results.ToList();
        }
        internal void Admit(string id, NodeState state)
        { if (state.IsResuming || state.Results.LastOrDefault()?.Status == Status.Interrupted) _replacing.Add(id); }
        internal void Commit(string id, NodeState state, NodeResult result)
        {
            if (_replacing.Remove(id) && _results.LastOrDefault(value => value.NodeId == id) is { } prior) _results.Remove(prior);
            _results.Add(result);
            _nodes[id] = CaptureNode(id, state);
        }
        internal MultiAgentSnapshot Capture(Graph graph, MultiAgentState state, MultiAgentInput input, IReadOnlyList<string> targets, IReadOnlyList<string> transitions)
        {
            var pending = targets.Count > 0 || transitions.Count > 0 || _nodes.Values.Any(node => node.HookPause is not null || node.InterruptedAgentSnapshot is not null || node.InterruptedMultiAgentSnapshot is not null || node.InterruptedNodeSnapshot is not null || node.Status == Status.Interrupted);
            return graph.CreateEnvelope(new(state.StartTime, state.Steps, state.App.GetAll(), graph._options.Nodes.Select(node => _nodes[node.Id]).ToArray(),
                _results.Select(CaptureResult).ToArray(), pending ? new(input.Content, input.IsTextInput) : null, pending ? targets : [], pending ? transitions : []), new());
        }
    }
}
