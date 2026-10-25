using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

public sealed partial class Agent
{
    /// <summary>The middleware layer owned by this agent, layered over application registrations and cleared during disposal.</summary>
    public MiddlewareRegistry Middleware { get; }

    private async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> StreamModelTerminalAsync(InvokeModelContext context, ModelTurn turn,
        IReadOnlyDictionary<string, JsonElement> modelState, IReadOnlyList<Message> pendingHistory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var providerStateful = context.Model is IStatefulModel { Stateful: true };
        if (_options.ModelHistoryBehavior == ModelHistoryBehavior.Native && providerStateful != IsStateful)
            throw new InvalidOperationException("A middleware-selected model must preserve the agent's server-history capability.");
        if (UsesNativeStatefulHistory && !ContainsPendingHistory(context.Messages, pendingHistory))
            throw new InvalidOperationException("Stateful middleware must retain every pending message and content block in order.");
        var request = new ModelRequest(context.Messages, context.SystemPrompt, context.ToolSpecs)
        { SystemPromptContent = context.SystemPromptContent, ToolChoice = context.ToolChoice, SessionId = SessionId, ModelState = modelState, DynamicTrailingBlocks = context.DynamicTrailingBlocks };
        var telemetrySpan = _tracer.StartModelInvokeSpan(TelemetryModelId(context.Model));
        telemetrySpan.RecordInput(request);
        var accumulator = new ModelStreamAccumulator();
        IAsyncEnumerator<ModelStreamEvent>? iterator = null;
        Exception? primary = null;
        Message? message = null;
        string? stopReason = null;
        turn.ProviderCompleted = false;
        turn.ProviderStateful = providerStateful;
        turn.ProviderCleanupFailed = false;
        turn.ModelState = null;
        turn.ProviderMessage = null;
        lock (_historyGate)
        {
            turn.ProviderInputMessageCount = _messages.Count;
            turn.ProviderInputLastTrackingId = _messages.LastOrDefault()?.TrackingId;
        }
        try
        {
            try { iterator = telemetrySpan.WithContext(() => context.Model.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken)); }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                bool moved;
                ModelStreamEvent? current = null;
                ContentBlock? completedBlock = null;
                try
                {
                    moved = await telemetrySpan.WithValueTaskContextAsync(() => iterator.MoveNextAsync()).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (moved)
                    {
                        current = CloneMiddlewareModelEvent(iterator.Current);
                        accumulator.Process(current);
                        if (current is ModelContentBlockStopEvent stopped) completedBlock = accumulator.GetCompletedBlock(stopped.Index);
                        turn.Usage = accumulator.Usage;
                        turn.UsageReported = accumulator.UsageReported;
                        if (_options.InputRedactionTiming == InputRedactionTiming.ProviderEvent && current is ModelRedactionEvent { InputReplacement: { } replacement })
                            await ApplyInputRedactionAsync(replacement, cancellationToken).ConfigureAwait(false);
                    }
                    else (message, stopReason) = accumulator.Complete();
                }
                catch (Exception error) { primary = error; throw; }
                if (!moved) break;
                yield return new MiddlewareStreamEvent<InvokeModelResult>(new ModelStreamUpdateEvent(current!));
                if (completedBlock is not null)
                {
                    try { cancellationToken.ThrowIfCancellationRequested(); }
                    catch (Exception error) { primary = error; throw; }
                    yield return new MiddlewareStreamEvent<InvokeModelResult>(new ContentBlockEvent(this, completedBlock, context.InvocationState));
                }
            }
        }
        finally
        {
            try
            {
                if (iterator is not null)
                    await telemetrySpan.WithValueTaskContextAsync(async () => { await iterator.DisposeAsync().ConfigureAwait(false); return true; }).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                turn.ProviderCleanupFailed = true;
                if (primary is null) { primary = cleanup; throw; }
                if (!ReferenceEquals(primary, cleanup)) AttachCleanupError(primary, cleanup);
            }
            finally { telemetrySpan.End(message, accumulator.UsageReported ? accumulator.Usage : null, primary, stopReason); }
        }
        turn.ProviderCompleted = true;
        turn.ModelState = accumulator.ModelState;
        turn.ProviderMessage = message;
        yield return new MiddlewareResultEvent<InvokeModelResult>(new(message!, stopReason!, accumulator.Usage)
        {
            InputRedaction = _options.InputRedactionTiming == InputRedactionTiming.AcceptedTurn ? accumulator.InputRedaction : null,
            UsageReported = accumulator.UsageReported,
            PerformanceMetrics = accumulator.PerformanceMetrics
        });
    }

    private void ValidateStatefulModelResult(ModelTurn turn, Message output)
    {
        if (turn.ProviderStateful && turn.ProviderCompleted && turn.ProviderMessage is { } original &&
            !original.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId).SequenceEqual(output.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId)))
            throw new ModelException("A transformed server-managed response must retain every emitted tool-call identifier in its original order.");
    }

    private static ModelStreamEvent CloneMiddlewareModelEvent(ModelStreamEvent value)
    {
        var current = value;
        if (current is ModelContentBlockStartEvent { ToolUse: { } tool } start)
            current = start with { ToolUse = (ToolUseBlock)Message.CloneBlock(tool) };
        if (current is ModelContentBlockStartEvent { ContentBlock: { } initial } typed)
            current = typed with { ContentBlock = Message.CloneBlock(initial) };
        if (current is ModelContentBlockStopEvent { ContentBlock: { } completed } stopped)
            current = stopped with { ContentBlock = Message.CloneBlock(completed) };
        if (current is ModelMessageStopEvent { Metadata: { } metadata } messageStopped)
            current = messageStopped with { Metadata = metadata.Clone() };
        if (current is ModelMessageStopEvent { ModelState: { } nextState } stateStopped)
            current = stateStopped with { ModelState = ModelStateValues.Clone(nextState) };
        return current;
    }

    private void RestoreMiddlewareModelState(IReadOnlyDictionary<string, JsonElement> snapshot)
    {
        var current = ModelState.GetAll();
        if (current.Count != snapshot.Count || current.Any(pair => !snapshot.TryGetValue(pair.Key, out var expected) || expected.GetRawText() != pair.Value.GetRawText()))
            ModelState.Replace(snapshot);
    }

    private async ValueTask<ToolResultBlock> InvokeToolMiddlewareAsync(ExecuteToolContext context, string? denied,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish, CancellationToken cancellationToken)
    {
        ToolResultBlock? result = null;
        var providerId = context.ToolUse.ToolUseId;
        var terminal = new ToolTerminalOutcome();
        try
        {
            await foreach (var item in Middleware.InvokeAsync(MiddlewareStages.ExecuteTool, context,
                (value, token) => StreamToolTerminalAsync(value, providerId, denied, terminal, token), cancellationToken).ConfigureAwait(false))
            {
                if (item is MiddlewareStreamEvent<ExecuteToolResult> update && publish is not null)
                    await publish(update.Event, cancellationToken).ConfigureAwait(false);
                else if (item is MiddlewareResultEvent<ExecuteToolResult> completed) result = completed.Result.Result;
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation stops new middleware work, but never erases a completed side effect or skips its after hook.
            result ??= terminal.Result ?? CancelledTool(context.ToolUse) with { Error = error };
        }
        return NormalizeToolResult(result!, providerId);
    }

    private async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> StreamToolTerminalAsync(ExecuteToolContext context, string providerId, string? denied, ToolTerminalOutcome terminal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var call = (ToolUseBlock)Message.CloneBlock(context.ToolUse with { ToolUseId = providerId });
        if (denied is not null) { yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(providerId, denied, ToolResultStatus.Error))); yield break; }
        if (context.Tool is { } known && _activeToolBatch?.ReuseBody(call, RegisteredToolSpec(known).Name) is { } previous)
        {
            terminal.Result = previous;
            yield return new MiddlewareResultEvent<ExecuteToolResult>(new(previous));
            yield break;
        }
        await foreach (var item in StreamObservedToolTerminalAsync(context with { ToolUse = call }, terminal, cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    private async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> StreamToolBodyTerminalAsync(ExecuteToolContext context, ToolTerminalOutcome terminal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var call = context.ToolUse;
        var providerId = call.ToolUseId;
        if (context.Tool is not { } tool) { yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(providerId, $"Tool not found: {call.Name}.", ToolResultStatus.Error))); yield break; }
        if (tool is not IStructuredOutputTool && !RegisteredToolSchema(tool).Evaluate(call.Input).IsValid)
        { yield return new MiddlewareResultEvent<ExecuteToolResult>(new(new(providerId, $"Input does not match the schema for tool {RegisteredToolSpec(tool).Name}.", ToolResultStatus.Error))); yield break; }
        if (tool is not IStreamingTool)
        {
            terminal.Result = await InvokeToolBodyAsync(tool, call, context.InvocationState, cancellationToken, null).ConfigureAwait(false);
            _activeToolBatch?.RecordBody(call, RegisteredToolSpec(tool).Name, terminal.Result);
            yield return new MiddlewareResultEvent<ExecuteToolResult>(new(terminal.Result));
            yield break;
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var queue = new ToolProgressQueue();
        var body = ProduceAsync();
        Exception? primary = null;
        ToolResultBlock? result = null;
        try
        {
            while (true)
            {
                ToolProgressQueue.PendingUpdate? update;
                try { update = await queue.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                if (update is null) break;
                yield return new MiddlewareStreamEvent<ExecuteToolResult>(update.Event);
                update.Acknowledge();
            }
            try { result = await body.ConfigureAwait(false); }
            catch (Exception error) { primary = error; throw; }
        }
        finally
        {
            var failure = primary;
            try { if (!body.IsCompleted) operation.Cancel(); }
            catch (Exception error) { if (failure is null) failure = error; else AttachCleanupError(failure, error); }
            try { await body.ConfigureAwait(false); }
            catch (Exception cleanup) { if (failure is null) failure = cleanup; else if (!ReferenceEquals(failure, cleanup)) AttachCleanupError(failure, cleanup); }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        yield return new MiddlewareResultEvent<ExecuteToolResult>(new(result!));

        async Task<ToolResultBlock> ProduceAsync()
        {
            try
            {
                terminal.Result = await InvokeToolBodyAsync(tool, call, context.InvocationState, operation.Token, queue.WriteAsync).ConfigureAwait(false);
                _activeToolBatch?.RecordBody(call, RegisteredToolSpec(tool).Name, terminal.Result);
                return terminal.Result;
            }
            finally { queue.Complete(); }
        }
    }

    private sealed class ToolTerminalOutcome { internal ToolResultBlock? Result; }
}
