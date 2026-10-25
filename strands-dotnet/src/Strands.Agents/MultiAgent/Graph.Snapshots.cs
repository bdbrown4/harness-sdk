using System.Text.Json;

namespace Strands.Agents.MultiAgent;

public sealed partial class Graph : ICheckpointableMultiAgent, IResumableCheckpointValidator
{
    private static readonly JsonSerializerOptions CheckpointJson = new(JsonSerializerDefaults.Web);
    /// <inheritdoc />
    public ValueTask<MultiAgentSnapshot> TakeSnapshotAsync(MultiAgentSnapshotOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 2, 0) != 0) throw new InvalidOperationException("Graph checkpoints require an inactive orchestrator.");
        try { return new(CreateSnapshot(options ?? new())); }
        finally { Volatile.Write(ref _active, 0); }
    }
    /// <inheritdoc />
    public ValueTask LoadSnapshotAsync(MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 2, 0) != 0) throw new InvalidOperationException("Graph checkpoints require an inactive orchestrator.");
        try { ApplySnapshot(snapshot, cancellationToken); return default; }
        finally { Volatile.Write(ref _active, 0); }
    }

    private MultiAgentSnapshot CreateSnapshot(MultiAgentSnapshotOptions options)
    {
        Guard.NotNull(options.AppData, nameof(options));
        var state = _pendingState ?? State;
        if (state is { } current && _options.Nodes.OfType<MultiAgentNode>().Any(node => current.Nodes[node.Id].HasPendingBody && current.Nodes[node.Id].InterruptedMultiAgentSnapshot is null))
            throw new NotSupportedException("Durable nested interruption requires the nested orchestrator's checkpoint capability.");
        return CreateEnvelope(state is null ? null : new StateCheckpoint(state.StartTime, state.Steps, state.App.GetAll(),
            _options.Nodes.Select(node => CaptureNode(node.Id, state.Nodes[node.Id])).ToArray(),
            state.Results.Select(CaptureResult).ToArray(),
            _pendingInput is null ? null : new InputCheckpoint(_pendingInput.Content, _pendingInput.IsTextInput), _pendingTargets, _pendingTransitions), options);
    }

    private MultiAgentSnapshot CreateEnvelope(StateCheckpoint? state, MultiAgentSnapshotOptions options)
    {
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["orchestratorId"] = JsonSerializer.SerializeToElement(Id),
            ["topology"] = TopologySignature(),
            ["state"] = JsonSerializer.SerializeToElement(state, CheckpointJson)
        };
        return new(data, options.AppData, schemaVersion: state?.Results.Any(MultiAgentCheckpointCodec.HasTypedResult) == true ? "1.1" : "1.0");
    }

    private static NodeCheckpoint CaptureNode(string id, NodeState state) => MultiAgentCheckpointCodec.CaptureNode(id, state);
    private static ResultCheckpoint CaptureResult(NodeResult result) => MultiAgentCheckpointCodec.CaptureResult(result);

    private void ApplySnapshot(MultiAgentSnapshot snapshot, CancellationToken cancellationToken)
        => ApplySnapshot(PrepareGraphSnapshot(snapshot), cancellationToken);

    private void ApplySnapshot(PreparedCheckpoint prepared, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = prepared.State;
        _pendingState = prepared.Input is null ? null : prepared.State;
        _pendingInput = prepared.Input;
        _pendingTargets = prepared.Targets;
        _pendingTransitions = prepared.Transitions;
        _snapshotLoaded = true;
    }

    /// <inheritdoc />
    public void ValidateSnapshot(MultiAgentSnapshot snapshot)
    { Guard.NotNull(snapshot, nameof(snapshot)); _ = PrepareGraphSnapshot(snapshot); }

    IReadOnlyList<Interrupt> IResumableCheckpointValidator.ReadPendingInterrupts(MultiAgentSnapshot snapshot)
    {
        var prepared = PrepareGraphSnapshot(snapshot);
        if (prepared.Input is null) throw new InvalidDataException("A pending nested snapshot must contain a resumable execution.");
        return NodeResult.CopyInterrupts(prepared.State!.Nodes.Values.SelectMany(value => value.PendingInterrupts).ToArray());
    }

    private PreparedCheckpoint PrepareGraphSnapshot(MultiAgentSnapshot snapshot)
    {
        try
        {
            if (snapshot.Scope != "multiAgent" || snapshot.SchemaVersion is not ("1.0" or "1.1")) throw new InvalidDataException("Unsupported orchestration checkpoint scope or version.");
            if (!snapshot.Data.TryGetValue("orchestratorId", out var id) || id.GetString() != Id) throw new InvalidDataException("Orchestration checkpoint identity does not match this graph.");
            if (!snapshot.Data.TryGetValue("topology", out var topology) || !JsonElement.DeepEquals(topology, TopologySignature())) throw new InvalidDataException("Orchestration checkpoint topology or compatibility policies do not match this graph.");
            if (!snapshot.Data.TryGetValue("state", out var stateData)) throw new InvalidDataException("Orchestration checkpoint state is missing.");
            MultiAgentState? prepared = null;
            MultiAgentInput? pending = null;
            IReadOnlyList<string> pendingTargets = [];
            IReadOnlyList<string> pendingTransitions = [];
            if (stateData.ValueKind != JsonValueKind.Null)
            {
                MultiAgentCheckpointCodec.ValidateResultFormat(stateData, snapshot.SchemaVersion == "1.1");
                var checkpoint = stateData.Deserialize<StateCheckpoint>(CheckpointJson) ?? throw new InvalidDataException("Orchestration checkpoint state must be an object or null.");
                prepared = PrepareState(checkpoint, snapshot.SchemaVersion == "1.1");
                if (checkpoint.PendingTargets is null || checkpoint.PendingTargets.Any(target => target is null || !Nodes.ContainsKey(target)) || checkpoint.PendingTargets.Distinct(StringComparer.Ordinal).Count() != checkpoint.PendingTargets.Count)
                    throw new InvalidDataException("Checkpoint ready targets must be distinct declared node identifiers.");
                var transitions = checkpoint.PendingTransitions ?? [];
                if (transitions.Any(source => source is null || !Nodes.ContainsKey(source) || prepared.Nodes[source].Status != Status.Completed) || transitions.Distinct(StringComparer.Ordinal).Count() != transitions.Count)
                    throw new InvalidDataException("Checkpoint pending transitions must be distinct completed declared nodes.");
                var interrupted = prepared.Nodes.Values.Any(node => node.HasPendingExecution) || checkpoint.PendingTargets.Count > 0 || transitions.Count > 0;
                if (interrupted)
                {
                    var original = checkpoint.PendingInput ?? throw new InvalidDataException("An interrupted checkpoint requires its original typed input.");
                    if (original.Content is null || original.Content.Any(block => block is null)) throw new InvalidDataException("Checkpoint input contains invalid content.");
                    pending = original.IsTextInput
                        ? original.Content.Count == 1 && original.Content[0] is TextBlock text ? new(text.Text) : throw new InvalidDataException("Text checkpoint input must contain exactly one text block.")
                        : new(original.Content);
                    pendingTargets = Array.AsReadOnly(checkpoint.PendingTargets.ToArray());
                    pendingTransitions = Array.AsReadOnly(transitions.ToArray());
                }
                else if (checkpoint.PendingInput is not null || checkpoint.PendingTargets is not { Count: 0 }) throw new InvalidDataException("A completed checkpoint cannot retain pending input or ready targets.");
            }
            return new(prepared, pending, pendingTargets, pendingTransitions);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("Orchestration checkpoint fields are malformed.", error); }
    }

    private MultiAgentState PrepareState(StateCheckpoint checkpoint, bool allowTypedResults) => MultiAgentCheckpointCodec.Prepare(checkpoint, Nodes, allowTypedResults);

    private JsonElement TopologySignature() => JsonSerializer.SerializeToElement(new
    {
        nodes = _options.Nodes.Select(node => new { node.Id, node.Type, agentContext = node is AgentNode agent ? agent.Options.ContextPolicy ?? _options.AgentContext : (AgentContextPolicy?)null }).ToArray(),
        edges = _options.Edges.Select(edge => new { edge.Source, edge.Target, conditional = edge.Condition is not null }).ToArray(),
        sources = _sources.Select(node => node.Id).ToArray(),
        _options.Dependencies,
        _options.Scheduling,
        _options.NodeFailure,
        _options.LimitOutcome,
        _options.InputFormat
    }, CheckpointJson);

    private sealed record StateCheckpoint(DateTimeOffset StartTime, long Steps, IReadOnlyDictionary<string, JsonElement> App, IReadOnlyList<NodeCheckpoint> Nodes,
        IReadOnlyList<ResultCheckpoint> Results, InputCheckpoint? PendingInput, IReadOnlyList<string> PendingTargets, IReadOnlyList<string>? PendingTransitions = null) : IMultiAgentStateCheckpoint;
    private sealed record InputCheckpoint(IReadOnlyList<ContentBlock> Content, bool IsTextInput);
    private sealed record PreparedCheckpoint(MultiAgentState? State, MultiAgentInput? Input, IReadOnlyList<string> Targets, IReadOnlyList<string> Transitions);
}

/// <summary>A portable failure restored from a durable node result. The original CLR exception instance is process-local.</summary>
public sealed class RestoredNodeException : Exception
{
    /// <summary>Restores a failure's original CLR type name and message without constructing arbitrary exception types.</summary>
    public RestoredNodeException(string originalTypeName, string message) : base(message) => OriginalTypeName = originalTypeName;
    /// <summary>The original exception's CLR type name.</summary>
    public string OriginalTypeName { get; }
}
