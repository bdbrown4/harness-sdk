using Strands.Agents;

namespace Strands.Harness;

public sealed partial class HarnessAssemblyLoader
{
    private sealed partial class DeclaredToolProvider
    {
        public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default)
        {
            Guard.NotNull(context, nameof(context)); Guard.NotNull(observer, nameof(observer));
            RefuseCallbackReentry(); cancellationToken.ThrowIfCancellationRequested();
            return new(PreserveFailure(EstablishAsync(context, observer, cancellationToken)));
        }

        private async Task<IAsyncDisposable> EstablishAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken caller, object? admission = null)
        {
            Task? closing;
            SourceGeneration generation;
            LoadAttempt attempt;
            lock (_gate)
            {
                admission = RequireConsumer(context, admission);
                closing = _closing?.Closed.Task;
                if (closing is null)
                {
                    generation = EnsureGenerationUnderGate();
                    ThrowTerminal(generation);
                    generation.WatchRequested = true;
                    attempt = RequireAttemptUnderGate(generation, watch: true);
                }
                else { generation = null!; attempt = null!; }
            }
            if (closing is not null)
            {
                await TaskCancellation.WaitAsync(closing, caller).ConfigureAwait(false);
                return await EstablishAsync(context, observer, caller, admission).ConfigureAwait(false);
            }
            await AwaitAttemptAsync(generation, attempt, caller).ConfigureAwait(false);
            Subscription subscription;
            IReadOnlyList<ITool> initial;
            lock (_gate)
            {
                caller.ThrowIfCancellationRequested(); RequireConsumer(context, admission); ThrowTerminal(generation);
                if (generation.Closing) throw new OperationCanceledException(generation.Lifetime.Token);
                initial = generation.Tools!;
                subscription = new(this, generation, context.ConsumerId, observer, generation.Version);
                generation.Subscriptions.Add(subscription);
            }
            Exception? primary = null;
            try
            {
                using (var scope = new CallbackScope(this))
                using (var synchronous = new SynchronousCallbackScope(this)) observer.OnNext(initial);
                caller.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (subscription.Closing || generation.Closing) throw new OperationCanceledException(generation.Lifetime.Token);
                    subscription.Initializing = false;
                    subscription.ScheduleUnderGate();
                }
            }
            catch (Exception error) { primary = error; }
            finally { subscription.InitialPhysical.TrySetResult(true); }
            if (primary is not null)
            {
                try { await subscription.CloseAsync().ConfigureAwait(false); } catch (Exception error) { AppendFailure(ref primary, error); }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary!).Throw();
            }
            return subscription;
        }

        private sealed class Subscription(DeclaredToolProvider owner, SourceGeneration generation, string consumerId,
            IObserver<IReadOnlyList<ITool>> observer, long version) : IAsyncDisposable
        {
            internal string ConsumerId { get; } = consumerId;
            internal TaskCompletionSource<bool> InitialPhysical { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Initializing = true;
            internal bool Closing;
            private long _version = version;
            private IReadOnlyList<ITool>? _pending;
            private Exception? _terminal;
            private TaskCompletionSource<bool>? _worker;
            private Task? _close;

            internal void EnqueueUnderGate(IReadOnlyList<ITool> tools, long nextVersion)
            {
                if (Closing || _terminal is not null || nextVersion <= _version) return;
                _version = nextVersion; _pending = tools;
                ScheduleUnderGate();
            }

            internal void TerminateUnderGate(Exception error)
            {
                if (Closing || _terminal is not null) return;
                _terminal = error; _pending = null;
                ScheduleUnderGate();
            }

            internal void ScheduleUnderGate()
            {
                if (Closing || Initializing || _worker is not null || _pending is null && _terminal is null) return;
                var worker = _worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(() => Deliver(worker));
            }

            private void Deliver(TaskCompletionSource<bool> worker)
            {
                try
                {
                    using var scope = new CallbackScope(owner);
                    using var synchronous = new SynchronousCallbackScope(owner);
                    while (true)
                    {
                        IReadOnlyList<ITool>? tools;
                        Exception? terminal;
                        lock (owner._gate)
                        {
                            if (Closing) return;
                            tools = _pending; _pending = null; terminal = _terminal;
                            if (tools is null && terminal is null) return;
                        }
                        try
                        {
                            if (terminal is not null) { observer.OnError(terminal); return; }
                            observer.OnNext(tools!);
                        }
                        catch (Exception error) { owner.Warn(error, "A managed tool observer failed; retaining the accepted generation."); }
                        if (terminal is not null) return;
                    }
                }
                finally
                {
                    lock (owner._gate)
                    {
                        _worker = null;
                        worker.TrySetResult(true);
                        if (_terminal is null) ScheduleUnderGate();
                    }
                }
            }

            public ValueTask DisposeAsync() { owner.RefuseCallbackReentry(); return new(CloseAsync()); }

            internal Task CloseAsync()
            {
                TaskCompletionSource<bool> completion;
                lock (owner._gate)
                {
                    if (_close is not null) return _close;
                    Closing = true; _pending = null;
                    completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _close = completion.Task;
                }
                _ = CompleteCloseAsync(completion);
                return completion.Task;
            }

            private async Task CompleteCloseAsync(TaskCompletionSource<bool> completion)
            {
                try
                {
                    await InitialPhysical.Task.ConfigureAwait(false);
                    Task worker;
                    lock (owner._gate) worker = _worker?.Task ?? Task.CompletedTask;
                    await worker.ConfigureAwait(false);
                    lock (owner._gate) generation.Subscriptions.Remove(this);
                    completion.TrySetResult(true);
                }
                catch (Exception error) { completion.TrySetException(error); }
            }
        }
    }
}
