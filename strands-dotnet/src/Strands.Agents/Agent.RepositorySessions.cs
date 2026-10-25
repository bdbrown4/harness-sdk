using Strands.Agents.SessionRepositories;

namespace Strands.Agents;

public sealed partial class Agent
{
    private PendingRepositoryRedaction? _pendingRepositoryRedaction;
    internal RepositoryAgentJournal GetRepositoryJournal(RepositorySessionManager manager)
    {
        if (_options.SessionStore is not RepositoryAgentJournal journal || !ReferenceEquals(journal.Manager, manager))
            throw new InvalidOperationException("The indexed manager is not bound to this agent.");
        return journal;
    }

    internal async ValueTask<SessionSnapshot> CaptureRepositorySnapshotAsync(CancellationToken cancellationToken)
        => (await PrepareSessionWriteAsync(Messages, InterruptState, cancellationToken).ConfigureAwait(false)).Snapshot;

    internal async ValueTask SynchronizeRepositoryJournalAsync(RepositoryAgentJournal journal, CancellationToken cancellationToken)
        => await journal.SaveAsync(journal.Manager.SessionId, await CaptureRepositorySnapshotAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private RepositoryAppendReceipt? PrepareRepositoryAppend(IReadOnlyList<Message> messages)
        => (_options.SessionStore as RepositoryAgentJournal)?.PrepareAppend(messages);

    private void ReleaseRepositoryBinding() => (_options.SessionStore as RepositoryAgentJournal)?.Release();

    private ValueTask RetryRepositoryWritesAsync(CancellationToken cancellationToken)
        => _options.SessionStore is RepositoryAgentJournal ? SaveSessionAsync(cancellationToken) : default;

    private RepositoryReductionReceipt? PrepareRepositoryReduction(IReadOnlyList<Message> expected, IReadOnlyList<Message> replacement)
        => (_options.SessionStore as RepositoryAgentJournal)?.PrepareReduction(expected, replacement, ClassicManager is Conversation.SummarizingConversationManager);

    private RepositoryHistoryClearReceipt? PrepareRepositoryAcknowledgedHistoryClear(IReadOnlyList<Message> expected)
        => (_options.SessionStore as RepositoryAgentJournal)?.PrepareAcknowledgedHistoryClear(expected);

    internal void ValidateRepositorySnapshot(SessionSnapshot snapshot)
    {
        if (snapshot.SchemaVersion is not (1 or 2)) throw new InvalidDataException("Unsupported native indexed session schema.");
        if (!IsStateful && snapshot.ModelHistoryCount is not null) throw new InvalidDataException("A stateless indexed session cannot restore a provider acknowledgement cursor.");
        _ = snapshot.CopyValidatedSystemPromptContent(allowGuardContent: true);
        _ = PrepareContextCheckpoint(snapshot);
        _backgroundTasks?.ValidateStoredState(snapshot.State);
    }

    private async ValueTask CommitRepositoryRedactionAsync(Message original, Message replacement)
    {
        if (_options.SessionStore is not RepositoryAgentJournal journal) return;
        var receipt = journal.PrepareRedaction(original, replacement);
        if (_pendingRepositoryRedaction is not null) throw new InvalidOperationException("An earlier indexed redaction still requires persistence.");
        _pendingRepositoryRedaction = new(receipt, original, replacement);
        MarkAcceptedHistory(CancellationToken.None, Volatile.Read(ref _invocationCancellation));
        await FlushRepositoryRedactionsAsync().ConfigureAwait(false);
    }

    private async ValueTask FlushRepositoryRedactionsAsync()
    {
        if (_pendingRepositoryRedaction is not { } pending) return;
        await pending.Receipt.PersistAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_historyGate)
            for (var index = 0; index < _messages.Count; index++)
                if (ReferenceEquals(_messages[index], pending.Original)) _messages[index] = pending.Replacement;
        _pendingRepositoryRedaction = null;
    }

    private sealed record PendingRepositoryRedaction(RepositoryRedactionReceipt Receipt, Message Original, Message Replacement);

    private ValueTask PersistRepositoryAbortAsync(Message message, SessionSnapshot snapshot, CancellationToken cancellationToken)
        => _options.SessionStore is RepositoryAgentJournal journal ? journal.PersistAbortAsync(message, snapshot, cancellationToken) :
            _options.SessionStore!.SaveAsync(SessionId!, snapshot, cancellationToken);

    internal (int Offset, IReadOnlyList<Message> Prepend) PrepareRepositoryConversation(System.Text.Json.JsonElement? state)
    {
        if (state is null) return (0, Array.Empty<Message>());
        var prepared = PrepareConversationState(state)!;
        int offset;
        try { offset = checked((int)Conversation.ConversationManager.ReadCounter(prepared.Data, "removed_message_count")); }
        catch (OverflowException error) { throw new InvalidDataException("The indexed row offset exceeds the repository pagination range.", error); }
        return (offset, ClassicManager!.PrepareRepositoryPrepend(prepared.Data));
    }
}
