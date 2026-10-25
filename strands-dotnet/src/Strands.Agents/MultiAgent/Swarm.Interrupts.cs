namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm : IInterruptibleMultiAgent
{
    private MultiAgentState? _pendingState;
    private MultiAgentInput? _pendingInput;
    private string? _pendingNode;
    private MultiAgentInput? _pendingNodeInput;
    private readonly Dictionary<string, ToolTurn> _toolTurns = new(StringComparer.Ordinal);
    /// <inheritdoc />
    public bool CanResume => _pendingState is not null;
    /// <inheritdoc />
    public IReadOnlyList<Interrupt> PendingInterrupts => NodeResult.CopyInterrupts(_pendingState?.Nodes.Values.SelectMany(node => node.PendingInterrupts).ToArray() ?? []);

    private (MultiAgentState State, MultiAgentInput Original, AgentNode Node, MultiAgentInput NodeInput, bool AcceptedResponses) PrepareRun(MultiAgentInput input, MultiAgentState? freshState = null)
    {
        if (!input.IsResume)
        {
            if (_pendingState is { } restored)
            {
                if (restored.Nodes.Values.Any(node => node.HasPendingExecution)) throw new ArgumentException("A paused swarm requires interrupt responses before a new task.", nameof(input));
                var original = _options.RestoredInput == GraphRestoredInputPolicy.Incoming ? input : _pendingInput!;
                var target = Nodes[_pendingNode!];
                var previous = restored.Results.LastOrDefault();
                var handoff = previous?.Status == Status.Completed && previous.StructuredOutput is { } decision && decision.TryGetProperty("agentId", out var id) && id.GetString() == target.Id ? previous.StructuredOutput : null;
                var nodeInput = _options.Routing == SwarmRoutingMode.Tool ? ToolInput(original, target.Id, restored, handoff?.GetProperty("message").GetString())
                    : handoff is { } committed ? HandoffInput(committed) : _options.RestoredInput == GraphRestoredInputPolicy.Incoming ? input : _pendingNodeInput!;
                restored.Nodes[target.Id].IsResuming = restored.Nodes[target.Id].Results.LastOrDefault()?.Status is Status.Cancelled or Status.Failed;
                return (restored, original, target, nodeInput, false);
            }
            _elapsedExecutionTicks = 0;
            var state = freshState ?? new MultiAgentState(Nodes.Keys);
            return (state, input, _start, _options.Routing == SwarmRoutingMode.Tool ? ToolInput(input, _start.Id, state, null) : input, false);
        }
        var pending = _pendingState ?? throw new ArgumentException("This swarm has no pending execution.", nameof(input));
        var node = pending.Nodes[_pendingNode ?? throw new InvalidOperationException("The paused swarm is missing its active node.")];
        if (input.InterruptResponses.Any(response => !node.PendingInterrupts.Any(question => question.Id == response.InterruptId)))
            throw new ArgumentException("Responses must identify outstanding requests from the paused node.", nameof(input));
        var hooks = node.HookPause;
        var hookIds = new HashSet<string>(hooks?.Interrupts.Select(question => question.Id) ?? [], StringComparer.Ordinal);
        if (hooks is not null) node.HookPause = new(hooks.Interrupts, hooks.Responses.Concat(input.InterruptResponses.Where(response => hookIds.Contains(response.InterruptId))).ToArray());
        node.DeferredResponses = Array.AsReadOnly(node.DeferredResponses.Concat(input.InterruptResponses.Where(response => !hookIds.Contains(response.InterruptId))).ToArray());
        node.IsResuming = node.HasPendingExecution || node.Results.LastOrDefault()?.Status is Status.Cancelled or Status.Failed;
        return (pending, _pendingInput ?? throw new InvalidOperationException("The paused swarm is missing its task."), Nodes[_pendingNode],
            node.HasPendingBody ? MultiAgentInput.Resume(node.DeferredResponses) : _pendingNodeInput ?? throw new InvalidOperationException("The paused swarm is missing its node input."), input.InterruptResponses.Count > 0);
    }
}
