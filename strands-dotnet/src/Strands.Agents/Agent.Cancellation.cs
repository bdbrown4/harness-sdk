using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents;

public sealed partial class Agent
{
    private InvocationCancellationScope? _invocationCancellation;
    // Protected by _initializationGate. No cancellation source or application callback is owned here.
    private bool _cancellationAdmission, _queuedCancellation, _queuedDeferredCancellation;
    private string? _queuedCancellationMessage;
    /// <summary>The initialized pass signal, None during admitted-pass initialization, or an already-cancelled idle snapshot when CancelNextInvocation has a pending immediate request.</summary>
    public CancellationToken CancelSignal
    {
        get
        {
            if (_options.IdleCancellationBehavior == IdleCancellationBehavior.Ignore)
                return Volatile.Read(ref _invocationCancellation)?.Signal ?? CancellationToken.None;
            lock (_initializationGate)
            {
                if (_invocationCancellation is { } scope) return scope.Signal;
                return !_cancellationAdmission && !_disposed && _shutdown is null && _queuedCancellation
                    ? new CancellationToken(true) : CancellationToken.None;
            }
        }
    }

    /// <summary>Requests cooperative cancellation; idle immediate requests are retained only with CancelNextInvocation.</summary>
    /// <param name="message">Optional final assistant text. The last nonnull request wins, including an empty string.</param>
    /// <param name="afterCurrentTools">Complete the accepted tool batch before stopping. An idle deferred-only request is cleared on admission.</param>
    public void Cancel(string? message = null, bool afterCurrentTools = false)
    {
        if (_options.IdleCancellationBehavior == IdleCancellationBehavior.Ignore)
        {
            Volatile.Read(ref _invocationCancellation)?.Request(message, afterCurrentTools);
            return;
        }
        InvocationCancellationScope? scope;
        lock (_initializationGate)
        {
            if (_disposed || _shutdown is not null) return;
            scope = _invocationCancellation;
            if (scope is null)
            {
                if (message is not null) _queuedCancellationMessage = message;
                if (afterCurrentTools) _queuedDeferredCancellation = true;
                else _queuedCancellation = true;
                return;
            }
        }
        // Token callbacks may reenter agent APIs, and must run outside the owner lock.
        scope.Request(message, afterCurrentTools);
    }

    // Called under the admission lock, only after the invocation CAS succeeds.
    private void BeginCancellationAdmission()
    {
        _cancellationAdmission = true;
        _queuedDeferredCancellation = false;
        if (!_queuedCancellation) _queuedCancellationMessage = null;
    }

    // Called under the same lock before releasing _invoking, including preflight failure.
    private void EndCancellationAdmission()
    {
        _cancellationAdmission = false;
        _queuedCancellation = _queuedDeferredCancellation = false;
        _queuedCancellationMessage = null;
    }

    private InvocationCancellationScope OpenInvocationCancellation(CancellationToken caller)
    {
        var scope = new InvocationCancellationScope(this, caller);
        lock (_initializationGate)
        {
            scope.Seed(_queuedCancellation, _queuedDeferredCancellation, _queuedCancellationMessage);
            _queuedCancellation = _queuedDeferredCancellation = false;
            _queuedCancellationMessage = null;
            _invocationCancellation = scope;
        }
        return scope;
    }

    private bool NativeCancellationCheckpoints => _options.ExplicitCancellationTiming == ExplicitCancellationTiming.NativeEarly;
    private bool BeforeInputCancellationCheckpoint => _options.ExplicitCancellationTiming != ExplicitCancellationTiming.ModelOrTool;

    // Preparation may postpone an explicit request, but never the caller's hard cancellation.
    private CancellationToken CancellationPreparationToken(CancellationToken operation)
    {
        if (NativeCancellationCheckpoints) return operation;
        var scope = Volatile.Read(ref _invocationCancellation);
        return scope is not null && scope.Token == operation ? scope.Caller : operation;
    }

    private void CheckInvocationCancellation(CancellationToken operation, bool explicitRequest)
        => (explicitRequest ? operation : CancellationPreparationToken(operation)).ThrowIfCancellationRequested();

    private bool CheckCompletedModelCancellation(ModelTurn turn)
        => NativeCancellationCheckpoints || (!turn.ProviderCompleted && !turn.BeforeModelDenied) ||
            (_options.ExplicitCancellationTiming == ExplicitCancellationTiming.BeforeInputAndModelOrTool && turn.StopReason == StopReasons.ToolUse);

    private CancellationToken InvocationCallbackToken(CancellationToken operation)
    {
        var scope = Volatile.Read(ref _invocationCancellation);
        return scope is not null && scope.Token == operation ? scope.Caller : operation;
    }

    private CancellationToken AcceptedWriteToken(CancellationToken operation)
    {
        var scope = Volatile.Read(ref _invocationCancellation);
        return scope is not null && scope.Token == operation
            ? scope.Token.IsCancellationRequested ? CancellationToken.None : scope.Caller
            : operation;
    }

    private void MarkAcceptedHistory(CancellationToken operation, InvocationCancellationScope? acceptedCancellation = null)
    {
        if (acceptedCancellation is not null) { acceptedCancellation.MarkAcceptedHistory(); return; }
        var scope = Volatile.Read(ref _invocationCancellation);
        if (scope is not null && scope.Token == operation) scope.MarkAcceptedHistory();
    }

    private async IAsyncEnumerable<IAgentStreamEvent> StreamInvocationToolsAsync(ToolUseBlock[] calls,
        IReadOnlyDictionary<string, object?> state, InvocationProgress progress, ToolBatchExecution execution,
        InvocationCancellationScope scope, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var iterator = StreamToolsAsync(calls, state, progress, execution, scope, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Exception? primary = null;
        try
        {
            while (true)
            {
                bool moved;
                try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                catch (OperationCanceledException error) when (error.CancellationToken == scope.Token && scope.Token.IsCancellationRequested &&
                    !scope.Caller.IsCancellationRequested &&
                    ((execution.Batch is { EndTurn: not null, Error: null } && execution.Message is not null) ||
                        (scope.AllowsPausedBoundary && execution.Batch is { Interrupted: true, Error: null })) &&
                    error.Data["Strands.CleanupErrors"] is not List<Exception> { Count: > 0 })
                {
                    // A settled hook outcome or opted-in paused boundary wins only over this scope cancellation, after physical cleanup.
                    break;
                }
                catch (Exception error) { primary = error; throw; }
                if (!moved) break;
                yield return iterator.Current;
            }
        }
        finally
        {
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                else if (!ReferenceEquals(primary, cleanup)) AttachCleanupError(primary, cleanup);
            }
        }
    }

    private async ValueTask<Message?> CommitCancellationMessageAsync(InvocationCancellationScope scope, InvocationProgress progress)
    {
        // A cancellation does not discard a pending human batch or insert text into its incomplete tool exchange.
        if (InterruptState is not null || !scope.TakeMessage(out var text)) return null;
        await RepairPendingToolsAsync(progress.InvocationState).ConfigureAwait(false);
        var message = Message.Assistant(text!);
        progress.Message = message;
        await AddMessageAsync(message, progress.InvocationState, CancellationToken.None, acceptedCancellation: scope).ConfigureAwait(false);
        return message;
    }

    private sealed class InvocationCancellationScope : IDisposable
    {
        private readonly Agent _owner;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _callerRegistration;
        private int _cancellers;
        private long _historyVersion, _savedHistoryVersion;
        private bool _enabled, _closed, _disposed, _deferred, _messageCommitted, _pendingImmediate;
        private string? _message;
        internal InvocationCancellationScope(Agent owner, CancellationToken caller)
        {
            _owner = owner;
            Caller = caller;
            Token = _source.Token;
            try { _callerRegistration = caller.Register(static value => ((InvocationCancellationScope)value!).SignalCaller(), this, false); }
            catch { _source.Dispose(); throw; }
        }
        internal CancellationToken Caller { get; }
        internal bool AllowsPausedBoundary => !_owner.NativeCancellationCheckpoints;
        internal CancellationToken Token { get; }
        internal CancellationToken Signal { get { lock (_gate) return _enabled && !_closed ? Token : CancellationToken.None; } }
        internal bool Deferred { get { lock (_gate) return _deferred && !_closed; } }
        internal long HistoryVersion { get { lock (_gate) return _historyVersion; } }
        internal bool HasUnpersistedHistory { get { lock (_gate) return _historyVersion != _savedHistoryVersion; } }
        internal void MarkAcceptedHistory() { lock (_gate) _historyVersion = checked(_historyVersion + 1); }
        internal void MarkHistoryPersisted(long version) { lock (_gate) _savedHistoryVersion = Math.Max(version, _savedHistoryVersion); }
        internal void Seed(bool immediate, bool deferred, string? message)
        {
            lock (_gate) { _pendingImmediate = immediate; _deferred = deferred; _message = message; }
        }
        internal void Enable()
        {
            var signal = false;
            lock (_gate)
            {
                if (_closed) return;
                _enabled = true;
                if (_pendingImmediate) { _pendingImmediate = false; _cancellers++; signal = true; }
            }
            if (signal) SignalOutsideLock();
        }
        internal void Request(string? message, bool deferred)
        {
            lock (_gate)
            {
                if (_closed || (!_enabled && _owner._options.IdleCancellationBehavior == IdleCancellationBehavior.Ignore)) return;
                if (message is not null) _message = message;
                if (deferred) { _deferred = true; return; }
                if (!_enabled) { _pendingImmediate = true; return; }
                _cancellers++;
            }
            SignalOutsideLock();
        }
        private void SignalCaller()
        {
            lock (_gate) { if (_closed) return; _cancellers++; }
            SignalOutsideLock();
        }
        private void SignalOutsideLock()
        {
            try { _source.Cancel(); }
            finally
            {
                var release = false;
                lock (_gate) { _cancellers--; release = TryRelease(); }
                if (release) DisposeResources();
            }
        }
        internal bool TakeMessage(out string? message)
        {
            lock (_gate)
            {
                message = _message;
                if (_messageCommitted || message is null) return false;
                _messageCommitted = true;
                return true;
            }
        }
        public void Dispose()
        {
            lock (_owner._initializationGate)
                if (ReferenceEquals(_owner._invocationCancellation, this)) _owner._invocationCancellation = null;
            var release = false;
            lock (_gate) { _closed = true; _enabled = false; release = TryRelease(); }
            if (release) DisposeResources();
        }
        private bool TryRelease()
        {
            if (!_closed || _cancellers != 0 || _disposed) return false;
            _disposed = true;
            return true;
        }
        private void DisposeResources()
        {
            // Never join this registration while its callback can still be waiting for the invocation.
            _callerRegistration.Dispose();
            _source.Dispose();
        }
    }
}
