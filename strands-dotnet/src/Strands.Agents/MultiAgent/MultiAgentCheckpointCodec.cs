using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.MultiAgent;

internal interface IResumableCheckpointValidator
{
    IReadOnlyList<Interrupt> ReadPendingInterrupts(MultiAgentSnapshot snapshot);
}

internal interface IMultiAgentStateCheckpoint
{
    DateTimeOffset StartTime { get; }
    long Steps { get; }
    IReadOnlyDictionary<string, JsonElement> App { get; }
    IReadOnlyList<NodeCheckpoint> Nodes { get; }
    IReadOnlyList<ResultCheckpoint> Results { get; }
}

internal sealed record NodeCheckpoint(string Id, Status Status, bool Terminus, AgentSnapshot? BaselineSnapshot, AgentSnapshot? InterruptedAgentSnapshot, MultiAgentSnapshot? InterruptedMultiAgentSnapshot, IReadOnlyList<Interrupt>? PendingInterrupts = null, NodeHookPause? HookPause = null, IReadOnlyList<InterruptResponse>? DeferredResponses = null, JsonElement? InterruptedNodeSnapshot = null);
internal sealed record ResultCheckpoint(NodeResult Result, string? ErrorType, string? ErrorMessage)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? AgentResult { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NestedResultCheckpoint? MultiAgentResult { get; init; }
}

internal sealed record NestedResultCheckpoint(
    [property: JsonRequired] string Type, [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] Status Status, [property: JsonRequired] IReadOnlyList<ResultCheckpoint> Results,
    [property: JsonRequired] IReadOnlyList<ContentBlock> Content, [property: JsonRequired] TimeSpan Duration,
    string? ErrorType, string? ErrorMessage, [property: JsonRequired] IReadOnlyList<Interrupt> Interrupts);

internal static class MultiAgentCheckpointCodec
{
    private static readonly JsonSerializerOptions CheckpointJson = new(JsonSerializerDefaults.Web);
    internal static NodeCheckpoint CaptureNode(string id, NodeState state) => new(id, state.Status, state.Terminus, state.BaselineSnapshot, state.InterruptedAgentSnapshot, state.InterruptedMultiAgentSnapshot, state.PendingInterrupts, state.HookPause, state.DeferredResponses, state.InterruptedNodeSnapshot?.Clone());
    internal static ResultCheckpoint CaptureResult(NodeResult result) => CaptureResult(result, 0);

    private static ResultCheckpoint CaptureResult(NodeResult result, int depth)
    {
        if (depth >= 64) throw new InvalidDataException("Typed result capture cannot exceed 64 result levels.");
        return new(result, result.Error?.GetType().FullName, result.Error?.Message)
        {
            AgentResult = result.AgentResult is { } agent ? AgentResultJson.ToJson(agent) : null,
            MultiAgentResult = result.MultiAgentResult is { } nested ? new("strands.multi_agent_result", 1, nested.Status,
                nested.Results.Select(child => CaptureResult(child, depth + 1)).ToArray(), nested.Content, nested.Duration,
                nested.Error?.GetType().FullName, nested.Error?.Message, nested.Interrupts) : null
        };
    }

    internal static bool HasTypedResult(ResultCheckpoint result) => result.AgentResult is not null || result.MultiAgentResult is not null;

    // Inspect only the new reserved arms. Opaque message/metadata/JSON content is left to the existing safe/native codecs.
    internal static void ValidateResultFormat(JsonElement state, bool allowTypedResults)
    {
        if (state.ValueKind != JsonValueKind.Object) return;
        foreach (var field in state.EnumerateObject())
            if (field.Name.Equals("results", StringComparison.OrdinalIgnoreCase)) ValidateResultRows(field.Value, allowTypedResults);
    }

    private static void ValidateResultRows(JsonElement rows, bool allowTypedResults)
    {
        if (rows.ValueKind != JsonValueKind.Array) return;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in row.EnumerateObject())
            {
                var isAgent = field.Name.Equals("agentResult", StringComparison.OrdinalIgnoreCase);
                var isNested = field.Name.Equals("multiAgentResult", StringComparison.OrdinalIgnoreCase);
                if (!isAgent && !isNested) continue;
                if (!allowTypedResults) throw new InvalidDataException("Typed node results require orchestration checkpoint version 1.1.");
                if (!seen.Add(field.Name) || seen.Count != 1 || field.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("A checkpoint row must carry exactly one non-null typed result arm when supplied.");
                if (isNested)
                {
                    var nestedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "type", "schemaVersion", "status", "results", "content", "duration", "errorType", "errorMessage", "interrupts" };
                    foreach (var nestedField in field.Value.EnumerateObject())
                        if (reserved.Contains(nestedField.Name))
                        {
                            if (!nestedFields.Add(nestedField.Name)) throw new InvalidDataException("A nested result repeats a reserved field.");
                            if (nestedField.Name.Equals("results", StringComparison.OrdinalIgnoreCase)) ValidateResultRows(nestedField.Value, true);
                        }
                }
            }
        }
    }

    private static NodeResult RestoreResult(ResultCheckpoint stored, bool allowTypedResults)
    {
        if (stored?.Result is not { } result) throw new InvalidDataException("Checkpoint results cannot be null.");
        if (HasTypedResult(stored) && !allowTypedResults) throw new InvalidDataException("Typed node results require orchestration checkpoint version 1.1.");
        if (stored.AgentResult is not null && stored.MultiAgentResult is not null) throw new InvalidDataException("Checkpoint result arms are mutually exclusive.");
        var agent = stored.AgentResult is { } safe ? AgentResultJson.FromJson(safe) : null;
        var nested = stored.MultiAgentResult is { } aggregate ? RestoreNestedResult(aggregate) : null;
        return new(result.NodeId, result.Status, result.Content, result.Usage, result.Duration,
            stored.ErrorType is null ? null : new RestoredNodeException(stored.ErrorType, stored.ErrorMessage ?? string.Empty),
            result.StructuredOutput, result.Interrupts, agent, nested);
    }

    private static MultiAgentResult RestoreNestedResult(NestedResultCheckpoint stored)
    {
        if (stored.Type != "strands.multi_agent_result" || stored.SchemaVersion != 1 || stored.Results is null || stored.Content is null || stored.Interrupts is null)
            throw new InvalidDataException("The nested safe result type, version or fields are invalid.");
        // Node usage stays on each visit; the aggregate constructor sums it once. Safe inner AgentResult usage is deliberately empty.
        return new(stored.Results.Select(result => RestoreResult(result, true)).ToArray(), stored.Content, stored.Duration, stored.Status,
            stored.ErrorType is null ? null : new RestoredNodeException(stored.ErrorType, stored.ErrorMessage ?? string.Empty), stored.Interrupts);
    }

    internal static MultiAgentState Prepare(IMultiAgentStateCheckpoint checkpoint, IReadOnlyDictionary<string, Node> nodes, bool allowTypedResults = false)
    {
        if (checkpoint.Steps < 0 || checkpoint.StartTime == default || checkpoint.Nodes is null || checkpoint.Results is null || checkpoint.App is null)
            throw new InvalidDataException("Checkpoint execution state is incomplete.");
        if (checkpoint.Nodes.Count != nodes.Count || checkpoint.Nodes.Any(node => node is null || !nodes.ContainsKey(node.Id)) || checkpoint.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Count)
            throw new InvalidDataException("Checkpoint node states must exactly match declared graph nodes.");
        var state = new MultiAgentState(nodes.Keys, checkpoint.StartTime) { Steps = checkpoint.Steps };
        state.App.Replace(AgentSnapshot.CloneValues(checkpoint.App, nameof(checkpoint)));
        foreach (var stored in checkpoint.Results)
        {
            if (stored?.Result is not { } result || !nodes.ContainsKey(result.NodeId)) throw new InvalidDataException("Checkpoint results must correspond to declared nodes.");
            if (state.Nodes[result.NodeId].Results.LastOrDefault()?.Status == Status.Interrupted)
                throw new InvalidDataException("A checkpoint cannot contain visits after a node's pending interruption.");
            state.Add(RestoreResult(stored, allowTypedResults));
        }
        if (checkpoint.Steps < state.Results.Count) throw new InvalidDataException("Checkpoint step count cannot be less than the recorded visits.");
        foreach (var row in checkpoint.Nodes)
        {
            var node = state.Nodes[row.Id];
            var knownQuestions = new HashSet<string>(node.Results.LastOrDefault()?.Interrupts.Select(question => question.Id) ?? [], StringComparer.Ordinal);
            if (!Enum.IsDefined(typeof(Status), row.Status) || row.Status == Status.Executing || row.Status != node.Status)
                throw new InvalidDataException("Checkpoint node status must match its recorded results and cannot be executing.");
            if (row.HookPause is { } hookPause)
            {
                if (row.Status is not (Status.Interrupted or Status.Failed or Status.Cancelled) || hookPause.Interrupts.Any(question => question.ToolUseId != row.Id))
                    throw new InvalidDataException("Pending hook questions require a matching incomplete node.");
                node.HookPause = new(hookPause.Interrupts, hookPause.Responses);
                node.PendingInterrupts = node.PendingBodyInterrupts.Where(question => !hookPause.Interrupts.Any(hook => hook.Id == question.Id)).ToArray();
            }
            if (row.BaselineSnapshot is { } baseline) Agent.ValidateSnapshotData(baseline);
            if (row.InterruptedAgentSnapshot is { } paused)
            {
                if (nodes[row.Id] is not AgentNode) throw new InvalidDataException("Only agent nodes can retain agent interruption snapshots.");
                Agent.ValidateSnapshotData(paused);
                if (!paused.Data.TryGetValue("interruptState", out var value) || value.ValueKind == JsonValueKind.Null) throw new InvalidDataException("A paused agent snapshot requires its pending tool batch.");
                var interruptions = value.Deserialize<InterruptState>(CheckpointJson) ?? throw new InvalidDataException("Invalid pending agent batch.");
                if (row.Status is not (Status.Interrupted or Status.Failed or Status.Cancelled)) throw new InvalidDataException("A pending agent execution cannot be complete or unstarted.");
                var answered = new HashSet<string>(interruptions.Responses.Select(response => response.InterruptId), StringComparer.Ordinal);
                var completed = new HashSet<string>(interruptions.CompletedToolResults.Select(result => result.ToolUseId), StringComparer.Ordinal);
                node.PendingInterrupts = NodeResult.CopyInterrupts(interruptions.Interrupts.Where(question => !answered.Contains(question.Id) && (question.IsBatch || !completed.Contains(question.ToolUseId))).ToArray());
                knownQuestions = new HashSet<string>(interruptions.Interrupts.Concat(row.HookPause?.Interrupts ?? []).Select(question => question.Id), StringComparer.Ordinal);
            }
            if (row.Status == Status.Interrupted && nodes[row.Id] is AgentNode && row.InterruptedAgentSnapshot is null && row.HookPause is null)
                throw new InvalidDataException("Interrupted agent nodes require a captured agent snapshot.");
            if (row.InterruptedMultiAgentSnapshot is { } nestedSnapshot)
            {
                if (nodes[row.Id] is not MultiAgentNode { Orchestrator: ICheckpointableMultiAgent nested }) throw new InvalidDataException("Only checkpointable nested nodes can retain orchestration snapshots.");
                nested.ValidateSnapshot(nestedSnapshot);
                if (row.Status is not (Status.Interrupted or Status.Failed or Status.Cancelled)) throw new InvalidDataException("A pending nested execution cannot be complete or unstarted.");
                var pendingRequests = (row.PendingInterrupts ?? node.Results.Last().Interrupts).Where(question => row.HookPause?.Interrupts.Any(hook => hook.Id == question.Id) != true).ToList();
                foreach (var question in node.Results.Last().Interrupts.Where(question => row.DeferredResponses?.Any(response => response.InterruptId == question.Id) == true))
                    if (pendingRequests.All(existing => existing.Id != question.Id)) pendingRequests.Add(question);
                node.PendingInterrupts = NodeResult.CopyInterrupts(pendingRequests);
                if (nested is IResumableCheckpointValidator resumable) node.PendingInterrupts = resumable.ReadPendingInterrupts(nestedSnapshot);
            }
            if (row.Status == Status.Interrupted && nodes[row.Id] is MultiAgentNode && row.InterruptedMultiAgentSnapshot is null && row.HookPause is null)
                throw new InvalidDataException("Interrupted nested nodes require a captured orchestration snapshot.");
            if (row.InterruptedNodeSnapshot is { } nodeSnapshot)
            {
                if (nodes[row.Id] is not INodeCheckpointValidator validator || row.InterruptedAgentSnapshot is not null || row.InterruptedMultiAgentSnapshot is not null)
                    throw new InvalidDataException("A node checkpoint requires its declared compatible node adapter.");
                if (row.Status is not (Status.Interrupted or Status.Failed or Status.Cancelled)) throw new InvalidDataException("A pending node checkpoint cannot be complete or unstarted.");
                node.PendingInterrupts = validator.ReadPendingInterrupts(nodeSnapshot);
                var authoritative = node.PendingInterrupts.Concat(row.HookPause?.Interrupts ?? []).ToDictionary(question => question.Id, StringComparer.Ordinal);
                foreach (var question in (row.PendingInterrupts ?? []).Concat(node.Results.Last().Interrupts))
                    if (!authoritative.TryGetValue(question.Id, out var actual) || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(question), JsonSerializer.SerializeToElement(actual)))
                        throw new InvalidDataException("Remote requests must retain their captured source, correlation, name and reason.");
                knownQuestions = new HashSet<string>(node.PendingInterrupts.Concat(row.HookPause?.Interrupts ?? []).Select(question => question.Id), StringComparer.Ordinal);
            }
            if (row.Status == Status.Interrupted && nodes[row.Id] is INodeCheckpointValidator && row.InterruptedNodeSnapshot is null && row.HookPause is null)
                throw new InvalidDataException("An interrupted checkpointable node requires its pending execution checkpoint.");
            node.Terminus = row.Terminus; node.BaselineSnapshot = row.BaselineSnapshot; node.InterruptedAgentSnapshot = row.InterruptedAgentSnapshot; node.InterruptedMultiAgentSnapshot = row.InterruptedMultiAgentSnapshot; node.InterruptedNodeSnapshot = row.InterruptedNodeSnapshot?.Clone();
            if (row.DeferredResponses is { Count: > 0 } buffered)
            {
                if (!node.HasPendingBody || buffered.Any(response => response is null || !node.PendingBodyInterrupts.Any(question => question.Id == response.InterruptId))
                    || buffered.Select(response => response.InterruptId).Distinct(StringComparer.Ordinal).Count() != buffered.Count)
                    throw new InvalidDataException("Deferred answers must uniquely target pending child requests.");
                node.DeferredResponses = Array.AsReadOnly(buffered.Select(response => new InterruptResponse(response.InterruptId, response.Response)).ToArray());
            }
            if (row.PendingInterrupts is { } expected && !node.PendingInterrupts.Select(question => question.Id).SequenceEqual(expected.Select(question => question.Id)))
                throw new InvalidDataException("Pending requests must match their captured node state.");
            if (row.Status == Status.Interrupted)
            {
                var recorded = new HashSet<string>(node.Results.Last().Interrupts.Select(question => question.Id), StringComparer.Ordinal);
                if (node.PendingInterrupts.Any(question => !recorded.Contains(question.Id)) || recorded.Any(question => !knownQuestions.Contains(question)))
                    throw new InvalidDataException("Node interruption identifiers must match its captured pending state.");
            }
        }
        _ = NodeResult.CopyInterrupts(state.Nodes.Values.SelectMany(node => node.PendingInterrupts).ToArray());
        return state;
    }

}
