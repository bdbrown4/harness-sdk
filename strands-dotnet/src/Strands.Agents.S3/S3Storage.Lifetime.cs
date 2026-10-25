namespace Strands.Agents.S3;

public sealed partial class S3Storage
{
    private static readonly AsyncLocal<Operation?> CurrentOperation = new();
    private static readonly AsyncLocal<Disposal?> CurrentDisposal = new();
    [ThreadStatic] private static Disposal? _synchronousDisposal;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private int _operations;
    private TaskCompletionSource<bool>? _drained;
    private TaskCompletionSource<bool>? _dispose;

    private Operation EnterOperation(CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        var prior = CurrentOperation.Value;
        lock (_gate)
        {
            if (_closing) throw new ObjectDisposedException(nameof(S3Storage));
            _operations++;
        }
        Operation operation;
        try { operation = new(this, prior, caller); }
        catch { ExitOperation(); throw; }
        CurrentOperation.Value = operation;
        return operation;
    }

    private bool HasActiveOperation(Operation? operation)
    {
        for (; operation is not null; operation = operation.Previous)
            if (ReferenceEquals(operation.Owner, this) && operation.Active) return true;
        return false;
    }

    private void ExitOperation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_gate) { if (--_operations == 0) drained = _drained; }
        drained?.TrySetResult(true);
    }

    private Task BeginDispose()
    {
        if (HasActiveOperation(CurrentOperation.Value) || HasActiveDisposal(CurrentDisposal.Value) || HasActiveDisposal(_synchronousDisposal)) throw new InvalidOperationException("An S3 storage callback cannot wait for its own disposal.");
        Task drain;
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_dispose is not null) return _dispose.Task;
            _closing = true;
            _dispose = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            drain = _operations == 0 ? Task.CompletedTask : (_drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        _ = CompleteDisposeAsync(completion, drain);
        return completion.Task;
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource<bool> completion, Task drain)
    {
        var prior = CurrentDisposal.Value;
        var scope = new Disposal(this, prior);
        CurrentDisposal.Value = scope;
        Exception? failure = null;
        try
        {
            try { InvokeDisposalCallback(_lifetime.Cancel); }
            catch (Exception error) { failure = error; }
            await drain.ConfigureAwait(false);
            try { if (_ownsClient && _client.IsValueCreated) InvokeDisposalCallback(() => _client.Value.Dispose()); }
            catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
            try { _lifetime.Dispose(); }
            catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
        }
        finally { scope.Active = false; CurrentDisposal.Value = prior; }
        if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
    }

    private void InvokeDisposalCallback(Action callback)
    {
        var prior = _synchronousDisposal; var scope = new Disposal(this, prior); _synchronousDisposal = scope;
        try { callback(); } finally { scope.Active = false; _synchronousDisposal = prior; }
    }

    private bool HasActiveDisposal(Disposal? scope)
    {
        for (; scope is not null; scope = scope.Previous)
            if (ReferenceEquals(scope.Owner, this) && scope.Active) return true;
        return false;
    }

    private sealed class Disposal(S3Storage owner, Disposal? previous)
    {
        internal S3Storage Owner { get; } = owner;
        internal Disposal? Previous { get; } = previous;
        internal volatile bool Active = true;
    }

    private static void AttachCleanup(Exception owner, Exception cleanup)
    {
        for (Exception? cause = owner; cause is not null; cause = cause.InnerException) if (ReferenceEquals(cause, cleanup)) return;
        if (owner.Data["Strands.CleanupErrors"] is not List<Exception> errors) owner.Data["Strands.CleanupErrors"] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }

    private sealed class Operation : IDisposable
    {
        private readonly CancellationTokenSource? _linked;
        private int _active = 1;
        internal S3Storage Owner { get; }
        internal Operation? Previous { get; }
        internal bool Active => Volatile.Read(ref _active) != 0;
        internal CancellationToken Token { get; }
        internal Operation(S3Storage owner, Operation? previous, CancellationToken caller)
        {
            Owner = owner; Previous = previous;
            if (caller.CanBeCanceled) { _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, owner._lifetime.Token); Token = _linked.Token; }
            else Token = owner._lifetime.Token;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            CurrentOperation.Value = Previous;
            try { _linked?.Dispose(); } finally { Owner.ExitOperation(); }
        }
    }
}
