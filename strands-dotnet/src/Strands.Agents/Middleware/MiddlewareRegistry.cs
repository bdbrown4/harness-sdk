using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.Middleware;

/// <summary>Composes typed Input, Wrap and Output middleware. Inputs enter in registration order; Wrap and Output unwind in reverse registration order.</summary>
public sealed class MiddlewareRegistry
{
    private readonly object _gate = new();
    private readonly List<Registration> _registrations = [];
    private readonly MiddlewareRegistry? _parent;
    private static long _sequence;

    /// <summary>Creates a registry, optionally layering local registrations over a shared application registry.</summary>
    public MiddlewareRegistry(MiddlewareRegistry? parent = null) => _parent = parent;

    /// <summary>Registers an input transformation and returns its independent removal handle.</summary>
    public IDisposable RegisterInput<TContext, TResult>(MiddlewareStage<TContext, TResult> stage, Func<TContext, CancellationToken, ValueTask<TContext>> handler)
    {
        Guard.NotNull(handler, nameof(handler));
        return Register(stage, Phase.Input, (MiddlewareHandler<TContext, TResult>)Adapt);
        async IAsyncEnumerable<IMiddlewareEvent<TResult>> Adapt(TContext context, MiddlewareNext<TContext, TResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var transformed = await handler(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var detached = stage.SnapshotContext(transformed);
            await foreach (var item in next(detached).ConfigureAwait(false)) yield return item;
        }
    }

    /// <summary>Registers a streaming wrapper and returns its independent removal handle.</summary>
    public IDisposable RegisterWrap<TContext, TResult>(MiddlewareStage<TContext, TResult> stage, MiddlewareHandler<TContext, TResult> handler)
    {
        Guard.NotNull(handler, nameof(handler));
        return Register(stage, Phase.Wrap, handler);
    }

    /// <summary>Registers a completed-result transformation and returns its independent removal handle.</summary>
    public IDisposable RegisterOutput<TContext, TResult>(MiddlewareStage<TContext, TResult> stage, Func<TResult, CancellationToken, ValueTask<TResult>> handler)
    {
        Guard.NotNull(handler, nameof(handler));
        return Register(stage, Phase.Output, (MiddlewareHandler<TContext, TResult>)Adapt);
        async IAsyncEnumerable<IMiddlewareEvent<TResult>> Adapt(TContext context, MiddlewareNext<TContext, TResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            TResult result = default!;
            await foreach (var item in next(context).ConfigureAwait(false))
            {
                if (item is MiddlewareResultEvent<TResult> completed) result = completed.Result;
                else yield return item;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var transformed = await handler(result, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MiddlewareResultEvent<TResult>(stage.SnapshotResult(transformed));
        }
    }

    /// <summary>Snapshots registrations and input immediately, then runs a typed stage. Every fully consumed chain must complete exactly once. The supplied cancellation token is forwarded unchanged.</summary>
    public IAsyncEnumerable<IMiddlewareEvent<TResult>> InvokeAsync<TContext, TResult>(MiddlewareStage<TContext, TResult> stage, TContext context, MiddlewareTerminal<TContext, TResult> terminal, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(stage, nameof(stage)); Guard.NotNull(terminal, nameof(terminal));
        cancellationToken.ThrowIfCancellationRequested();
        var detached = stage.SnapshotContext(context);
        var registrations = Snapshot().Where(registration => ReferenceEquals(registration.Stage, stage))
            .OrderBy(registration => registration.Phase).ThenBy(registration => registration.Sequence).ToArray();
        MiddlewareNext<TContext, TResult> current = value => ValidateAsync(stage, () => terminal(stage.SnapshotContext(value), cancellationToken), cancellationToken);
        for (var index = registrations.Length - 1; index >= 0; index--)
        {
            var handler = (MiddlewareHandler<TContext, TResult>)registrations[index].Handler;
            var inner = current;
            current = value => ValidateAsync(stage, () => RunLayerAsync(stage, value, handler, inner, cancellationToken), cancellationToken);
        }
        return current(detached);
    }

    private Registration Register<TContext, TResult>(MiddlewareStage<TContext, TResult> stage, Phase phase, object handler)
    {
        Guard.NotNull(stage, nameof(stage));
        lock (_gate)
        {
            var registration = new Registration(this, stage, phase, handler, Interlocked.Increment(ref _sequence));
            _registrations.Add(registration);
            return registration;
        }
    }

    private Registration[] Snapshot()
    {
        Registration[] local;
        lock (_gate) local = _registrations.ToArray();
        return _parent is null ? local : _parent.Snapshot().Concat(local).ToArray();
    }

    private static async IAsyncEnumerable<IMiddlewareEvent<TResult>> RunLayerAsync<TContext, TResult>(MiddlewareStage<TContext, TResult> stage, TContext context,
        MiddlewareHandler<TContext, TResult> handler, MiddlewareNext<TContext, TResult> inner, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scope = new NextScope<IMiddlewareEvent<TResult>>();
        IAsyncEnumerator<IMiddlewareEvent<TResult>>? iterator = null;
        Exception? primary = null;
        MiddlewareNext<TContext, TResult> next = value =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detached = stage.SnapshotContext(value);
            return scope.Track(() => inner(detached), cancellationToken);
        };
        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                iterator = handler(stage.SnapshotContext(context), next, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                bool moved;
                IMiddlewareEvent<TResult>? item = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    moved = await iterator.MoveNextAsync().ConfigureAwait(false);
                    if (moved) item = iterator.Current;
                }
                catch (Exception error) { primary = error; throw; }
                if (!moved) yield break;
                yield return item!;
            }
        }
        finally
        {
            var cleanup = new List<IAsyncDisposable>();
            if (iterator is not null) cleanup.Add(iterator);
            cleanup.Add(scope);
            await DisposeAllAsync(cleanup, primary).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<IMiddlewareEvent<TResult>> ValidateAsync<TContext, TResult>(MiddlewareStage<TContext, TResult> stage,
        Func<IAsyncEnumerable<IMiddlewareEvent<TResult>>> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<IMiddlewareEvent<TResult>>? iterator = null;
        Exception? primary = null;
        var completed = false;
        try
        {
            try { cancellationToken.ThrowIfCancellationRequested(); iterator = source().GetAsyncEnumerator(cancellationToken); }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                bool moved;
                IMiddlewareEvent<TResult>? item = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    moved = await iterator.MoveNextAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (moved)
                    {
                        item = iterator.Current;
                        if (completed) throw new InvalidOperationException("Middleware emitted content after its completed result.");
                        item = item switch
                        {
                            MiddlewareStreamEvent<TResult> update when update.Event is not null => update,
                            MiddlewareResultEvent<TResult> result when result.Result is not null => new MiddlewareResultEvent<TResult>(stage.SnapshotResult(result.Result)),
                            _ => throw new InvalidOperationException("Middleware emitted an unknown or null stage event or result."),
                        };
                        completed = item is MiddlewareResultEvent<TResult>;
                    }
                    else if (!completed) throw new InvalidOperationException("Middleware ended without a completed stage result.");
                }
                catch (Exception error) { primary = error; throw; }
                if (!moved) yield break;
                yield return item!;
            }
        }
        finally { if (iterator is not null) await DisposeAllAsync(new[] { iterator }, primary).ConfigureAwait(false); }
    }

    private static async ValueTask DisposeAllAsync(IEnumerable<IAsyncDisposable> resources, Exception? primary)
    {
        var failure = primary;
        foreach (var resource in resources)
        {
            try { await resource.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else if (!ReferenceEquals(failure, error))
                {
                    const string key = "Strands.CleanupErrors";
                    if (failure.Data[key] is not List<Exception> errors) failure.Data[key] = errors = [];
                    if (!errors.Contains(error)) errors.Add(error);
                }
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal void ClearOwned() { lock (_gate) _registrations.Clear(); }
    private enum Phase { Input, Output, Wrap }
    private sealed record Registration(MiddlewareRegistry Owner, object Stage, Phase Phase, object Handler, long Sequence) : IDisposable
    {
        public void Dispose() { lock (Owner._gate) Owner._registrations.Remove(this); }
    }

    private sealed class NextScope<T> : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<TrackedEnumerator> _iterators = [];
        private bool _closed;

        internal IAsyncEnumerable<T> Track(Func<IAsyncEnumerable<T>> source, CancellationToken token) => new TrackedEnumerable(this, source, token);
        private IAsyncEnumerator<T> Open(Func<IAsyncEnumerable<T>> source, CancellationToken token)
        {
            lock (_gate)
            {
                if (_closed) throw new ObjectDisposedException("middleware continuation", "A middleware continuation cannot outlive its wrapper.");
                var iterator = new TrackedEnumerator(source().GetAsyncEnumerator(token));
                _iterators.Add(iterator);
                return iterator;
            }
        }
        public async ValueTask DisposeAsync()
        {
            TrackedEnumerator[] iterators;
            lock (_gate) { _closed = true; iterators = _iterators.AsEnumerable().Reverse().ToArray(); }
            await DisposeAllAsync(iterators, null).ConfigureAwait(false);
        }
        private sealed class TrackedEnumerable(NextScope<T> owner, Func<IAsyncEnumerable<T>> source, CancellationToken token) : IAsyncEnumerable<T>
        {
            public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => owner.Open(source, token);
        }
        private sealed class TrackedEnumerator(IAsyncEnumerator<T> inner) : IAsyncEnumerator<T>
        {
            private readonly object _gate = new();
            private Task? _disposal;
            public T Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
            public ValueTask DisposeAsync() { lock (_gate) return new(_disposal ??= DisposeCoreAsync()); }
            private async Task DisposeCoreAsync() => await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
