namespace Strands.Agents;

public sealed partial class Agent
{
    private async Task<ToolBatch> CollectCustomToolsAsync(IToolBatchExecutor executor, ToolUseBlock[] calls,
        IReadOnlyDictionary<string, object?> state, InvocationProgress progress, CancellationToken token,
        Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish)
    {
        using var execution = new CustomToolBatchExecution(this, calls, state, progress, token, publish);
        execution.SeedCompleted();
        Exception? schedulerError = null;
        try { await executor.ExecuteAsync(execution.Context, execution.Token).ConfigureAwait(false); }
        catch (Exception error) { schedulerError = error; }
        finally { execution.CloseAdmission(); }
        if (schedulerError is not null) execution.Cancel();
        return await execution.FinishAsync(schedulerError).ConfigureAwait(false);
    }

    private sealed class CustomToolBatchExecution : IDisposable
    {
        private static readonly AsyncLocal<PipelineReceipt?> Current = new();
        [ThreadStatic] private static PipelineReceipt? _synchronous;
        private readonly object _gate = new();
        private readonly Agent _owner;
        private readonly ToolUseBlock[] _calls;
        private readonly IReadOnlyDictionary<string, object?> _state;
        private readonly InvocationProgress _progress;
        private readonly Func<IAgentStreamEvent, CancellationToken, ValueTask>? _publish;
        private readonly PendingToolBatch _active;
        private readonly SemaphoreSlim _concurrency;
        private readonly CancellationTokenSource _operation;
        private readonly Task<ToolResultBlock?>?[] _tasks;
        private readonly ToolOutcome?[] _outcomes;
        private readonly Exception?[] _failures;
        private bool _open = true;
        private Exception? _cancelFailure;
        internal ToolBatchExecutionContext Context { get; }
        internal CancellationToken Token => _operation.Token;

        internal CustomToolBatchExecution(Agent owner, ToolUseBlock[] calls, IReadOnlyDictionary<string, object?> state,
            InvocationProgress progress, CancellationToken token, Func<IAgentStreamEvent, CancellationToken, ValueTask>? publish)
        {
            _owner = owner; _calls = calls; _state = state; _progress = progress; _publish = publish; _active = owner._activeToolBatch!;
            _tasks = new Task<ToolResultBlock?>?[calls.Length]; _outcomes = new ToolOutcome?[calls.Length]; _failures = new Exception?[calls.Length];
            Context = new(calls, state, ExecuteAsync);
            _concurrency = new(owner._options.MaxParallelTools);
            try { _operation = CancellationTokenSource.CreateLinkedTokenSource(token); }
            catch { _concurrency.Dispose(); throw; }
        }

        internal void SeedCompleted()
        {
            // Rehydrate the existing structured decoder as well as completed remote results, without replaying bodies/events.
            for (var index = 0; index < _calls.Length; index++)
                if (_active.TryGetResult(_calls[index].ToolUseId, out _)) _ = ExecuteAsync(index);
        }

        private Task<ToolResultBlock?> ExecuteAsync(int index)
        {
            if (index < 0 || index >= _calls.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (HasActive(Current.Value) || HasActive(_synchronous)) throw new InvalidOperationException("A tool pipeline callback cannot reschedule its own custom batch.");
            TaskCompletionSource<ToolResultBlock?> completion;
            lock (_gate)
            {
                if (!_open) throw new InvalidOperationException("The custom tool batch no longer accepts scheduling requests.");
                if (_tasks[index] is { } existing) return existing;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _tasks[index] = completion.Task;
            }
            // The public task is already published before any synchronous application callback can run.
            _ = RunAcceptedAsync(index, completion);
            return completion.Task;
        }

        private bool HasActive(PipelineReceipt? receipt)
        {
            for (; receipt is not null; receipt = receipt.Previous)
                if (ReferenceEquals(receipt.Owner, this) && receipt.Active) return true;
            return false;
        }

        private async Task RunAcceptedAsync(int index, TaskCompletionSource<ToolResultBlock?> completion)
        {
            var previous = Current.Value; var receipt = new PipelineReceipt(this, previous); Current.Value = receipt;
            var entered = false; ToolResultBlock? result = null; Exception? failure = null;
            try
            {
                await _concurrency.WaitAsync(CancellationToken.None).ConfigureAwait(false); entered = true;
                ValueTask<ToolOutcome> pending;
                var priorSynchronous = _synchronous; var synchronous = new PipelineReceipt(this, priorSynchronous); _synchronous = synchronous;
                try { pending = _owner.RunToolPipelineAsync(_calls[index], _state, _progress, Token, _publish); }
                finally { synchronous.Active = false; _synchronous = priorSynchronous; }
                var outcome = await pending.ConfigureAwait(false);
                _outcomes[index] = outcome;
                if (outcome.Interrupt is null) result = (ToolResultBlock)Message.CloneBlock(outcome.Result);
            }
            catch (Exception error) { failure = error; _failures[index] = error; }
            finally
            {
                if (entered) _concurrency.Release();
                receipt.Active = false; Current.Value = previous;
            }
            // Completion follows physical pipeline/permit cleanup and private outcome capture.
            if (failure is null) completion.TrySetResult(result); else completion.TrySetException(failure);
        }

        internal void CloseAdmission() { lock (_gate) _open = false; }
        internal void Cancel() { try { _operation.Cancel(); } catch (Exception error) { _cancelFailure = error; } }

        internal async Task<ToolBatch> FinishAsync(Exception? schedulerError)
        {
            Task[] accepted;
            lock (_gate) accepted = _tasks.Where(task => task is not null).Cast<Task>().ToArray();
            try { await Task.WhenAll(accepted).ConfigureAwait(false); }
            catch { /* Every fault remains captured in its issued-index slot below. */ }
            if (!_active.IsPaused)
            {
                for (var index = 0; index < _calls.Length; index++)
                {
                    if (_active.TryGetResult(_calls[index].ToolUseId, out _)) continue;
                    if (_tasks[index] is not null && _failures[index] is null) continue;
                    var skipped = _failures[index] is { } failure
                        ? new ToolResultBlock(_calls[index].ToolUseId, "Tool pipeline failed: " + failure.Message, ToolResultStatus.Error) { Error = failure }
                        : Token.IsCancellationRequested ? CancelledTool(_calls[index])
                        : new ToolResultBlock(_calls[index].ToolUseId, "The custom tool batch executor did not schedule this call.", ToolResultStatus.Error);
                    _active.Complete(skipped);
                    _outcomes[index] = await _owner.PublishToolResultAsync(new(skipped, _failures[index]), _state, Token, _publish).ConfigureAwait(false);
                }
            }
            Exception? primary = null;
            for (var index = 0; index < _calls.Length; index++)
            {
                AddFailure(ref primary, _outcomes[index]?.Error);
                AddFailure(ref primary, _failures[index]);
            }
            AddFailure(ref primary, schedulerError); AddFailure(ref primary, _cancelFailure);
            var results = _calls.Select(call => _active.TryGetResult(call.ToolUseId, out var value) ? value : null)
                .Where(result => result is not null).Cast<ContentBlock>().ToArray();
            return new(results, primary)
            {
                Interrupted = _active.IsPaused,
                DelegationToolUseId = _outcomes.SingleOrDefault(outcome => outcome?.Delegation == true)?.Result.ToolUseId
            };
        }

        private static void AddFailure(ref Exception? primary, Exception? error)
        {
            if (error is null) return;
            if (primary is null) { primary = error; return; }
            for (Exception? cause = primary; cause is not null; cause = cause.InnerException)
                if (ReferenceEquals(cause, error)) return;
            if (primary.Data["Strands.CleanupErrors"] is List<Exception> errors && errors.Any(value => ReferenceEquals(value, error))) return;
            AttachCleanupError(primary, error);
        }

        public void Dispose() { try { _concurrency.Dispose(); } finally { _operation.Dispose(); } }

        private sealed class PipelineReceipt(CustomToolBatchExecution owner, PipelineReceipt? previous)
        {
            internal CustomToolBatchExecution Owner { get; } = owner;
            internal PipelineReceipt? Previous { get; } = previous;
            internal volatile bool Active = true;
        }
    }
}
