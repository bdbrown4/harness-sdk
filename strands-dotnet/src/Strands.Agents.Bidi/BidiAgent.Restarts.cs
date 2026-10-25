using System.Runtime.ExceptionServices;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    private BidiSessionOptions CaptureSession(Conversation? conversation = null)
    {
        IReadOnlyList<Message> messages;
        lock (_gate) messages = Array.AsReadOnly(_messages.Where((_, index) => conversation?.UnsentResultIndices.Contains(index) != true).Select(Copy).ToArray());
        return new()
        {
            SystemPrompt = SystemPrompt,
            Messages = messages,
            Tools = Array.AsReadOnly(_options.ToolAgent.Tools.Select(tool => tool.Spec).Select(spec => new ToolSpec(spec.Name, spec.Description, spec.InputSchema)
            { OutputSchema = spec.OutputSchema?.Clone(), Annotations = spec.Annotations?.Clone() }).ToArray())
        };
    }

    private async Task SendModelAsync(Conversation conversation, IBidiInput input, bool recordUser, CancellationToken cancellationToken, int? settledResultIndex = null)
    {
        while (true)
        {
            Task ready; lock (_gate) ready = conversation.Ready;
            await WaitAsync(ready, cancellationToken).ConfigureAwait(false);
            await conversation.ConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Task? notification = null;
                lock (_gate)
                {
                    if (!ReferenceEquals(conversation.Ready, ready)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (recordUser && input is BidiMessage user)
                    {
                        var message = new Message(Role.User, user.Content); _messages.Add(message);
                        notification = QueueHistoryLocked([new BidiMessageAddedEvent(this, message)], cancellationToken);
                        conversation.AwaitingResponse = true; UpdateTurn(conversation);
                    }
                }
                if (notification is not null) await notification.ConfigureAwait(false);
                var previous = Scope.Value; var previousSend = SendScope.Value;
                Scope.Value = new(this, previous); SendScope.Value = new(this, previousSend);
                try { await _options.Model.SendAsync(input, cancellationToken).ConfigureAwait(false); }
                finally { Scope.Value = previous; SendScope.Value = previousSend; }
                if (settledResultIndex is { } index) lock (_gate) conversation.UnsentResultIndices.Remove(index);
                return;
            }
            finally { conversation.ConnectionGate.Release(); }
        }
    }

    private async Task RestartConnectionAsync(Conversation conversation, ReaderContext context, Task reader, BidiConnectionTimeoutException? timeout)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool interrupted;
        lock (_gate)
        {
            conversation.Token.ThrowIfCancellationRequested();
            conversation.Ready = ready.Task; conversation.Generation++;
            interrupted = conversation.ResponseActive || conversation.AwaitingResponse;
            CompletePendingContent(context);
        }
        Exception? primary = null;
        var reason = timeout is null ? BidiRestartReason.Scheduled : BidiRestartReason.Timeout;
        TraceSpan? restartSpan = null;
        Exception? restartError = null;
        try
        {
            await conversation.Output.WriteAsync(new BidiConnectionRestartEvent(reason, timeout, interrupted), conversation.Token).ConfigureAwait(false);
            await FlushHistoryAsync().ConfigureAwait(false);
            await InvokeRestartHookAsync(new BidiBeforeConnectionRestartEvent(this, reason, timeout), conversation.Token).ConfigureAwait(false);
            await SaveSessionCoreAsync(conversation.Token, true).ConfigureAwait(false);
            if (conversation.Telemetry is { } telemetry) restartSpan = StartBidiRestart(telemetry, reason, timeout);
            await conversation.ConnectionGate.WaitAsync(conversation.Token).ConfigureAwait(false);
            try
            {
                try
                {
                    conversation.Token.ThrowIfCancellationRequested();
                    var session = CaptureSession(conversation);
                    if (_options.Model is IRestartableBidiModel restartable) await restartable.RestartAsync(session, conversation.Token).ConfigureAwait(false);
                    else
                    {
                        await _options.Model.StopAsync(conversation.Token).ConfigureAwait(false);
                        await _options.Model.StartAsync(session, conversation.Token).ConfigureAwait(false);
                    }
                    conversation.Token.ThrowIfCancellationRequested();
                    await ReapSupersededReaderAsync(context, reader, conversation.Token).ConfigureAwait(false);
                }
                catch (Exception error) { primary = error; restartError = BidiTraceError(error, conversation.Token); }
                restartSpan?.End(error: restartError);
                try { await InvokeRestartHookAsync(new BidiAfterConnectionRestartEvent(this, reason, primary), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { Add(ref primary, error); }
                if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
                lock (_gate) { conversation.ResponseActive = false; conversation.AwaitingResponse = false; UpdateTurn(conversation); conversation.Ready = Task.CompletedTask; }
                ready.TrySetResult(true);
            }
            finally { conversation.ConnectionGate.Release(); }
        }
        catch (Exception error)
        {
            primary = error; ready.TrySetException(error); _ = ready.Task.Exception;
        }
        finally { restartSpan?.End(error: restartError); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private async ValueTask InvokeRestartHookAsync(IBidiHookEvent value, CancellationToken cancellationToken)
    {
        var previous = RestartHookScope.Value; RestartHookScope.Value = new(this, previous);
        try { await Hooks.InvokeAsync(value, cancellationToken).ConfigureAwait(false); }
        finally { RestartHookScope.Value = previous; }
    }

    private void TrackTurn(Conversation conversation, IBidiOutputEvent output)
    {
        lock (_gate)
        {
            switch (output)
            {
                case BidiResponseStartEvent: conversation.ResponseActive = true; conversation.AwaitingResponse = false; break;
                case BidiResponseStopEvent: conversation.ResponseActive = false; conversation.AwaitingResponse = false; break;
                case BidiTranscriptStartEvent { Role: Role.User }: conversation.AwaitingResponse = true; break;
                case BidiBargeInEvent: conversation.ResponseActive = false; break;
            }
            UpdateTurn(conversation);
        }
    }

    private static void UpdateTurn(Conversation conversation)
    {
        if (conversation.ResponseActive || conversation.AwaitingResponse)
        {
            if (conversation.TurnComplete.Task.IsCompleted) conversation.TurnComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        else conversation.TurnComplete.TrySetResult(true);
    }

    private Task RunDeadlineAsync(Conversation conversation, long generation, BidiConnectionOptions settings, CancellationToken cancellationToken)
        => settings.AutoRestart && settings.RestartAfter is { } interval && interval > TimeSpan.Zero
            ? BidiRestartTimer.RunAsync(interval, TimeSpan.FromSeconds(10),
                async (remaining, token) => await conversation.Output.WriteAsync(new BidiConnectionWarningEvent(remaining), token).ConfigureAwait(false), _delay, cancellationToken)
            : Task.Delay(Timeout.Infinite, cancellationToken);

    private async Task<bool> AwaitTurnBoundaryAsync(Conversation conversation, Task reader)
    {
        Task boundary; lock (_gate) boundary = conversation.TurnComplete.Task;
        if (boundary.IsCompleted) return true;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(conversation.Token);
        var limit = _delay(TimeSpan.FromSeconds(10), waiting.Token);
        Exception? primary = null;
        var restart = true;
        try
        {
            var completed = await Task.WhenAny(boundary, reader, limit).ConfigureAwait(false);
            if (completed == reader) { await reader.ConfigureAwait(false); restart = false; }
            else if (completed == limit) await limit.ConfigureAwait(false);
            conversation.Token.ThrowIfCancellationRequested();
        }
        catch (Exception error) { primary = error; }
        await CancelAndJoinAsync(waiting, limit, error => Add(ref primary, error)).ConfigureAwait(false);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        return restart;
    }

    private async Task ReapSupersededReaderAsync(ReaderContext context, Task reader, CancellationToken cancellationToken)
    {
        // Restart has closed the old transport. Cancellation now only reaps its stale reader.
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? limit = null;
        try
        {
            if (!reader.IsCompleted)
            {
                limit = _delay(TimeSpan.FromSeconds(2), waiting.Token);
                if (await Task.WhenAny(reader, limit).ConfigureAwait(false) == limit)
                {
                    await limit.ConfigureAwait(false);
                    context.Lifetime.Cancel();
                }
            }
            try { await reader.ConfigureAwait(false); } catch (OperationCanceledException) when (context.Lifetime.IsCancellationRequested) { } catch (Exception) { }
            context.Reaped = true;
            if (context.CleanupErrors.Count > 0)
            {
                Exception? failure = context.ReceiveError is BidiConnectionTimeoutException ? context.Failure : context.CleanupErrors[0];
                if (context.ReceiveError is not BidiConnectionTimeoutException)
                    foreach (var error in context.CleanupErrors.Skip(1)) Add(ref failure, error);
                ExceptionDispatchInfo.Capture(failure!).Throw();
            }
        }
        finally
        {
            if (limit is not null) await CancelAndJoinAsync(waiting, limit, _ => { }).ConfigureAwait(false);
        }
    }

    private void CompletePendingContent(ReaderContext context)
    {
        lock (_gate) { foreach (var block in context.Blocks.Values) CommitContent(block, false); context.Blocks.Clear(); }
    }

    private static async Task CancelAndJoinAsync(CancellationTokenSource cancellation, Task task, Action<Exception> failure)
    {
        try { cancellation.Cancel(); } catch (Exception error) { failure(error); }
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { failure(error); }
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); completed.SetResult(true); return completed;
    }

    private sealed class ReaderContext(long generation, CancellationToken cancellationToken)
    {
        internal TraceSpan? ResponseSpan;
        internal long ResponseStarted;
        internal long? FirstAudioMilliseconds;
        internal long Generation { get; } = generation;
        internal CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        internal Dictionary<string, PendingContent> Blocks { get; } = new(StringComparer.Ordinal);
        internal List<Exception> CleanupErrors { get; } = [];
        internal Exception? ReceiveError;
        internal Exception? Failure;
        internal bool Reaped;
        internal bool RestartTriggerHandled;
    }

    private static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!task.IsCompleted && cancellationToken.CanBeCanceled)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
            if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) cancellationToken.ThrowIfCancellationRequested();
        }
        await task.ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
    }
}
