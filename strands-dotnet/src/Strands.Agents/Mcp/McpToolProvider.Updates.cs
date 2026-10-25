using System.Runtime.ExceptionServices;

namespace Strands.Agents.Mcp;

public sealed partial class McpToolProvider
{
    /// <summary>Establishes an independently owned full-snapshot observer for an admitted consumer.</summary>
    /// <remarks>The token controls establishment only. Without ToolChanges this delivers the cached initial batch only.
    /// Removing a consumer also closes all of its leases. Borrowed observers must return cooperatively;
    /// waiting on this provider's lifecycle from one of its callbacks is refused.</remarks>
    public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context, nameof(context)); Guard.NotNull(observer, nameof(observer));
        RefuseCallbackReentry();
        cancellationToken.ThrowIfCancellationRequested();
        return new(EstablishUpdateSubscriptionAsync(context, observer, cancellationToken));
    }

    private async Task<IAsyncDisposable> EstablishUpdateSubscriptionAsync(ToolProviderContext context,
        IObserver<IReadOnlyList<ITool>> observer, CancellationToken caller)
    {
        object admission;
        lock (_gate)
            if (!_consumers.TryGetValue(context.ConsumerId, out admission!))
                throw new InvalidOperationException("The MCP provider consumer is no longer admitted.");
        await LoadToolsAsync(context, caller, admission).ConfigureAwait(false);
        UpdateSubscription subscription;
        IReadOnlyList<ITool> initial;
        lock (_gate)
        {
            caller.ThrowIfCancellationRequested();
            if (!_consumers.TryGetValue(context.ConsumerId, out var currentAdmission) || !ReferenceEquals(admission, currentAdmission) ||
                _current is not { Closing: false, Tools: { } tools } generation)
                throw new InvalidOperationException("The MCP provider consumer is no longer admitted.");
            if (generation.UpdatesError is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            if (generation.UpdatesTerminated) throw new InvalidOperationException("The MCP provider notification stream has ended.");
            initial = tools;
            subscription = new(this, generation, context.ConsumerId, observer, generation.WatchVersion);
            generation.UpdateSubscriptions.Add(subscription);
        }
        Exception? failure = null;
        try
        {
            using (var scope = new CallbackScope(this, subscription.Generation))
            using (var synchronous = new SynchronousCallbackScope(this)) observer.OnNext(initial);
            caller.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (subscription.Closing || subscription.Generation.Closing)
                    throw new OperationCanceledException(subscription.Generation.Lifetime.Token);
                subscription.Initializing = false;
                subscription.ScheduleUnderGate();
            }
        }
        catch (Exception error) { failure = error; }
        finally { subscription.InitialPhysical.TrySetResult(true); }
        if (failure is not null)
        {
            try { await subscription.CloseAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { AppendFailure(ref failure, cleanup); }
            ExceptionDispatchInfo.Capture(failure!).Throw();
        }
        return subscription;
    }

    private async Task ObserveToolChangeCompletionAsync(Generation generation, Task completion)
    {
        Exception? failure = null;
        try { await completion.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        lock (_gate)
        {
            if (generation.Closing) return;
            generation.UpdatesTerminated = true;
            generation.UpdatesError = failure;
            foreach (var subscription in generation.UpdateSubscriptions)
                subscription.TerminateUnderGate(failure);
        }
    }

    private sealed class UpdateSubscription(McpToolProvider owner, Generation generation, string consumerId,
        IObserver<IReadOnlyList<ITool>> observer, long version) : IAsyncDisposable
    {
        internal Generation Generation { get; } = generation;
        internal string ConsumerId { get; } = consumerId;
        internal TaskCompletionSource<bool> InitialPhysical { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Initializing = true;
        internal bool Closing;
        private long _version = version;
        private IReadOnlyList<ITool>? _pending;
        private bool _terminal;
        private Exception? _error;
        private TaskCompletionSource<bool>? _worker;
        private Task? _close;

        internal void EnqueueUnderGate(IReadOnlyList<ITool> tools, long nextVersion)
        {
            if (Closing || _terminal || nextVersion <= _version) return;
            _version = nextVersion; _pending = tools;
            ScheduleUnderGate();
        }

        internal void TerminateUnderGate(Exception? error)
        {
            if (Closing || _terminal) return;
            _terminal = true; _error = error; _pending = null;
            ScheduleUnderGate();
        }

        internal void ScheduleUnderGate()
        {
            if (Closing || Initializing || _worker is not null || _pending is null && !_terminal) return;
            var worker = _worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // No borrowed callback runs inline in the cache worker or under the provider gate.
            _ = Task.Run(() => Deliver(worker));
        }

        private void Deliver(TaskCompletionSource<bool> worker)
        {
            var scope = new CallbackScope(owner, Generation);
            try
            {
                while (true)
                {
                    IReadOnlyList<ITool>? tools; bool terminal; Exception? error;
                    lock (owner._gate)
                    {
                        if (Closing) return;
                        tools = _pending; _pending = null; terminal = _terminal; error = _error;
                        if (tools is null && !terminal) return;
                    }
                    try
                    {
                        using var synchronous = new SynchronousCallbackScope(owner);
                        if (terminal) { if (error is null) observer.OnCompleted(); else observer.OnError(error); return; }
                        observer.OnNext(tools!);
                    }
                    catch (Exception observerError)
                    {
                        using var synchronous = new SynchronousCallbackScope(owner);
                        SdkLogging.Warning(observerError, "An MCP provider update observer failed; retaining the accepted cache.");
                    }
                    if (terminal) return;
                }
            }
            finally
            {
                scope.Dispose();
                lock (owner._gate)
                {
                    _worker = null;
                    worker.TrySetResult(true);
                    if (!_terminal) ScheduleUnderGate();
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
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _close = completion.Task;
            }
            _ = CompleteCloseAsync(completion);
            return completion.Task;
        }

        private async Task CompleteCloseAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                await InitialPhysical.Task.ConfigureAwait(false);
                Task worker; lock (owner._gate) worker = _worker?.Task ?? Task.CompletedTask;
                await worker.ConfigureAwait(false);
                lock (owner._gate) Generation.UpdateSubscriptions.Remove(this);
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
