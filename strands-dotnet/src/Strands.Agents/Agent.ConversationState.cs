using System.Text.Json;
using Strands.Agents.Conversation;

namespace Strands.Agents;

public sealed partial class Agent
{
    private int _classicReductions;
    private JsonElement? _savedConversationState;
    private ConversationManager? ClassicManager => _contextManager is null ? _plugins.OfType<ConversationManager>().SingleOrDefault() : null;
    private JsonElement? CaptureConversationState() => ClassicManager is { } manager ? ConversationManager.ValidateEnvelope(manager.GetState()) : null;
    private PreparedConversationState? PrepareConversationState(JsonElement? state)
    {
        if (state is null || state.Value.ValueKind == JsonValueKind.Null) return null;
        var copy = ConversationManager.ValidateEnvelope(state.Value);
        return ClassicManager?.PrepareState(copy) ?? throw new InvalidDataException("The checkpoint contains classic conversation state but no matching classic manager is configured.");
    }
    private bool ConversationStateChanged()
    {
        var current = CaptureConversationState();
        return current.HasValue != _savedConversationState.HasValue || current is { } value && !JsonElement.DeepEquals(value, _savedConversationState!.Value);
    }
    private void RecordSavedConversationState(SessionSnapshot snapshot) => _savedConversationState = snapshot.ConversationManagerState?.Clone();
    internal IReadOnlyList<Message>? RestoreClassicManagerState(Func<IReadOnlyList<Message>?> restore)
    {
        lock (_initializationGate)
        {
            ValidateSnapshotAccess();
            return restore();
        }
    }
    internal async ValueTask RestoreClassicSummarizerAsync(AgentSnapshot snapshot)
    {
        await LoadSnapshotAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        await SaveSessionAsync(CancellationToken.None).ConfigureAwait(false);
    }
    internal IDisposable EnterClassicReduction(bool fromRegisteredCallback)
        => EnterClassicReductionCore(fromRegisteredCallback, null, null);
    internal IDisposable EnterDirectToolReduction(DirectToolOperation operation, ConversationManager manager)
        => EnterClassicReductionCore(false, operation, manager);
    private IDisposable EnterClassicReductionCore(bool fromRegisteredCallback, DirectToolOperation? directOperation, ConversationManager? directManager)
    {
        Action? providerUpdateReport;
        lock (_initializationGate)
        {
            ValidateClassicManagerUse();
            if (_catalogOperation is not null) throw new ConcurrentInvocationException();
            if (directOperation is not null) ValidateDirectToolManagement(directOperation, directManager!);
            else if (!fromRegisteredCallback && (Volatile.Read(ref _invoking) != 0 || _directToolCalls != 0 || _initialization is { IsCompleted: false })) throw new ConcurrentInvocationException();
            if (InterruptState is not null) throw new InvalidOperationException("Classic reduction cannot replace a paused tool batch's history.");
            providerUpdateReport = PublishPendingProviderUpdatesUnderGate();
            _classicReductions++;
        }
        try { providerUpdateReport?.Invoke(); }
        catch
        {
            if (directOperation is not null) lock (_initializationGate) _classicReductions--;
            throw;
        }
        return new ClassicReductionAdmission(this);
    }
    internal void ValidateClassicManagerUse()
    {
        ThrowIfDisposed();
        if (IsStateful) throw new InvalidOperationException("Classic history reducers cannot modify server-managed history.");
    }
    internal async ValueTask CommitClassicContextAsync(IReadOnlyList<Message> expected, IReadOnlyList<Message> replacement, Action commitState, CancellationToken cancellationToken)
    {
        ConversationHistory.Validate(replacement);
        cancellationToken.ThrowIfCancellationRequested();
        var repositoryReduction = PrepareRepositoryReduction(expected, replacement);
        lock (_historyGate)
        {
            if (_messages.Count != expected.Count || _messages.Where((message, index) => !ReferenceEquals(message, expected[index])).Any())
                throw new InvalidOperationException("Conversation changed while classic reduction was being prepared.");
            commitState();
            _messages.Clear(); _messages.AddRange(replacement);
        }
        repositoryReduction?.Accept();
        await SaveSessionAsync(cancellationToken).ConfigureAwait(false);
    }
    private sealed class ClassicReductionAdmission(Agent agent) : IDisposable
    {
        public void Dispose()
        {
            Action? report;
            lock (agent._initializationGate) { agent._classicReductions--; report = agent.PublishPendingProviderUpdatesUnderGate(); }
            report?.Invoke();
        }
    }
}
