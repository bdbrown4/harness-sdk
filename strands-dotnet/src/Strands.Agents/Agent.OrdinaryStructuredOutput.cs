using System.Text.Json;

namespace Strands.Agents;

public sealed partial class Agent
{
    private static StructuredOutputOptions<JsonElement> CopyStructuredOutputOptions(StructuredOutputOptions<JsonElement> options)
        => options with
        {
            Schema = options.Schema.Clone(),
            SerializerOptions = options.SerializerOptions is null ? null : new(options.SerializerOptions)
        };

    private InvocationOptions PrepareOrdinaryStructuredOutput(InvocationOptions options)
    {
        // An explicit typed invocation already supplied its own response tool.
        if (options.StructuredOutputTool is not null) return options;
        var configured = options.StructuredOutput ?? _options.StructuredOutput;
        if (configured is null) return options;
        var snapshot = CopyStructuredOutputOptions(configured);
        if (snapshot.Deserializer is { } convert)
            snapshot = snapshot with { Deserializer = value => convert(value).Clone() };
        var tool = new StructuredOutputTool<JsonElement>(snapshot);
        return options with
        {
            StructuredOutputTool = tool,
            StructuredOutputForcePrompt = tool.ForcePrompt,
            StructuredOutputBehavior = snapshot.Behavior,
            StructuredOutputValidationLimit = snapshot.MaxValidationAttempts,
            ExposeStructuredOutput = true
        };
    }

    private async ValueTask AcceptDiscardedModelResponseAsync(ModelTurn turn, CancellationToken cancellationToken)
    {
        if (turn.ModelState is null || (_options.ModelHistoryBehavior == ModelHistoryBehavior.Native ? IsStateful : turn.ProviderStateful) && !turn.ProviderCompleted) return;
        var modelState = ModelStateValues.Clone(turn.ModelState);
        lock (_historyGate)
        {
            if (UsesNativeStatefulHistory)
            {
                var count = turn.ProviderInputMessageCount;
                if (count < EffectiveModelHistoryCount || count > _messages.Count ||
                    count > 0 && !StringComparer.Ordinal.Equals(turn.ProviderInputLastTrackingId, _messages[count - 1].TrackingId))
                    throw new ModelException("The consumed local history boundary no longer matches the completed provider input.");
                var cursor = _modelHistoryCount;
                var receipt = _modelInputReceipt;
                if (count > 0)
                {
                    if (_messages[count - 1].Role == Role.Assistant) { cursor = count; receipt = null; }
                    else receipt = new(count, turn.ProviderInputLastTrackingId!, turn.ProviderMessage!.TrackingId);
                }
                var accepted = new SessionSnapshot(receipt is null ? 1 : 2, _messages.ToArray())
                { ModelHistoryCount = cursor, ModelInputReceipt = receipt, ModelState = modelState };
                ConversationHistory.Validate(accepted.Messages);
                accepted.ValidateModelHistory();
                accepted.ValidateModelInputReceipt();
                ModelState.Replace(modelState);
                _modelHistoryCount = cursor;
                _modelInputReceipt = receipt;
            }
            else ModelState.Replace(modelState);
        }
        MarkAcceptedHistory(cancellationToken);
        await SaveSessionAsync(AcceptedWriteToken(cancellationToken)).ConfigureAwait(false);
    }

    private static string? SelectAcceptedStructuredOutput(IStructuredOutputTool tool, StructuredOutputBehavior behavior,
        IReadOnlyList<ToolUseBlock> calls, ToolBatch batch, CancellationToken cancellationToken)
    {
        if (behavior == StructuredOutputBehavior.TypeScript)
        {
            var first = calls.FirstOrDefault(call => call.Name == tool.Spec.Name);
            if (first is null) return null;
            var final = batch.Results.OfType<ToolResultBlock>().FirstOrDefault(result => result.ToolUseId == first.ToolUseId && result.Status == ToolResultStatus.Success);
            if (final?.ContentBlocks.FirstOrDefault() is not JsonBlock json || tool is not IStructuredOutputAttempt attempt) return null;
            attempt.AcceptFinalJson(first.ToolUseId, json.Json, cancellationToken);
            return first.ToolUseId;
        }
        if (behavior == StructuredOutputBehavior.Python)
            return calls.FirstOrDefault(call => call.Name == tool.Spec.Name && batch.ValidatedStructuredIds.Contains(call.ToolUseId, StringComparer.Ordinal) && tool.TryGetResult(call.ToolUseId, out _))?.ToolUseId;
        // Native retains its accepted successful result and validated-body/cache requirements.
        return batch.Results.OfType<ToolResultBlock>().FirstOrDefault(result => result.Status == ToolResultStatus.Success &&
            batch.ValidatedStructuredIds.Contains(result.ToolUseId, StringComparer.Ordinal) && tool.TryGetResult(result.ToolUseId, out _))?.ToolUseId;
    }

    private static JsonElement? AcceptedStructuredJson(IStructuredOutputTool? tool, string? toolUseId, string reason, StructuredOutputBehavior behavior)
        => (reason == StopReasons.ToolUse || behavior == StructuredOutputBehavior.Python && reason is (StopReasons.EndTurn or StopReasons.Cancelled or StopReasons.Interrupt)) &&
            tool is StructuredOutputTool<JsonElement> json && toolUseId is not null && json.TryGetResult(toolUseId, out var value)
            ? value : null;
}
