using Strands.Agents.Context;
using Strands.Agents.Conversation;

namespace Strands.Agents;

public sealed partial class Agent
{
    private PreparedSessionRestore PrepareSessionRestore(SessionSnapshot snapshot, bool catalog)
    {
        if (snapshot.SchemaVersion is not (1 or 2)) throw new InvalidDataException("Unsupported .NET session schema version.");
        if (snapshot.AgentId is not null && string.IsNullOrWhiteSpace(snapshot.AgentId)) throw new InvalidDataException("A stored agent identity cannot be blank.");
        snapshot.ValidateHistoryProfile();
        ValidateStoredHistoryProfile(snapshot.HistoryProfile);
        var systemPromptContent = snapshot.CopyValidatedSystemPromptContent(allowGuardContent: true);
        ConversationHistory.Validate(snapshot.Messages, snapshot.HistoryProfile);
        snapshot.ValidateModelHistory();
        snapshot.ValidateModelInputReceipt();
        ValidateRetainedHistoryMetadata(snapshot.ModelHistoryCount, snapshot.ModelInputReceipt);
        if (snapshot.ModelInputReceipt is not null && !IsStateful)
            throw new InvalidDataException("An input receipt requires a stateful model.");
        IReadOnlyList<Message>? sourceMessages = null;
        if (snapshot.SchemaVersion == 2)
        {
            sourceMessages = snapshot.Messages;
            try
            {
                snapshot = snapshot with
                {
                    Messages = Array.AsReadOnly(snapshot.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray()),
                    ModelState = AgentSnapshot.CloneValues(snapshot.ModelState!, nameof(snapshot.ModelState))
                };
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { throw new InvalidDataException("Version-two session input must contain detached valid JSON.", error); }
        }
        snapshot.InterruptState?.ValidateHistory(snapshot.Messages, allowIndependent: _options.ModelHistoryBehavior != ModelHistoryBehavior.Native, independentOwnerProfile: _options.ModelHistoryBehavior);
        var interrupted = snapshot.InterruptState?.Copy();
        var context = PrepareContextCheckpoint(snapshot);
        var conversation = PrepareConversationState(snapshot.ConversationManagerState);
        if (catalog)
        {
            _backgroundTasks?.ValidateStoredState(snapshot.State);
            if (context is not null && _contextManager is null) throw new InvalidDataException("Restoring context originals requires a matching context manager.");
        }
        IReadOnlyList<Message> messages = catalog
            ? Array.AsReadOnly(snapshot.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray())
            : snapshot.Messages;
        var state = snapshot.State is null || !catalog ? snapshot.State : AgentSnapshot.CloneValues(snapshot.State, nameof(snapshot.State));
        var modelState = snapshot.ModelState is null || !catalog ? snapshot.ModelState : AgentSnapshot.CloneValues(snapshot.ModelState, nameof(snapshot.ModelState));
        var copy = snapshot with { Messages = messages, State = state, ModelState = modelState, SystemPromptContent = systemPromptContent, InterruptState = interrupted, ContextStash = context?.Entries };
        return new(copy, context, conversation, sourceMessages);
    }

    private void ValidateStoredHistoryProfile(ModelHistoryBehavior profile)
    {
        if (profile != ModelHistoryBehavior.Native && profile != _options.ModelHistoryBehavior)
            throw new InvalidDataException("The stored history profile does not match the receiving agent.");
    }

    private void ApplyPreparedSessionRestore(PreparedSessionRestore prepared, bool initial)
    {
        var snapshot = prepared.Snapshot;
        if (initial && prepared.SourceMessages is { } source && _options.SessionStore is SessionRepositories.RepositoryAgentJournal journal)
            journal.RebindRestoredMessageCopies(source, snapshot.Messages);
        prepared.Conversation?.Apply();
        lock (_historyGate)
        {
            _messages.Clear();
            if (!UsesNativeStatefulHistory || snapshot.ModelHistoryCount is not null) _messages.AddRange(snapshot.Messages);
            _modelHistoryCount = UsesNativeStatefulHistory ? snapshot.ModelHistoryCount ?? 0 : 0;
            _modelInputReceipt = UsesNativeStatefulHistory ? snapshot.ModelInputReceipt : null;
            _interruptState = snapshot.InterruptState;
        }
        if (snapshot.State is not null) State.Replace(snapshot.State);
        if (snapshot.ModelState is not null) ModelState.Replace(snapshot.ModelState);
        if (snapshot.HasSystemPrompt) { SystemPrompt = snapshot.SystemPrompt; SystemPromptContent = snapshot.SystemPromptContent; }
        if (snapshot.AgentId is not null)
        {
            _id = snapshot.AgentId;
            _invocationTrace?.SetAttribute("gen_ai.agent.id", _id);
        }
        if (initial) SetRestoredContextCheckpoint(prepared.Context);
        else _backgroundTasks?.LoadState();
        _savedStateVersion = State.Version;
        _savedModelStateVersion = ModelState.Version;
        _savedConversationState = CaptureConversationState();
    }

    private sealed record PreparedSessionRestore(SessionSnapshot Snapshot, StashCheckpoint? Context, PreparedConversationState? Conversation, IReadOnlyList<Message>? SourceMessages);
}
