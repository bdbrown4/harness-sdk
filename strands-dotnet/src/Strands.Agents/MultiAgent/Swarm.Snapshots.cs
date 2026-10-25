using System.Text.Json;

namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm : ICheckpointableMultiAgent, IResumableCheckpointValidator
{
    private static readonly JsonSerializerOptions CheckpointJson = new(JsonSerializerDefaults.Web);
    private long _elapsedExecutionTicks = 0;
    /// <inheritdoc />
    public ValueTask<MultiAgentSnapshot> TakeSnapshotAsync(MultiAgentSnapshotOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 2, 0) != 0) throw new InvalidOperationException("Swarm snapshots require an inactive orchestrator.");
        try { return new(CreateSnapshot(options ?? new())); }
        finally { Volatile.Write(ref _active, 0); }
    }
    /// <inheritdoc />
    public ValueTask LoadSnapshotAsync(MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(snapshot, nameof(snapshot)); cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 2, 0) != 0) throw new InvalidOperationException("Swarm snapshots require an inactive orchestrator.");
        try { ApplySnapshot(PrepareSnapshot(snapshot), cancellationToken); return default; }
        finally { Volatile.Write(ref _active, 0); }
    }
    /// <inheritdoc />
    public void ValidateSnapshot(MultiAgentSnapshot snapshot)
    { Guard.NotNull(snapshot, nameof(snapshot)); _ = PrepareSnapshot(snapshot); }

    IReadOnlyList<Interrupt> IResumableCheckpointValidator.ReadPendingInterrupts(MultiAgentSnapshot snapshot)
    {
        var prepared = PrepareSnapshot(snapshot);
        if (prepared.PendingInput is null) throw new InvalidDataException("A paused nested swarm must contain a resumable execution.");
        return NodeResult.CopyInterrupts(prepared.State!.Nodes.Values.SelectMany(node => node.PendingInterrupts).ToArray());
    }

    private MultiAgentSnapshot CreateSnapshot(MultiAgentSnapshotOptions options)
    {
        var state = _pendingState ?? State;
        return new(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["orchestratorId"] = JsonSerializer.SerializeToElement(Id),
            ["topology"] = TopologySignature(),
            ["baselines"] = JsonSerializer.SerializeToElement(_baselines, CheckpointJson),
            ["sharedContext"] = JsonSerializer.SerializeToElement(SharedContext.GetAll(), CheckpointJson),
            ["state"] = JsonSerializer.SerializeToElement(state is null ? null : new SwarmCheckpoint(state.StartTime, state.Steps, state.App.GetAll(),
                _options.Nodes.Select(node => MultiAgentCheckpointCodec.CaptureNode(node.Id, state.Nodes[node.Id])).ToArray(),
                state.Results.Select(MultiAgentCheckpointCodec.CaptureResult).ToArray(),
                _pendingState is null ? null : CaptureInput(_pendingInput!), _pendingState is null ? null : _pendingNode,
                _pendingState is null ? null : CaptureInput(_pendingNodeInput!),
                _toolTurns.ToDictionary(pair => pair.Key, pair => pair.Value.Capture(), StringComparer.Ordinal), CurrentExecutionTicks()), CheckpointJson)
        }, options.AppData);
    }

    private JsonElement TopologySignature() => JsonSerializer.SerializeToElement(new
    {
        kind = "swarm",
        nodes = _options.Nodes.Select(node => new { node.Id, context = node.Options.ContextPolicy ?? _options.AgentContext }).ToArray(),
        start = _start.Id,
        _options.Routing
    }, CheckpointJson);

    private PreparedSnapshot PrepareSnapshot(MultiAgentSnapshot snapshot)
    {
        try
        {
            if (snapshot.Scope != "multiAgent" || snapshot.SchemaVersion != "1.0") throw new InvalidDataException("Unsupported orchestration snapshot scope or version.");
            if (!snapshot.Data.TryGetValue("orchestratorId", out var id) || id.GetString() != Id || !snapshot.Data.TryGetValue("topology", out var topology) || !JsonElement.DeepEquals(topology, TopologySignature()))
                throw new InvalidDataException("Swarm snapshot identity, nodes or routing policies do not match.");
            if (!snapshot.Data.TryGetValue("baselines", out var baselineData) || !snapshot.Data.TryGetValue("sharedContext", out var contextData) || !snapshot.Data.TryGetValue("state", out var stateData))
                throw new InvalidDataException("Swarm snapshot fields are missing.");
            var baselines = baselineData.Deserialize<Dictionary<string, AgentSnapshot>>(CheckpointJson) ?? throw new InvalidDataException("Swarm baselines must be an object.");
            foreach (var pair in baselines)
            {
                if (!Nodes.TryGetValue(pair.Key, out var node) || (node.Options.ContextPolicy ?? _options.AgentContext) != AgentContextPolicy.ResetOnRevisit || pair.Value is null)
                    throw new InvalidDataException("A swarm baseline must belong to a declared resetting agent.");
                Agent.ValidateSnapshotData(pair.Value);
            }
            var shared = contextData.Deserialize<Dictionary<string, IReadOnlyDictionary<string, JsonElement>>>(CheckpointJson) ?? throw new InvalidDataException("Shared knowledge must be an object.");
            var validatedKnowledge = new SharedContext(); validatedKnowledge.Replace(shared);
            MultiAgentState? state = null;
            MultiAgentInput? original = null, nodeInput = null;
            string? nodeId = null;
            var turns = new Dictionary<string, ToolTurn>(StringComparer.Ordinal);
            long elapsedTicks = 0;
            if (stateData.ValueKind != JsonValueKind.Null)
            {
                var checkpoint = stateData.Deserialize<SwarmCheckpoint>(CheckpointJson) ?? throw new InvalidDataException("Swarm state must be an object or null.");
                elapsedTicks = checkpoint.ElapsedExecutionTicks;
                if (elapsedTicks < 0) throw new InvalidDataException("Accumulated swarm execution time cannot be negative.");
                state = MultiAgentCheckpointCodec.Prepare(checkpoint, Nodes.ToDictionary(pair => pair.Key, pair => (Node)pair.Value, StringComparer.Ordinal));
                foreach (var node in _options.Nodes.Where(node => (node.Options.ContextPolicy ?? _options.AgentContext) == AgentContextPolicy.ResetOnRevisit))
                    if (!baselines.TryGetValue(node.Id, out var baseline) || state.Nodes[node.Id].BaselineSnapshot is not { } stored
                        || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(baseline, CheckpointJson), JsonSerializer.SerializeToElement(stored, CheckpointJson)))
                        throw new InvalidDataException("An initialized resetting swarm must retain each original construction baseline consistently.");
                foreach (var result in state.Results)
                {
                    if (result.Status == Status.Completed && _options.Routing == SwarmRoutingMode.Structured && result.StructuredOutput is null)
                        throw new InvalidDataException("A completed structured swarm visit requires its routing output.");
                    if (result.StructuredOutput is { } handoff)
                    {
                        if (result.Status != Status.Completed) throw new InvalidDataException("Only completed swarm visits can commit routing output.");
                        ValidateHandoff(handoff, Nodes, _options.Routing == SwarmRoutingMode.Structured ? result.NodeId : null);
                    }
                }
                var paused = state.Nodes.Where(pair => pair.Value.HasPendingExecution).Select(pair => pair.Key).ToArray();
                if (paused.Length > 1) throw new InvalidDataException("A sequential swarm cannot pause multiple agents.");
                if (checkpoint.PendingNode is { } frontier)
                {
                    if (!Nodes.ContainsKey(frontier) || paused.Length == 1 && frontier != paused[0] || checkpoint.PendingInput is null || checkpoint.PendingNodeInput is null) throw new InvalidDataException("An unfinished swarm requires its matching node and original inputs.");
                    nodeId = frontier; original = RestoreInput(checkpoint.PendingInput); nodeInput = RestoreInput(checkpoint.PendingNodeInput);
                }
                else if (paused.Length > 0 || checkpoint.PendingInput is not null || checkpoint.PendingNodeInput is not null)
                    throw new InvalidDataException("A completed swarm cannot retain a paused frontier.");
                if (checkpoint.ToolTurns is null || checkpoint.ToolTurns.Count > (nodeId is null ? 0 : 1)) throw new InvalidDataException("Only the paused tool-mode visit may retain a handoff turn.");
                foreach (var pair in checkpoint.ToolTurns)
                {
                    if (_options.Routing != SwarmRoutingMode.Tool || pair.Key != nodeId || pair.Value is null || !state.Nodes[pair.Key].HasPendingBody) throw new InvalidDataException("Stored handoff work must belong to the paused tool-mode node.");
                    turns.Add(pair.Key, ToolTurn.Restore(pair.Value, Nodes, state.Nodes[pair.Key].InterruptedAgentSnapshot!));
                }
            }
            return new(state, original, nodeId, nodeInput, baselines, validatedKnowledge.GetAll(), turns, elapsedTicks);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Swarm snapshot fields are malformed.", error); }
    }

    private void ApplySnapshot(PreparedSnapshot prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SharedContext.Replace(prepared.Shared);
        State = prepared.State; _pendingState = prepared.PendingInput is null ? null : prepared.State;
        _pendingInput = prepared.PendingInput; _pendingNode = prepared.PendingNode; _pendingNodeInput = prepared.PendingNodeInput;
        _baselines.Clear(); foreach (var pair in prepared.Baselines) _baselines.Add(pair.Key, pair.Value);
        _toolTurns.Clear(); foreach (var pair in prepared.ToolTurns) _toolTurns.Add(pair.Key, pair.Value);
        _snapshotLoaded = true;
        _elapsedExecutionTicks = prepared.ElapsedExecutionTicks;
    }

    private static InputCheckpoint CaptureInput(MultiAgentInput input) => new(input.Content, input.IsTextInput);
    private static void ValidateHandoff(JsonElement value, IReadOnlyDictionary<string, AgentNode> nodes, string? source)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Swarm routing output must be an object.");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => name is not ("agentId" or "message" or "context"))
            || !value.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String
            || value.TryGetProperty("agentId", out var target) && (target.ValueKind != JsonValueKind.String || !nodes.ContainsKey(target.GetString()!) || target.GetString() == source)
            || value.TryGetProperty("context", out var context) && context.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Stored routing output must preserve its message, optional declared target and object context.");
    }
    private static MultiAgentInput RestoreInput(InputCheckpoint input)
    {
        if (input.Content is null || input.Content.Any(block => block is null)) throw new InvalidDataException("Stored input requires defined ordered content.");
        return input.IsTextInput ? input.Content.Count == 1 && input.Content[0] is TextBlock text ? new(text.Text) : throw new InvalidDataException("Text input requires exactly one text block.") : new(input.Content);
    }

    private sealed record SwarmCheckpoint(DateTimeOffset StartTime, long Steps, IReadOnlyDictionary<string, JsonElement> App, IReadOnlyList<NodeCheckpoint> Nodes, IReadOnlyList<ResultCheckpoint> Results,
        InputCheckpoint? PendingInput, string? PendingNode, InputCheckpoint? PendingNodeInput, IReadOnlyDictionary<string, ToolTurnCheckpoint> ToolTurns, long ElapsedExecutionTicks = 0) : IMultiAgentStateCheckpoint;
    private sealed record InputCheckpoint(IReadOnlyList<ContentBlock> Content, bool IsTextInput);
    private sealed record PreparedSnapshot(MultiAgentState? State, MultiAgentInput? PendingInput, string? PendingNode, MultiAgentInput? PendingNodeInput,
        IReadOnlyDictionary<string, AgentSnapshot> Baselines, IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Shared, IReadOnlyDictionary<string, ToolTurn> ToolTurns, long ElapsedExecutionTicks);
}
