using System.Text.Json;

namespace Strands.Agents.MultiAgent;

public sealed partial class Swarm
{
    private sealed class ToolTurn
    {
        private readonly object _gate = new();
        private readonly List<ToolBatch> _batches = [];
        private readonly Dictionary<string, ToolBatch> _runtimeBatches = new(StringComparer.Ordinal);
        private ToolBatch? _current;
        private string? _pausedBatchId;
        private bool _resumeExpected;

        internal void Record(Agent agent, ToolExecutionCheckpoint execution, JsonElement decision)
        { lock (_gate) GetBatch(agent).Decisions[execution.ToolUse.ToolUseId] = new(execution, decision.Clone()); }

        internal void Observe(object? value)
        {
            lock (_gate)
            {
                if (value is MessageAddedEvent { Message.Role: Role.Assistant } added && added.Message.Content.OfType<ToolUseBlock>().Any())
                { _current = NewBatch(added.Message.Content.OfType<ToolUseBlock>().ToArray()); _resumeExpected = false; }
                if (value is ToolResultEvent result)
                    GetBatch(result.Agent).Results[result.Result.ToolUseId] = (ToolResultBlock)Message.CloneBlock(result.Result);
            }
        }

        private ToolBatch NewBatch(IReadOnlyList<ToolUseBlock> calls)
        {
            var batch = new ToolBatch(Guid.NewGuid().ToString("N"), calls);
            _batches.Add(batch); return batch;
        }

        private ToolBatch GetBatch(Agent agent)
        {
            var runtime = agent.ActiveInterruptBatchId ?? throw new InvalidOperationException("Swarm coordination requires an active tool batch.");
            if (_runtimeBatches.TryGetValue(runtime, out var existing)) return existing;
            var calls = agent.Messages.Last().Content.OfType<ToolUseBlock>().ToArray();
            var batch = _current;
            if (batch is null || !SameCalls(batch.Calls, calls) || batch.RuntimeId is not null && !_resumeExpected) batch = _current = NewBatch(calls);
            batch.RuntimeId = runtime; _resumeExpected = false; _runtimeBatches.Add(runtime, batch);
            return batch;
        }

        internal void Pause(AgentSnapshot snapshot)
        {
            var pending = ReadPending(snapshot);
            var calls = pending.AssistantMessage.Content.OfType<ToolUseBlock>().ToArray();
            lock (_gate)
            {
                if (_current is null || !SameCalls(_current.Calls, calls) || _current.Results.Count == _current.Calls.Count)
                    _current = NewBatch(calls);
                _pausedBatchId = _current.Id; _resumeExpected = true;
                _current.Results.Clear(); foreach (var result in pending.CompletedToolResults) _current.Results.Add(result.ToolUseId, (ToolResultBlock)Message.CloneBlock(result));
                foreach (var id in _current.Decisions.Keys.Where(id => !pending.ToolExecutions.Any(execution => execution.ToolUse.ToolUseId == id)).ToArray()) _current.Decisions.Remove(id);
            }
        }

        internal ToolTurnCheckpoint Capture()
        {
            lock (_gate) return new(_batches.Where(batch => batch.Decisions.Count > 0 || batch.Id == _pausedBatchId)
                .Select(batch => new ToolBatchCheckpoint(batch.Id, batch.Calls, batch.Results.Values.ToArray(), batch.Decisions.Values.ToArray())).ToArray(), _pausedBatchId);
        }

        internal static ToolTurn Restore(ToolTurnCheckpoint checkpoint, IReadOnlyDictionary<string, AgentNode> nodes, AgentSnapshot paused)
        {
            if (checkpoint.Batches is null || string.IsNullOrWhiteSpace(checkpoint.PausedBatchId) || checkpoint.Batches.Any(batch => batch is null || string.IsNullOrWhiteSpace(batch.Id))
                || checkpoint.Batches.Select(batch => batch.Id).Distinct(StringComparer.Ordinal).Count() != checkpoint.Batches.Count)
                throw new InvalidDataException("A stored handoff turn requires distinct batches and its paused batch identity.");
            var pending = ReadPending(paused);
            var turn = new ToolTurn();
            foreach (var row in checkpoint.Batches)
            {
                if (row.Calls is null || row.Calls.Count == 0 || row.Calls.Any(call => call is null || string.IsNullOrWhiteSpace(call.ToolUseId))
                    || row.Calls.Select(call => call.ToolUseId).Distinct(StringComparer.Ordinal).Count() != row.Calls.Count || row.Results is null || row.Decisions is null)
                    throw new InvalidDataException("A stored tool batch requires distinct correlated calls, results and handoff records.");
                var batch = new ToolBatch(row.Id, row.Calls);
                foreach (var result in row.Results)
                {
                    if (result is null || !Enum.IsDefined(typeof(ToolResultStatus), result.Status) || !batch.Calls.Any(call => call.ToolUseId == result.ToolUseId) || batch.Results.ContainsKey(result.ToolUseId))
                        throw new InvalidDataException("Stored coordination results must uniquely correspond to their original batch.");
                    batch.Results.Add(result.ToolUseId, (ToolResultBlock)Message.CloneBlock(result));
                }
                foreach (var decision in row.Decisions)
                {
                    ValidateDecision(decision, batch, nodes);
                    var id = decision.Execution.ToolUse.ToolUseId;
                    if (batch.Decisions.ContainsKey(id)) throw new InvalidDataException("A coordination call can record only one handoff.");
                    batch.Decisions.Add(id, decision with { Decision = decision.Decision.Clone() });
                }
                if (row.Id == checkpoint.PausedBatchId)
                {
                    ValidatePausedBatch(batch, pending);
                    turn._current = batch; turn._pausedBatchId = batch.Id; turn._resumeExpected = true;
                }
                else if (batch.Results.Count != batch.Calls.Count) throw new InvalidDataException("Earlier tool batches must be complete before the paused batch.");
                turn._batches.Add(batch);
            }
            if (turn._current is null || turn._batches.Last() != turn._current) throw new InvalidDataException("The paused tool batch must be the last captured batch.");
            return turn;
        }

        private static void ValidateDecision(HandoffDecisionCheckpoint decision, ToolBatch batch, IReadOnlyDictionary<string, AgentNode> nodes)
        {
            if (decision?.Execution is not { } execution || execution.ToolName != HandoffTool.Name || execution.Result.Status != ToolResultStatus.Success
                || !batch.Calls.Any(call => call.ToolUseId == execution.ToolUse.ToolUseId)) throw new InvalidDataException("Stored handoffs require a successful correlated coordination body.");
            if (decision.ContextApplied && (!batch.Results.TryGetValue(execution.ToolUse.ToolUseId, out var accepted) || accepted.Status != ToolResultStatus.Success))
                throw new InvalidDataException("Only normalized successful handoff results can already have contributed context.");
            var value = decision.Decision; var input = execution.ToolUse.Input;
            ValidateHandoff(value, nodes, null);
            if (value.ValueKind != JsonValueKind.Object || input.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("agentId", out var target) || target.ValueKind != JsonValueKind.String || !nodes.ContainsKey(target.GetString()!)
                || !value.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String
                || !input.TryGetProperty("agent_name", out var inputTarget) || inputTarget.ValueKind != JsonValueKind.String || inputTarget.GetString() != target.GetString()
                || !input.TryGetProperty("message", out var inputMessage) || inputMessage.ValueKind != JsonValueKind.String || inputMessage.GetString() != message.GetString()
                || input.TryGetProperty("context", out var inputContext) && inputContext.ValueKind == JsonValueKind.Object && (!value.TryGetProperty("context", out var outputContext) || !JsonElement.DeepEquals(inputContext, outputContext))
                || value.TryGetProperty("context", out var context) && (context.ValueKind != JsonValueKind.Object || context.EnumerateObject().Any(item => string.IsNullOrWhiteSpace(item.Name)) || !input.TryGetProperty("context", out var contributed) || contributed.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException("Stored handoffs must match their effective coordination input and declared target.");
        }

        private static void ValidatePausedBatch(ToolBatch batch, InterruptState pending)
        {
            if (!SameCalls(batch.Calls, pending.AssistantMessage.Content.OfType<ToolUseBlock>().ToArray()) || batch.Results.Count != pending.CompletedToolResults.Count
                || batch.Results.Values.Any(result => !pending.CompletedToolResults.Any(accepted => accepted.ToolUseId == result.ToolUseId && SameJson(accepted, result))))
                throw new InvalidDataException("The current coordination receipt must match the authoritative paused calls and accepted results.");
            foreach (var decision in batch.Decisions.Values)
                if (!pending.ToolExecutions.Any(execution => execution.ToolUse.ToolUseId == decision.Execution.ToolUse.ToolUseId && SameJson(execution, decision.Execution)))
                    throw new InvalidDataException("The current coordination body must match the authoritative paused execution ledger.");
        }

        internal JsonElement? Commit(SharedContext shared, string contributor)
        {
            lock (_gate)
            {
                JsonElement? last = null;
                foreach (var batch in _batches)
                    foreach (var call in batch.Calls)
                        if (batch.Decisions.TryGetValue(call.ToolUseId, out var handoff) && batch.Results.TryGetValue(call.ToolUseId, out var result) && result.Status == ToolResultStatus.Success)
                        {
                            var decision = handoff.Decision;
                            if (!handoff.ContextApplied)
                            {
                                if (decision.TryGetProperty("context", out var context)) foreach (var property in context.EnumerateObject()) shared.AddContext(contributor, property.Name, property.Value);
                                batch.Decisions[call.ToolUseId] = handoff with { ContextApplied = true };
                            }
                            last = decision.Clone();
                        }
                return last;
            }
        }

        private static InterruptState ReadPending(AgentSnapshot snapshot) => snapshot.Data.TryGetValue("interruptState", out var value)
            ? value.Deserialize<InterruptState>(CheckpointJson) ?? throw new InvalidDataException("A stored coordination turn requires an agent pause.")
            : throw new InvalidDataException("A stored coordination turn requires an agent pause.");
        private static bool SameCalls(IReadOnlyList<ToolUseBlock> left, IReadOnlyList<ToolUseBlock> right) => SameJson(left, right);
        private static bool SameJson<T>(T left, T right) => JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left, CheckpointJson), JsonSerializer.SerializeToElement(right, CheckpointJson));

        private sealed class ToolBatch(string id, IReadOnlyList<ToolUseBlock> calls)
        {
            internal string Id { get; } = id;
            internal IReadOnlyList<ToolUseBlock> Calls { get; } = Array.AsReadOnly(calls.Select(call => (ToolUseBlock)Message.CloneBlock(call)).ToArray());
            internal string? RuntimeId { get; set; }
            internal Dictionary<string, ToolResultBlock> Results { get; } = new(StringComparer.Ordinal);
            internal Dictionary<string, HandoffDecisionCheckpoint> Decisions { get; } = new(StringComparer.Ordinal);
        }
    }

    private sealed record HandoffDecisionCheckpoint(ToolExecutionCheckpoint Execution, JsonElement Decision, bool ContextApplied = false);
    private sealed record ToolBatchCheckpoint(string Id, IReadOnlyList<ToolUseBlock> Calls, IReadOnlyList<ToolResultBlock> Results, IReadOnlyList<HandoffDecisionCheckpoint> Decisions);
    private sealed record ToolTurnCheckpoint(IReadOnlyList<ToolBatchCheckpoint> Batches, string? PausedBatchId);
}
