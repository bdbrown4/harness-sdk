using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents;

public sealed partial class Agent
{
    /// <summary>Streams one registered tool without invoking the model or recording conversation messages.</summary>
    /// <param name="toolUse">The registered tool request and its original correlation identifier.</param>
    /// <param name="invocationState">Optional state copied for this direct call.</param>
    /// <param name="cancellationToken">Cancellation for this operation.</param>
    /// <returns>Progress updates followed by one normalized rich terminal result.</returns>
    /// <remarks>Uses the native direct-tool before/after hooks, schema validation and admission rules. Model and tool middleware are not run. The call owns its iterator and joins its cleanup when enumeration ends. Caller-supplied tool and model resources remain borrowed. This native hook and no-history profile differs from the pinned TypeScript and Python direct callers.</remarks>
#if NETSTANDARD2_0
    public IAsyncEnumerable<IToolStreamEvent> StreamToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState = null, CancellationToken cancellationToken = default)
        => StreamDirectToolAsync(toolUse, invocationState, null, cancellationToken);
#else
    public async IAsyncEnumerable<IToolStreamEvent> StreamToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in StreamDirectToolAsync(toolUse, invocationState, null, cancellationToken).ConfigureAwait(false))
            yield return update;
    }
#endif

    private IAsyncEnumerable<IToolStreamEvent> StreamDirectToolAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolCallOptions? directOptions,
        CancellationToken cancellationToken)
    {
#if NETSTANDARD2_0
        return new DirectToolStreamEnumerable(this, toolUse, invocationState, directOptions, cancellationToken);
#else
        return StreamDirectToolIteratorAsync(toolUse, invocationState, directOptions, cancellationToken);
#endif
    }

    private async IAsyncEnumerable<IToolStreamEvent> StreamDirectToolIteratorAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolCallOptions? directOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var updates = new DirectToolProgressQueue();
        var producer = ProduceDirectToolStreamAsync(toolUse, invocationState, updates, operation.Token, directOptions);
        Exception? primary = null;
        OperationCanceledException? producerCancellation = null;
        var accepted = false;
        try
        {
            while (true)
            {
                DirectToolProgressQueue.PendingUpdate? update;
                try { update = await updates.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                if (update is null) break;
                yield return update.Event;
                update.Acknowledge();
            }
            ToolResultBlock result;
            try { result = await producer.ConfigureAwait(false); }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken == operation.Token)
            {
                producerCancellation = error;
                var canceled = new OperationCanceledException(error.Message, error, cancellationToken);
                foreach (var key in error.Data.Keys) canceled.Data[key] = error.Data[key];
                primary = canceled;
                throw canceled;
            }
            catch (Exception error) { primary = error; throw; }
            accepted = true;
            yield return new ToolStreamResultEvent(result);
        }
        finally
        {
            var cleanup = new List<Exception>();
            var ownedStop = false;
            if (!producer.IsCompleted)
            {
                ownedStop = true;
                try { operation.Cancel(); }
                catch (Exception error) { AddDirectStreamFailure(cleanup, error); }
            }
            try
            {
                var result = await producer.ConfigureAwait(false);
                // A terminal not consumed by the caller must not hide a failed iterator cleanup.
                if (!accepted && result.IteratorCleanupFailed && result.Error is { } error)
                    AddDirectStreamFailure(cleanup, error);
            }
            catch (Exception error)
            {
                // Only an unadorned cancellation attributable to this call can be an expected stop.
                // Cleanup metadata or a different token remains an independent observable failure.
                var expectedStop = error is OperationCanceledException canceled &&
                    canceled.CancellationToken == operation.Token && canceled.Data.Count == 0 &&
                    (ownedStop || cancellationToken.IsCancellationRequested);
                if (!ReferenceEquals(error, primary) && !ReferenceEquals(error, producerCancellation) && !expectedStop)
                    AddDirectStreamFailure(cleanup, error);
            }
            var failure = primary;
            foreach (var error in cleanup)
            {
                if (failure is null) failure = error;
                else AttachDirectStreamCleanup(failure, error);
            }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task<ToolResultBlock> ProduceDirectToolStreamAsync(ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolProgressQueue updates, CancellationToken cancellationToken, DirectToolCallOptions? directOptions)
    {
        try
        {
            return await InvokeDirectToolAsync(toolUse, invocationState, true,
                (_, update, token) => updates.WriteAsync(update, token), cancellationToken, directOptions).ConfigureAwait(false);
        }
        finally { updates.Complete(); }
    }

    private static void AddDirectStreamFailure(List<Exception> failures, Exception error)
    {
        if (!failures.Any(value => ReferenceEquals(value, error))) failures.Add(error);
    }

    private static void AttachDirectStreamCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup) || primary.Data["Strands.CleanupErrors"] is List<Exception> errors && errors.Any(value => ReferenceEquals(value, cleanup))) return;
        AttachCleanupError(primary, cleanup);
    }

    // One producer awaits acknowledgment for every update; it cannot publish a second in-flight item.
    private sealed class DirectToolProgressQueue : IDisposable
    {
        private readonly object _gate = new();
        private readonly SemaphoreSlim _available = new(0);
        private PendingUpdate? _pending;
        private bool _completed;

        internal async ValueTask WriteAsync(ToolStreamEvent update, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingUpdate(update);
            lock (_gate)
            {
                if (_completed || _pending is not null) throw new InvalidOperationException("Direct tool progress has an invalid producer state.");
                _pending = pending;
            }
            _available.Release();
            using (cancellationToken.Register(() => pending.Cancel(cancellationToken)))
                await pending.Completion.ConfigureAwait(false);
        }

        internal async ValueTask<PendingUpdate?> ReadAsync(CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_pending is null) return _completed ? null : throw new InvalidOperationException("Direct tool progress lost an update.");
                var pending = _pending;
                _pending = null;
                return pending;
            }
        }

        internal void Complete() { lock (_gate) _completed = true; _available.Release(); }
        public void Dispose() { lock (_gate) _pending = null; _available.Dispose(); }

        internal sealed class PendingUpdate(ToolStreamEvent update)
        {
            private readonly TaskCompletionSource<bool> _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal ToolStreamEvent Event { get; } = update;
            internal Task Completion => _acknowledged.Task;
            internal void Acknowledge() => _acknowledged.TrySetResult(true);
            internal void Cancel(CancellationToken cancellationToken) => _acknowledged.TrySetCanceled(cancellationToken);
        }
    }

#if NETSTANDARD2_0
    // Task-backed operations retain cancellation payloads when callers use ValueTask.AsTask().
    private sealed class DirectToolStreamEnumerable(Agent owner, ToolUseBlock toolUse,
        IReadOnlyDictionary<string, object?>? invocationState, DirectToolCallOptions? directOptions,
        CancellationToken methodCancellation) : IAsyncEnumerable<IToolStreamEvent>
    {
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new DirectToolStreamEnumerator(owner.StreamDirectToolIteratorAsync(toolUse, invocationState,
                directOptions, methodCancellation).GetAsyncEnumerator(cancellationToken));
    }

    private sealed class DirectToolStreamEnumerator(IAsyncEnumerator<IToolStreamEvent> iterator)
        : IAsyncEnumerator<IToolStreamEvent>
    {
        public IToolStreamEvent Current => iterator.Current;
        public ValueTask<bool> MoveNextAsync() => new(MoveNextTaskAsync());
        public ValueTask DisposeAsync() => new(DisposeTaskAsync());

        private async Task<bool> MoveNextTaskAsync()
            => await iterator.MoveNextAsync().ConfigureAwait(false);

        private async Task DisposeTaskAsync()
            => await iterator.DisposeAsync().ConfigureAwait(false);
    }
#endif
}
