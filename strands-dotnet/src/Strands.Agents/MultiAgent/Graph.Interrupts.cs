namespace Strands.Agents.MultiAgent;

public sealed partial class Graph : IInterruptibleMultiAgent
{
    private MultiAgentState? _pendingState;
    private MultiAgentInput? _pendingInput;
    private IReadOnlyList<string> _pendingTargets = [];
    private IReadOnlyList<string> _pendingTransitions = [];
    /// <inheritdoc />
    public bool CanResume => _pendingState is not null;
    /// <inheritdoc />
    public IReadOnlyList<Interrupt> PendingInterrupts => NodeResult.CopyInterrupts(_pendingState?.Nodes.Values.SelectMany(node => node.PendingInterrupts).ToArray() ?? []);

    private PreparedRun PrepareRun(MultiAgentInput input, MultiAgentState? freshState = null)
    {
        if (!input.IsResume)
        {
            if (_pendingState is { } restored)
            {
                if (restored.Nodes.Values.Any(node => node.HasPendingExecution)) throw new ArgumentException("A paused graph requires interrupt responses before a new task.", nameof(input));
                var content = _options.RestoredInput == GraphRestoredInputPolicy.Incoming ? input : _pendingInput ?? throw new InvalidOperationException("The restored graph is missing its original typed input.");
                MarkResuming(restored, _pendingTargets);
                return new(restored, content, _pendingTargets.Select(id => Nodes[id]).ToArray(), new Dictionary<string, IReadOnlyList<InterruptResponse>>(StringComparer.Ordinal), Transitions: _pendingTransitions);
            }
            return new(freshState ?? new(Nodes.Keys), input, _sources, new Dictionary<string, IReadOnlyList<InterruptResponse>>(StringComparer.Ordinal));
        }
        var state = _pendingState ?? throw new ArgumentException("This graph has no pending interrupts.", nameof(input));
        var groups = new Dictionary<string, List<InterruptResponse>>(StringComparer.Ordinal);
        foreach (var response in input.InterruptResponses)
        {
            var matches = state.Nodes.Where(pair => pair.Value.PendingInterrupts.Any(question => question.Id == response.InterruptId)).Select(pair => pair.Key).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Each response must uniquely identify an outstanding node interrupt.", nameof(input));
            if (!groups.TryGetValue(matches[0], out var group)) groups[matches[0]] = group = [];
            group.Add(response);
        }
        if (input.InterruptResponses.Count == 0)
            foreach (var pair in state.Nodes.Where(pair => pair.Value.HasPendingExecution)) groups[pair.Key] = [];
        foreach (var pair in groups)
            if (Nodes[pair.Key] is INodeCheckpointValidator validator && state.Nodes[pair.Key].InterruptedNodeSnapshot is { } checkpoint)
            {
                var bodyIds = new HashSet<string>(state.Nodes[pair.Key].PendingBodyInterrupts.Select(question => question.Id), StringComparer.Ordinal);
                validator.ValidateResponses(checkpoint, pair.Value.Where(response => bodyIds.Contains(response.InterruptId)).ToArray());
            }
        var acceptedHooks = false;
        foreach (var pair in groups)
            if (state.Nodes[pair.Key].HookPause is { } hooks)
            {
                var ids = new HashSet<string>(hooks.Interrupts.Select(question => question.Id), StringComparer.Ordinal);
                var answers = pair.Value.Where(response => ids.Contains(response.InterruptId)).ToArray();
                if (answers.Length > 0)
                {
                    state.Nodes[pair.Key].HookPause = new(hooks.Interrupts, hooks.Responses.Concat(answers).ToArray());
                    pair.Value.RemoveAll(response => ids.Contains(response.InterruptId));
                    acceptedHooks = true;
                }
            }
        foreach (var pair in groups)
            if (pair.Value.Count > 0)
            {
                var node = state.Nodes[pair.Key];
                node.DeferredResponses = Array.AsReadOnly(node.DeferredResponses.Concat(pair.Value).Select(response => new InterruptResponse(response.InterruptId, response.Response)).ToArray());
                acceptedHooks = true;
            }
        var ready = groups.Keys.Concat(_pendingTargets).Concat(state.Nodes.Where(pair => pair.Value.DeferredResponses.Count > 0).Select(pair => pair.Key)).Distinct(StringComparer.Ordinal).ToArray();
        MarkResuming(state, ready);
        return new(state, _pendingInput ?? throw new InvalidOperationException("The paused graph is missing its original typed input."), ready.Select(id => Nodes[id]).ToArray(), ready.ToDictionary(id => id, id => state.Nodes[id].DeferredResponses, StringComparer.Ordinal), acceptedHooks, _pendingTransitions);
    }

    private static void MarkResuming(MultiAgentState state, IEnumerable<string> targets)
    {
        foreach (var id in targets)
            if (state.Nodes[id].HasPendingExecution || state.Nodes[id].Results.LastOrDefault()?.Status is Status.Failed or Status.Cancelled)
                state.Nodes[id].IsResuming = true;
    }

    private void RetainPending(MultiAgentState state, MultiAgentInput original, IReadOnlyList<string> pendingTargets, IReadOnlyList<string> transitions)
    {
        _pendingState = state.Nodes.Values.Any(node => node.HasPendingExecution) ? state : null;
        _pendingInput = _pendingState is null ? null : original;
        _pendingTargets = _pendingState is null ? [] : Array.AsReadOnly(pendingTargets.ToArray());
        _pendingTransitions = _pendingState is null ? [] : Array.AsReadOnly(transitions.ToArray());
    }

    private sealed record PreparedRun(MultiAgentState State, MultiAgentInput Input, IReadOnlyList<Node> Ready, IReadOnlyDictionary<string, IReadOnlyList<InterruptResponse>> Responses, bool AcceptedHookResponses = false, IReadOnlyList<string>? Transitions = null);
}
