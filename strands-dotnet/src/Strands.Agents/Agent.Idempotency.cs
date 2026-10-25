using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents;

public sealed partial class Agent
{
    private InvocationIdempotencyReceipt? _inflightIdempotency;
    private static readonly AsyncLocal<IdempotencyContext?> IdempotencyFlow = new();
    [ThreadStatic] private static IdempotencyPrefix? _idempotencyPrefix;

    /// <summary>Streams model events and committed messages. Disposing the enumerator releases the invocation and repairs pending tool requests.</summary>
    /// <remarks>A matching in-flight idempotency token yields only the original final result, after physical cleanup. Canceling a duplicate cancels only its wait.</remarks>
    public async IAsyncEnumerable<IAgentStreamEvent> StreamAsync(string? prompt, InvocationOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options = PrepareOrdinaryStructuredOutput(options ?? new());
        ValidateCheckpointResume(prompt, options);
        if (options?.IdempotencyToken is null)
        {
            await foreach (var value in StreamInvocationCoreAsync(prompt, options ?? new(), null, null, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        options = CopyEphemeralTools(CopyInterruptResponses(options));
        ValidateInvocationOptions(options);
        var admission = BeginIdempotentInvocation(options.IdempotencyToken!);
        if (!admission.Primary)
        {
            await TaskCancellation.WaitAsync(admission.Receipt.Done.Task, cancellationToken).ConfigureAwait(false);
            admission.Receipt.Failure?.Throw();
            yield return new AgentResultEvent(this, admission.Receipt.Result!);
            yield break;
        }

        var receipt = admission.Receipt;
        IAsyncEnumerator<IAgentStreamEvent>? iterator = null;
        AgentResult? result = null;
        Exception? primary = null;
        try
        {
            iterator = StreamInvocationCoreAsync(prompt, options, receipt, admission.Report, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool next;
                try { next = await MoveIdempotentAsync(iterator, receipt).ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                if (!next) break;
                if (iterator.Current is AgentResultEvent completed) result = completed.Result;
                yield return iterator.Current;
            }
        }
        finally
        {
            var hadPrimary = primary is not null;
            Exception? cleanup = null;
            try { if (iterator is not null) await DisposeIdempotentAsync(iterator, receipt).ConfigureAwait(false); }
            catch (Exception error)
            {
                cleanup = error;
                if (primary is null) primary = error;
                else AttachIdempotencyCleanup(primary, error);
            }
            finally { CompleteIdempotentInvocation(receipt, result, primary); }
            if (!hadPrimary && cleanup is not null) ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }

    private (InvocationIdempotencyReceipt Receipt, bool Primary, Action? Report) BeginIdempotentInvocation(string token)
    {
        var candidate = new InvocationIdempotencyReceipt(token);
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            if (_inflightIdempotency is { } existing)
            {
                if (!StringComparer.Ordinal.Equals(existing.Token, token)) throw new ConcurrentInvocationException();
                RefuseIdempotencySelfWait(existing);
                return (existing, false, null);
            }
            if (_directToolCalls != 0 || _classicReductions != 0 || _catalogOperation is not null) throw new ConcurrentInvocationException();
            var report = PublishPendingProviderUpdatesUnderGate();
            if (Interlocked.CompareExchange(ref _invoking, 1, 0) != 0) throw new ConcurrentInvocationException();
            BeginGoalInvocationIdentity();
            BeginCancellationAdmission();
            _inflightIdempotency = candidate;
            return (candidate, true, report);
        }
    }

    private void CompleteIdempotentInvocation(InvocationIdempotencyReceipt receipt, AgentResult? result, Exception? failure)
    {
        receipt.Result = failure is null ? result : null;
        receipt.Failure = failure is not null ? ExceptionDispatchInfo.Capture(failure)
            : result is null ? ExceptionDispatchInfo.Capture(new IdempotencyAbortedException()) : null;
        receipt.Active = false;
        lock (_initializationGate)
        {
            // Core cleanup already released its own admission. Another lifecycle (such as Abort) may now own it.
            // Only remove this private slot; never clear _invoking/cancellation or any other operation's lease again.
            if (ReferenceEquals(_inflightIdempotency, receipt)) _inflightIdempotency = null;
        }
        receipt.Done.TrySetResult(true);
    }

    private static void RefuseIdempotencySelfWait(InvocationIdempotencyReceipt receipt)
    {
        for (var scope = IdempotencyFlow.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Receipt, receipt) && receipt.Active)
                throw new InvalidOperationException("An invocation callback cannot join its own in-flight idempotency token.");
        for (var scope = _idempotencyPrefix; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Receipt, receipt) && scope.Active && receipt.Active)
                throw new InvalidOperationException("An invocation callback cannot join its own in-flight idempotency token.");
    }

    private static ValueTask<bool> MoveIdempotentAsync(IAsyncEnumerator<IAgentStreamEvent> iterator, InvocationIdempotencyReceipt receipt)
    {
        var flow = IdempotencyFlow.Value;
        var prefix = _idempotencyPrefix;
        var current = new IdempotencyPrefix(receipt, prefix);
        IdempotencyFlow.Value = new(receipt, flow);
        _idempotencyPrefix = current;
        try { return iterator.MoveNextAsync(); }
        finally { current.Active = false; _idempotencyPrefix = prefix; IdempotencyFlow.Value = flow; }
    }

    private static ValueTask DisposeIdempotentAsync(IAsyncEnumerator<IAgentStreamEvent> iterator, InvocationIdempotencyReceipt receipt)
    {
        var flow = IdempotencyFlow.Value;
        var prefix = _idempotencyPrefix;
        var current = new IdempotencyPrefix(receipt, prefix);
        IdempotencyFlow.Value = new(receipt, flow);
        _idempotencyPrefix = current;
        try { return iterator.DisposeAsync(); }
        finally { current.Active = false; _idempotencyPrefix = prefix; IdempotencyFlow.Value = flow; }
    }

    private static AgentResult BindIdempotentStructuredResult(AgentResult result, InvocationOptions options, InvocationIdempotencyReceipt? receipt)
    {
        if (receipt is not null && result.StructuredOutputToolUseId is { } id && options.StructuredOutputTool is { } tool && tool.TryGetResult(id, out var value))
            return result with { AcceptedIdempotentStructuredValue = (id, value) };
        return result;
    }

    private static void AttachIdempotencyCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is List<Exception> errors && errors.Any(value => ReferenceEquals(value, cleanup))) return;
        AttachCleanupError(primary, cleanup);
    }

    private sealed class InvocationIdempotencyReceipt(string token)
    {
        internal string Token { get; } = token;
        internal TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AgentResult? Result;
        internal ExceptionDispatchInfo? Failure;
        internal volatile bool Active = true;
    }
    private sealed record IdempotencyContext(InvocationIdempotencyReceipt Receipt, IdempotencyContext? Parent);
    private sealed class IdempotencyPrefix(InvocationIdempotencyReceipt receipt, IdempotencyPrefix? parent)
    {
        internal InvocationIdempotencyReceipt Receipt { get; } = receipt;
        internal IdempotencyPrefix? Parent { get; } = parent;
        internal bool Active = true;
    }
}
