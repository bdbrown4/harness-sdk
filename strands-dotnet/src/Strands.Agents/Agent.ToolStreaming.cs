using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents;

public sealed partial class Agent
{
    private async IAsyncEnumerable<IAgentStreamEvent> StreamToolsAsync(ToolUseBlock[] calls, IReadOnlyDictionary<string, object?> state,
        InvocationProgress progress, ToolBatchExecution execution, InvocationCancellationScope scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var queue = new ToolProgressQueue();
        var active = BeginToolBatch(calls);
        var batchTask = ProduceToolBatchAsync(calls, state, progress, operation.Token, queue);
        Exception? primary = null;
        var consumed = false;
        try
        {
            while (true)
            {
                ToolProgressQueue.PendingUpdate? update;
                try { update = await queue.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                if (update is null) break;
                try { if (update.Event is HookEvent hook) await Hooks.InvokeAsync(hook, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                yield return update.Event;
                update.Acknowledge();
            }
            try { execution.Batch = await batchTask.ConfigureAwait(false); }
            catch (Exception error) { primary = error; throw; }
            consumed = true;
        }
        finally
        {
            var cleanup = new List<Exception>();
            try { if (!batchTask.IsCompleted) operation.Cancel(); }
            catch (Exception error) { cleanup.Add(error); }
            try
            {
                execution.Batch ??= await batchTask.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (!ReferenceEquals(error, primary)) cleanup.Add(error);
            }
            try
            {
                if (active.IsPaused)
                {
                    lock (_historyGate) _interruptState = active.Snapshot();
                    MarkAcceptedHistory(CancellationToken.None, scope);
                    await SaveSessionAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    if (active.AssistantMessageIsIndependent)
                    {
                        execution.Batch ??= new(active.ResultsForCommit(), null);
                        execution.Message = new(Role.User, execution.Batch.Results);
                        execution.ReinsertedAssistant = _options.ModelHistoryBehavior switch
                        {
                            ModelHistoryBehavior.TypeScript => active.AssistantMessage,
                            ModelHistoryBehavior.Python => null,
                            _ => throw new InvalidOperationException("Independent pending execution requires retained-history admission.")
                        };
                        await CommitIndependentToolResultsAsync(execution.ReinsertedAssistant, execution.Message, state, scope).ConfigureAwait(false);
                    }
                    else
                    {
                        lock (_historyGate) _interruptState = null;
                        execution.Batch ??= new(active.ResultsForCommit(), null);
                        execution.Message = new(Role.User, execution.Batch.Results);
                        // Committing in disposal retains completed sibling side effects when the consumer stops on progress.
                        await AddMessageAsync(execution.Message, state, CancellationToken.None, acceptedCancellation: scope).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error)
            {
                if (!ReferenceEquals(error, primary)) cleanup.Add(error);
            }
            var failure = primary ?? execution.Batch?.Error;
            active.Close();
            lock (_historyGate) _activeToolBatch = null;
            if (primary is not null && execution.Batch?.Error is { } pipeline && !ReferenceEquals(primary, pipeline)) cleanup.Add(pipeline);
            foreach (var error in cleanup)
            {
                if (failure is null) failure = error;
                else if (failure is { } existing && !ReferenceEquals(existing, error)) AttachCleanupError(existing, error);
            }
            if (primary is null && failure is not null && (!consumed || !ReferenceEquals(failure, execution.Batch?.Error))) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async ValueTask CommitIndependentToolResultsAsync(Message? assistant, Message result,
        IReadOnlyDictionary<string, object?> state, InvocationCancellationScope scope)
    {
        Message[] messages = assistant is null ? [result] : [assistant, result];
        var repositoryAppend = PrepareRepositoryAppend(messages);
        lock (_historyGate)
        {
            _messages.AddRange(messages);
            _interruptState = null;
        }
        MarkAcceptedHistory(CancellationToken.None, scope);
        repositoryAppend?.Accept();
        Exception? failure = null;
        try
        {
            foreach (var message in messages)
                await Hooks.InvokeAsync(new MessageAddedEvent(this, message) { InvocationState = state }, InvocationCallbackToken(CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { await SaveSessionAsync(failure is null ? AcceptedWriteToken(CancellationToken.None) : CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) when (failure is not null) { AttachCleanupError(failure, cleanup); }
        }
    }

    private async Task<ToolBatch> ProduceToolBatchAsync(ToolUseBlock[] calls, IReadOnlyDictionary<string, object?> state,
        InvocationProgress progress, CancellationToken cancellationToken, ToolProgressQueue queue)
    {
        try { return await ExecuteToolsAsync(calls, state, progress, cancellationToken, queue.WriteAsync).ConfigureAwait(false); }
        finally { queue.Complete(); }
    }

    private sealed class ToolBatchExecution
    {
        internal ToolBatch? Batch;
        internal Message? Message;
        internal Message? ReinsertedAssistant;
    }

    private sealed class ToolProgressQueue : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<PendingUpdate> _events = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly SemaphoreSlim _capacity = new(1, 1);
        private bool _completed;

        internal async ValueTask WriteAsync(IAgentStreamEvent update, CancellationToken cancellationToken)
        {
            await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            var pending = new PendingUpdate(update);
            lock (_gate) _events.Enqueue(pending);
            _available.Release();
            using (cancellationToken.Register(() => pending.Cancel(cancellationToken)))
                await pending.Completion.ConfigureAwait(false);
        }

        internal async ValueTask<PendingUpdate?> ReadAsync(CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_events.Count == 0) return _completed ? null : throw new InvalidOperationException("Tool progress queue lost an update.");
                var update = _events.Dequeue();
                _capacity.Release();
                return update;
            }
        }

        internal void Complete() { lock (_gate) _completed = true; _available.Release(); }
        public void Dispose() { _available.Dispose(); _capacity.Dispose(); }

        internal sealed class PendingUpdate(IAgentStreamEvent update)
        {
            private readonly TaskCompletionSource<bool> _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal IAgentStreamEvent Event { get; } = update;
            internal Task Completion => _acknowledged.Task;
            internal void Acknowledge() => _acknowledged.TrySetResult(true);
            internal void Cancel(CancellationToken cancellationToken) => _acknowledged.TrySetCanceled(cancellationToken);
        }
    }
}
