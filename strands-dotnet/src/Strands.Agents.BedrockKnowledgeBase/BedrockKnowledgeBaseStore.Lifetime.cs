namespace Strands.Agents.BedrockKnowledgeBase;

public sealed partial class BedrockKnowledgeBaseStore
{
    private static readonly AsyncLocal<Receipt?> Current = new();
    [ThreadStatic] private static Receipt? _synchronous;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closing;
    private int _operations;
    private TaskCompletionSource<bool>? _drained;
    private TaskCompletionSource<bool>? _dispose;

    private Operation EnterOperation(CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        lock (_gate) { if (_closing) throw new ObjectDisposedException(nameof(BedrockKnowledgeBaseStore)); _operations++; }
        try
        {
            var operation = new Operation(this, caller, Current.Value);
            Current.Value = operation.Scope;
            return operation;
        }
        catch { ExitOperation(); throw; }
    }
    private void ExitOperation()
    {
        TaskCompletionSource<bool>? drained;
        lock (_gate) { drained = --_operations == 0 ? _drained : null; }
        drained?.TrySetResult(true);
    }
    private bool HasActive(Receipt? receipt)
    {
        for (; receipt is not null; receipt = receipt.Previous)
            if (ReferenceEquals(receipt.Owner, this) && receipt.Active) return true;
        return false;
    }
    private T InvokeClient<T>(Func<T> callback)
    {
        var previous = _synchronous; var receipt = new Receipt(this, previous); _synchronous = receipt;
        try { return callback(); }
        finally { receipt.Active = false; _synchronous = previous; }
    }
    /// <summary>Closes admission, cancels operation tokens, joins real SDK tasks, and attempts all owned cleanup.</summary>
    public ValueTask DisposeAsync() => new(BeginDispose());
    /// <inheritdoc />
    public void Dispose() => BeginDispose().GetAwaiter().GetResult();
    private Task BeginDispose()
    {
        if (HasActive(Current.Value) || HasActive(_synchronous)) throw new InvalidOperationException("A knowledge-base callback cannot wait for its own disposal.");
        Task drain; TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_dispose is not null) return _dispose.Task;
            _closing = true; _dispose = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            drain = _operations == 0 ? Task.CompletedTask : (_drained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        _ = CompleteDisposeAsync(completion, drain);
        return completion.Task;
    }
    private async Task CompleteDisposeAsync(TaskCompletionSource<bool> completion, Task drain)
    {
        var previous = Current.Value; var receipt = new Receipt(this, previous); Current.Value = receipt;
        Exception? failure = null;
        try
        {
            Attempt(() => _lifetime.Cancel());
            await drain.ConfigureAwait(false);
            if (_ownsRuntime && _runtime.IsValueCreated) Attempt(() => _runtime.Value.Dispose());
            if (_ownsAgent && _agent.IsValueCreated) Attempt(() => _agent.Value.Dispose());
            if (_ownsS3 && _s3.IsValueCreated) Attempt(() => _s3.Value.Dispose());
            Attempt(_initialization.Dispose);
            Attempt(_lifetime.Dispose);
        }
        finally { receipt.Active = false; Current.Value = previous; }
        if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);

        void Attempt(Action callback)
        {
            try { InvokeClient(() => { callback(); return 0; }); }
            catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
        }
    }
    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        for (Exception? cause = primary; cause is not null; cause = cause.InnerException) if (ReferenceEquals(cause, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }
    private sealed class Receipt(BedrockKnowledgeBaseStore owner, Receipt? previous)
    {
        internal BedrockKnowledgeBaseStore Owner { get; } = owner;
        internal Receipt? Previous { get; } = previous;
        internal volatile bool Active = true;
    }
    private sealed class Operation : IDisposable
    {
        private readonly BedrockKnowledgeBaseStore _owner;
        private readonly CancellationTokenSource _linked;
        private int _active = 1;
        internal Receipt Scope { get; }
        internal CancellationToken Token => _linked.Token;
        internal Operation(BedrockKnowledgeBaseStore owner, CancellationToken caller, Receipt? previous)
        {
            _owner = owner; Scope = new(owner, previous);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, owner._lifetime.Token);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Scope.Active = false; Current.Value = Scope.Previous;
            try { _linked.Dispose(); } finally { _owner.ExitOperation(); }
        }
    }
}
