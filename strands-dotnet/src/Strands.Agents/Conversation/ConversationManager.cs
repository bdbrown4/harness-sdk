using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Conversation;

/// <summary>A stateful classic conversation reducer. This namespace is distinct from the original stateless projection API.</summary>
public abstract class ConversationManager : IConversationManager, IPlugin
{
    private Agent? _agent;
    private int _initialized;
    private int _reductions;
    private readonly SemaphoreSlim _reductionGate = new(1, 1);
    private static readonly AsyncLocal<ReductionScope?> ReductionContexts = new();
    private long _removedMessageCount;
    /// <summary>The native message JSON policy used inside classic checkpoints.</summary>
    protected static JsonSerializerOptions StateJson { get; } = new(JsonSerializerDefaults.Web);
    private readonly double? _compressionThreshold;
    /// <summary>Serializes access to built-in manager checkpoint fields.</summary>
    protected object StateGate { get; } = new();
    /// <summary>Creates a manager with an optional proactive utilization threshold.</summary>
    protected ConversationManager(double? proactiveCompressionThreshold = null)
    {
        if (proactiveCompressionThreshold is { } threshold && (!Guard.IsFinite(threshold) || threshold <= 0 || threshold > 1))
            throw new ArgumentOutOfRangeException(nameof(proactiveCompressionThreshold));
        _compressionThreshold = proactiveCompressionThreshold;
    }
    /// <summary>The number of original history messages removed by this manager.</summary>
    public long RemovedMessageCount { get => Interlocked.Read(ref _removedMessageCount); protected set => Interlocked.Exchange(ref _removedMessageCount, value); }
    /// <inheritdoc />
    public string Name => "strands:classic-conversation-manager";
    /// <summary>Returns the unchanged model projection; classic reducers manage durable history through lifecycle callbacks.</summary>
    public IReadOnlyList<Message> Reduce(IReadOnlyList<Message> messages) { Guard.NotNull(messages, nameof(messages)); return messages; }
    /// <summary>Captures detached Python-compatible manager state.</summary>
    public JsonElement GetState()
    {
        lock (StateGate)
        {
            var state = new Dictionary<string, object?> { ["__name__"] = GetType().Name, ["removed_message_count"] = RemovedMessageCount };
            WriteState(state);
            return JsonSerializer.SerializeToElement(state, StateJson);
        }
    }
    /// <summary>Validates and restores manager state, returning optional summary messages for append-only repository callers.</summary>
    public IReadOnlyList<Message>? RestoreFromSession(JsonElement state)
    {
        IReadOnlyList<Message>? Restore()
        {
            var prepared = PrepareState(state);
            prepared.Apply();
            return GetRepositoryPrepend();
        }
        return Volatile.Read(ref _agent) is { } agent ? agent.RestoreClassicManagerState(Restore) : Restore();
    }
    /// <summary>Adds implementation state to an owned checkpoint dictionary while StateGate is held.</summary>
    protected virtual void WriteState(IDictionary<string, object?> state) { }
    /// <summary>Validates and detaches implementation fields without mutation, returning a nonthrowing atomic assignment.</summary>
    protected virtual Action PrepareAdditionalState(JsonElement state) => static () => { };
    /// <summary>Returns detached previous-summary messages for append-only repository consumers. Complete native snapshots do not prepend them.</summary>
    protected virtual IReadOnlyList<Message>? GetRepositoryPrepend() => null;

    internal virtual IReadOnlyList<Message> PrepareRepositoryPrepend(JsonElement state) => Array.Empty<Message>();

    internal PreparedConversationState PrepareState(JsonElement state)
    {
        lock (StateGate) if (_reductions != 0) throw new InvalidOperationException("Manager state cannot be restored during an admitted context reduction.");
        try
        {
            var copy = state.Clone();
            if (copy.ValueKind != JsonValueKind.Object || !copy.TryGetProperty("__name__", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != GetType().Name)
                throw new InvalidDataException("Conversation manager checkpoint discriminator does not match the configured manager.");
            var removed = ReadCounter(copy, "removed_message_count");
            var apply = PrepareAdditionalState(copy);
            return new(copy, () => { lock (StateGate) { if (_reductions != 0) throw new InvalidOperationException("Manager state cannot be restored during an admitted context reduction."); apply(); RemovedMessageCount = removed; } });
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw new InvalidDataException("Conversation manager state is malformed.", error); }
    }
    internal static long ReadCounter(JsonElement state, string name, bool optional = false)
    {
        if (!state.TryGetProperty(name, out var field) && optional) return 0;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var value) || value < 0)
            throw new InvalidDataException("Conversation manager counters must be nonnegative Int64 values.");
        return value;
    }
    internal static JsonElement ValidateEnvelope(JsonElement state)
    {
        try
        {
            var copy = state.Clone();
            if (copy.ValueKind != JsonValueKind.Object || !copy.TryGetProperty("__name__", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidDataException("Conversation manager state requires a class discriminator.");
            _ = ReadCounter(copy, "removed_message_count");
            if (name.GetString() == nameof(SlidingWindowConversationManager)) _ = new SlidingWindowConversationManager().PrepareState(copy);
            if (name.GetString() == nameof(SummarizingConversationManager)) _ = new SummarizingConversationManager().PrepareState(copy);
            return copy;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw new InvalidDataException("Conversation manager checkpoint is malformed.", error); }
    }
    /// <summary>Applies routine end-of-invocation management to durable history.</summary>
    public ValueTask ApplyManagementAsync(Agent agent, CancellationToken cancellationToken = default)
        => RunReductionAsync(agent, () => ApplyManagementCoreAsync(agent, cancellationToken), cancellationToken);
    internal ValueTask ApplyDirectToolManagementAsync(Agent agent, Agent.DirectToolOperation operation, CancellationToken cancellationToken)
        => RunReductionCoreAsync(agent, () => ApplyManagementCoreAsync(agent, cancellationToken), cancellationToken, null, operation);
    /// <summary>Reduces history proactively or in response to a provider overflow.</summary>
    public ValueTask ReduceContextAsync(Agent agent, Exception? error = null, CancellationToken cancellationToken = default)
        => RunReductionAsync(agent, () => ReduceContextCoreAsync(agent, error, cancellationToken), cancellationToken);
    /// <summary>Applies routine management within an admitted, serialized operation.</summary>
    protected abstract ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken);
    /// <summary>Implements one reduction within an admitted, serialized operation.</summary>
    protected abstract ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken);
    /// <summary>Observes an admitted before-model callback, including custom per-turn cadence.</summary>
    protected virtual ValueTask BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken) => default;
    /// <inheritdoc />
    public virtual ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        CheckAgent(agent, cancellationToken);
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return default;
        agent.Hooks.Register<BeforeModelCallEvent>((value, token) => RunLifecycleAsync(agent, async () =>
        {
            if (_compressionThreshold is { } threshold)
            {
                try
                {
                    var used = await value.Agent.Model.CountTokensAsync(value.Request, token).ConfigureAwait(false);
                    if (used / (double)value.Agent.Model.GetLimits().ContextWindowLimit!.Value >= threshold)
                    {
                        await ReduceContextCoreAsync(value.Agent, null, token).ConfigureAwait(false);
                        value.Request = value.Request with { Messages = value.Agent.Messages };
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            await BeforeModelCallAsync(value, token).ConfigureAwait(false);
        }, token));
        agent.Hooks.Register<AfterModelCallEvent>(async (value, _) =>
        {
            if (value.Error is not ContextWindowOverflowException || value.InvocationCancellationToken.IsCancellationRequested) return;
            var before = value.Agent.Messages;
            await RunLifecycleAsync(agent, () => ReduceContextCoreAsync(value.Agent, value.Error, value.InvocationCancellationToken), value.InvocationCancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(before, value.Agent.Messages) && !before.SequenceEqual(value.Agent.Messages)) value.Retry = true;
        });
        agent.Hooks.Register<AfterInvocationEvent>((value, token) => value.Agent.InterruptState is null ? RunLifecycleAsync(agent, () => ApplyManagementCoreAsync(value.Agent, token), token) : default);
        return default;
    }
    /// <summary>Checks cancellation and binds this stateful manager to exactly one agent.</summary>
    protected void CheckAgent(Agent agent, CancellationToken cancellationToken)
    {
        Guard.NotNull(agent, nameof(agent)); cancellationToken.ThrowIfCancellationRequested();
        agent.ValidateClassicManagerUse();
        if (Interlocked.CompareExchange(ref _agent, agent, null) is { } previous && !ReferenceEquals(previous, agent))
            throw new InvalidOperationException("A classic conversation manager binds to one agent; create a fresh manager for each agent.");
    }
    /// <summary>Serializes asynchronous reductions, rejects callback self-waits and gives requested caller cancellation precedence.</summary>
    protected ValueTask RunReductionAsync(Agent agent, Func<ValueTask> operation, CancellationToken cancellationToken)
        => RunReductionCoreAsync(agent, operation, cancellationToken, null);
    private async ValueTask RunLifecycleAsync(Agent agent, Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        var receipt = new CallbackAdmission(this, agent);
        try { await RunReductionCoreAsync(agent, operation, cancellationToken, receipt).ConfigureAwait(false); }
        finally { receipt.Close(); }
    }
    private async ValueTask RunReductionCoreAsync(Agent agent, Func<ValueTask> operation, CancellationToken cancellationToken,
        CallbackAdmission? callback, Agent.DirectToolOperation? directOperation = null)
    {
        Guard.NotNull(agent, nameof(agent)); cancellationToken.ThrowIfCancellationRequested();
        for (var scope = ReductionContexts.Value; scope is not null; scope = scope.Parent)
            if (scope.IsOpen && ReferenceEquals(scope.Manager, this)) throw new InvalidOperationException("A classic reduction callback cannot reenter the same manager.");
        var fromCallback = callback is not null && callback.IsOpen && ReferenceEquals(callback.Manager, this) && ReferenceEquals(callback.Agent, agent);
        var admission = directOperation is null ? agent.EnterClassicReduction(fromCallback) : agent.EnterDirectToolReduction(directOperation, this);
        Exception? primary = null;
        try
        {
            CheckAgent(agent, cancellationToken);
            lock (StateGate) _reductions++;
            var entered = false;
            var previous = ReductionContexts.Value;
            ReductionScope? current = null;
            try
            {
                await _reductionGate.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
                ReductionContexts.Value = current = new(this, previous);
                await operation().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure) when (cancellationToken.IsCancellationRequested && (failure is not OperationCanceledException canceled || canceled.CancellationToken != cancellationToken))
            { throw new OperationCanceledException("Classic conversation reduction was cancelled.", failure, cancellationToken); }
            finally
            {
                current?.Close(); ReductionContexts.Value = previous;
                lock (StateGate) _reductions--;
                if (entered) _reductionGate.Release();
            }
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { admission.Dispose(); }
            catch (Exception cleanup) when (directOperation is not null && primary is not null) { directOperation.AttachCleanup(primary, cleanup); }
        }
    }
    /// <summary>Rethrows the original reactive overflow when no valid reduction is available.</summary>
    protected static void CannotReduce(Exception? error)
    { if (error is not null) ExceptionDispatchInfo.Capture(error).Throw(); }
    internal virtual ConversationManager CreateForAgent() => throw new NotSupportedException("Custom classic managers must supply a fresh child binding explicitly.");
    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => [];
    private sealed class ReductionScope(ConversationManager manager, ReductionScope? parent)
    {
        private int _open = 1;
        internal ConversationManager Manager { get; } = manager;
        internal ReductionScope? Parent { get; } = parent;
        internal bool IsOpen => Volatile.Read(ref _open) != 0;
        internal void Close() => Volatile.Write(ref _open, 0);
    }
    private sealed class CallbackAdmission(ConversationManager manager, Agent agent)
    {
        private int _open = 1;
        internal ConversationManager Manager { get; } = manager;
        internal Agent Agent { get; } = agent;
        internal bool IsOpen => Volatile.Read(ref _open) != 0;
        internal void Close() => Volatile.Write(ref _open, 0);
    }
}

internal sealed class PreparedConversationState(JsonElement data, Action apply)
{
    internal JsonElement Data { get; } = data;
    internal void Apply() => apply();
}
