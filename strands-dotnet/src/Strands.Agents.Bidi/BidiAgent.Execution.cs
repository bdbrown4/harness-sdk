using System.Text.Json;

namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    private async Task RunModelAsync(Conversation conversation)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        ReaderContext? current = null;
        Task? reader = null;
        Exception? primary = null;
        try
        {
            while (true)
            {
                conversation.Token.ThrowIfCancellationRequested();
                current = new(Volatile.Read(ref conversation.Generation), conversation.Token);
                reader = RunGenerationAsync(conversation, current);
                using var timerLifetime = CancellationTokenSource.CreateLinkedTokenSource(conversation.Token);
                var settings = _options.Model.Connection ?? throw new ModelException("The bidirectional model returned null connection settings.");
                var timer = RunDeadlineAsync(conversation, current.Generation, settings, timerLifetime.Token);
                BidiConnectionTimeoutException? timeout = null;
                var restart = false;
                Exception? cycleFailure = null;
                try
                {
                    if (await Task.WhenAny(reader, timer).ConfigureAwait(false) == reader) await reader.ConfigureAwait(false);
                    else
                    {
                        await timer.ConfigureAwait(false);
                        restart = await AwaitTurnBoundaryAsync(conversation, reader).ConfigureAwait(false);
                    }
                }
                catch (BidiConnectionTimeoutException error) when (_options.Model.Connection?.AutoRestart == true && !conversation.Token.IsCancellationRequested)
                { timeout = error; restart = true; }
                catch (Exception error) { cycleFailure = error; }
                await CancelAndJoinAsync(timerLifetime, timer, error => Add(ref cycleFailure, error)).ConfigureAwait(false);
                if (cycleFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cycleFailure).Throw();
                if (!restart) break;
                current.RestartTriggerHandled = timeout is not null;
                await RestartConnectionAsync(conversation, current, reader, timeout).ConfigureAwait(false);
                current.Lifetime.Dispose(); current = null; reader = null;
            }
            await Task.WhenAll(conversation.Tools).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (conversation.Token.IsCancellationRequested) { }
        catch (Exception error) { primary = error; }
        finally
        {
            if (current is not null && reader is not null)
            {
                var settling = current;
                await CancelAndJoinAsync(current.Lifetime, reader, error =>
                {
                    var handled = settling.Reaped || settling.RestartTriggerHandled && settling.CleanupErrors.Count == 0;
                    if (!handled || !ReferenceEquals(error, settling.Failure)) Add(ref primary, error);
                }).ConfigureAwait(false);
                current.Lifetime.Dispose();
            }
            conversation.Output.Complete(primary);
            Scope.Value = previous;
        }
    }
    private async Task RunGenerationAsync(Conversation conversation, ReaderContext context)
    {
        IAsyncEnumerator<IBidiOutputEvent>? iterator = null;
        Exception? primary = null;
        try
        {
            iterator = _options.Model.ReceiveAsync(context.Lifetime.Token).GetAsyncEnumerator(context.Lifetime.Token);
            while (await iterator.MoveNextAsync().ConfigureAwait(false))
            {
                context.Lifetime.Token.ThrowIfCancellationRequested();
                var output = iterator.Current;
                IBidiOutputEvent? complete;
                TaskCompletionSource<bool>? publication = null;
                lock (_gate)
                {
                    if (context.Generation != conversation.Generation) return;
                    if (output is BidiConnectionStartEvent connection)
                    {
                        conversation.ConnectionId = connection.ConnectionId;
                        conversation.ConnectionIdGeneration = context.Generation;
                    }
                    if (output is BidiToolUseBlocksEvent tools)
                    {
                        ValidateCalls(tools.ToolUses, conversation);
                        publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        conversation.Tools.Add(RunToolsAsync(conversation, tools.ToolUses, publication.Task));
                    }
                    complete = ProcessContent(output, context.Blocks); TrackTurn(conversation, output);
                    if (output is BidiUsageEvent usage) RecordBidiUsage(conversation, usage);
                }
                if (context.Generation == Volatile.Read(ref conversation.Generation)) ObserveBidiOutput(conversation, context, output);
                try
                {
                    await FlushHistoryAsync().ConfigureAwait(false);
                    if (output is BidiBargeInEvent) await Hooks.InvokeAsync(new BidiBargeInHookEvent(this), context.Lifetime.Token).ConfigureAwait(false);
                    else if (output is BidiResponseStopEvent completed) await Hooks.InvokeAsync(new BidiResponseStopHookEvent(this, completed.ResponseId), context.Lifetime.Token).ConfigureAwait(false);
                    if (publication is null && context.Generation != Volatile.Read(ref conversation.Generation)) return;
                    await conversation.Output.WriteAsync(output, publication is null ? context.Lifetime.Token : conversation.Token).ConfigureAwait(false);
                    publication?.TrySetResult(true);
                }
                catch (Exception error) { publication?.TrySetException(error); throw; }
                if (context.Generation != Volatile.Read(ref conversation.Generation)) return;
                if (complete is not null) await conversation.Output.WriteAsync(complete, context.Lifetime.Token).ConfigureAwait(false);
                if (context.Generation != Volatile.Read(ref conversation.Generation)) return;
            }
        }
        catch (Exception error) { primary = error; context.ReceiveError = error; }
        finally
        {
            if (iterator is not null) try { await iterator.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { context.CleanupErrors.Add(error); Add(ref primary, error); }
            try { CompletePendingContent(context); await FlushHistoryAsync().ConfigureAwait(false); } catch (Exception error) { context.CleanupErrors.Add(error); Add(ref primary, error); }
            EndBidiResponse(context, BidiTraceError(primary, context.Lifetime.Token));
            context.Failure = primary;
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
    private static void ValidateCalls(IReadOnlyList<ToolUseBlock> calls, Conversation conversation)
    {
        if (calls.Count == 0 || calls.Any(call => string.IsNullOrWhiteSpace(call.ToolUseId)) || calls.Select(call => call.ToolUseId).Distinct(StringComparer.Ordinal).Count() != calls.Count ||
            calls.Any(call => conversation.ToolIds.Contains(call.ToolUseId))) throw new ModelException("The bidirectional model supplied empty or repeated tool identifiers.");
        foreach (var call in calls) conversation.ToolIds.Add(call.ToolUseId);
    }
    private async Task RunToolsAsync(Conversation conversation, IReadOnlyList<ToolUseBlock> calls, Task publication)
    {
        var previous = Scope.Value; Scope.Value = new(this, previous);
        var assistant = new Message(Role.Assistant, calls.Cast<ContentBlock>().ToArray());
        var placeholder = new Message(Role.User, calls.Select(call => (ContentBlock)new ToolResultBlock(call.ToolUseId, "Tool result pending.", ToolResultStatus.Error)).ToArray(),
            metadata: JsonSerializer.SerializeToElement(new { custom = new { bidi = new { kind = "tool_dispatch" } } }));
        int resultIndex; Task notification;
        lock (_gate)
        {
            _messages.Add(assistant); resultIndex = _messages.Count; _messages.Add(placeholder); conversation.UnsentResultIndices.Add(resultIndex);
            notification = QueueHistoryLocked([new BidiMessageAddedEvent(this, assistant), new BidiMessageAddedEvent(this, placeholder)], conversation.Token);
        }
        var results = new ToolResultBlock?[calls.Count];
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(conversation.Token);
        Exception? primary = null;
        Exception? historyFailure = null;
        try
        {
            await notification.ConfigureAwait(false);
            await WaitAsync(publication, batch.Token).ConfigureAwait(false);
            await Task.WhenAll(calls.Select((call, index) => RunOneAsync(call, index))).ConfigureAwait(false);
        }
        catch (Exception error) { primary = error; }
        finally
        {
            var complete = new Message(Role.User, calls.Select((call, index) => (ContentBlock)(results[index] ?? new(call.ToolUseId, "Tool execution did not complete.", ToolResultStatus.Error))).ToArray(), placeholder.TrackingId,
                JsonSerializer.SerializeToElement(new { custom = new { bidi = new { kind = "tool_result" } } }));
            lock (_gate) { _messages[resultIndex] = complete; notification = QueueHistoryLocked([new BidiMessageUpdatedEvent(this, complete)], CancellationToken.None); }
            try { await notification.ConfigureAwait(false); }
            catch (Exception error) { historyFailure = error; Add(ref primary, error); }
            if (primary is null && !conversation.Token.IsCancellationRequested)
            {
                try
                {
                    if (ShouldStopAfterTools(conversation, out var terminal))
                    {
                        if (terminal is not null) await conversation.Output.WriteAsync(terminal, conversation.Token).ConfigureAwait(false);
                    }
                    else await SendModelAsync(conversation, new BidiMessage(complete.Content), false, conversation.Token, resultIndex).ConfigureAwait(false);
                }
                catch (Exception error) { primary = error; }
            }
            if (primary is not null && !conversation.Token.IsCancellationRequested) conversation.Output.Complete(primary);
            Scope.Value = previous;
        }
        if (historyFailure is not null && conversation.Token.IsCancellationRequested)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary is OperationCanceledException ? historyFailure : primary!).Throw();

        async Task RunOneAsync(ToolUseBlock call, int index)
        {
            var span = conversation.Telemetry is { } telemetry ? StartBidiTool(telemetry, call) : null;
            Exception? toolError = null;
            try
            {
                results[index] = await _options.ToolAgent.InvokeBidiToolAsync(call, conversation.State, PublishProgressAsync, batch.Token).ConfigureAwait(false);
                var terminal = new ToolResultEvent(_options.ToolAgent, results[index]!, conversation.State);
                await _options.ToolAgent.Hooks.InvokeAsync(terminal, batch.Token).ConfigureAwait(false);
                await conversation.Output.WriteAsync(terminal, batch.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                toolError = error;
                results[index] ??= new(call.ToolUseId, error.Message, ToolResultStatus.Error) { Error = error };
                try { batch.Cancel(); } catch (Exception cleanup) { Exception? captured = error; Add(ref captured, cleanup); }
                throw;
            }
            finally
            {
                if (toolError is OperationCanceledException && batch.IsCancellationRequested) span?.End();
                else if (results[index] is { } result) span?.EndToolCall(result, toolError);
                else span?.End(error: toolError);
            }
            async ValueTask PublishProgressAsync(ToolUseBlock effectiveCall, ToolStreamEvent update, CancellationToken token)
            {
                var progress = new ToolStreamUpdateEvent(_options.ToolAgent, effectiveCall, update, conversation.State);
                await _options.ToolAgent.Hooks.InvokeAsync(progress, token).ConfigureAwait(false);
                await conversation.Output.WriteAsync(progress, token).ConfigureAwait(false);
            }
        }
    }
}
