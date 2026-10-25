using System.Text.Json;

namespace Strands.Agents;

public sealed partial class Agent
{
    private object? _goalInvocationIdentity;

    private void BeginGoalInvocationIdentity() => _goalInvocationIdentity = new object();

    internal object GoalInvocationIdentity => _goalInvocationIdentity ?? throw new InvalidOperationException("No agent invocation has been admitted.");

    internal AgentSnapshot CaptureGoalSnapshot(BeforeModelCallEvent boundary)
    {
        Guard.NotNull(boundary, nameof(boundary));
        lock (_initializationGate)
        {
            ValidateGoalSnapshotBoundary(boundary.Agent);
            var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["messages"] = JsonSerializer.SerializeToElement(Messages, SnapshotJson),
                ["systemPrompt"] = SystemPromptContent is { } content ? JsonSerializer.SerializeToElement(content, SnapshotJson) : JsonSerializer.SerializeToElement(SystemPrompt, SnapshotJson),
                ["modelState"] = JsonSerializer.SerializeToElement(ModelState.GetAll(), SnapshotJson),
                ["interruptState"] = JsonSerializer.SerializeToElement(InterruptState, SnapshotJson)
            };
            if (_options.ModelHistoryBehavior != ModelHistoryBehavior.Native)
                data.Add("historyProfile", JsonSerializer.SerializeToElement(_options.ModelHistoryBehavior));
            if (UsesNativeStatefulHistory) data.Add("modelHistoryCount", JsonSerializer.SerializeToElement(_modelHistoryCount));
            var inputReceipt = UsesNativeStatefulHistory ? _modelInputReceipt : null;
            if (inputReceipt is not null) data["modelInputReceipt"] = JsonSerializer.SerializeToElement(inputReceipt, SnapshotJson);
            var captured = new AgentSnapshot(data, schemaVersion: inputReceipt is null ? "1.0" : "2.0");
            if (inputReceipt is not null) _ = PrepareSnapshot(captured);
            return captured;
        }
    }

    internal void RestoreGoalSnapshot(AfterInvocationEvent boundary, AgentSnapshot snapshot)
    {
        Guard.NotNull(boundary, nameof(boundary));
        Guard.NotNull(snapshot, nameof(snapshot));
        var prepared = PrepareSnapshot(snapshot);
        ValidateStoredHistoryProfile(prepared.HistoryProfile);
        lock (_initializationGate)
        {
            ValidateGoalSnapshotBoundary(boundary.Agent);
            if (boundary.Error is not null || boundary.Result?.StopReason is StopReasons.Interrupt or StopReasons.Cancelled)
                throw new InvalidOperationException("Only a completed, uninterrupted goal attempt can be rewound.");
            _backgroundTasks?.AssertCanLoadSnapshot();
            ValidateSnapshotProviderState(prepared);
            var effectiveInterrupt = prepared.HasInterruptState ? prepared.InterruptState : InterruptState;
            effectiveInterrupt?.ValidateHistory(prepared.Messages ?? Messages, allowIndependent: _options.ModelHistoryBehavior != ModelHistoryBehavior.Native, independentOwnerProfile: _options.ModelHistoryBehavior);
            if (prepared.Messages is { } messages)
            {
                lock (_historyGate)
                {
                    _messages.Clear();
                    _messages.AddRange(messages);
                    _modelHistoryCount = UsesNativeStatefulHistory ? prepared.ModelHistoryCount ?? 0 : 0;
                    _modelInputReceipt = UsesNativeStatefulHistory ? prepared.ModelInputReceipt : null;
                }
            }
            if (prepared.ModelState is { } modelState) ModelState.Replace(modelState);
            if (prepared.HasSystemPrompt) { SystemPrompt = prepared.SystemPrompt; SystemPromptContent = prepared.SystemPromptContent; }
            if (prepared.HasInterruptState) lock (_historyGate) _interruptState = prepared.InterruptState?.Copy();
        }
    }

    private void ValidateGoalSnapshotBoundary(Agent owner)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(owner, this) || Volatile.Read(ref _invoking) == 0)
            throw new InvalidOperationException("Goal checkpoints require this agent's active invocation boundary.");
    }
}
