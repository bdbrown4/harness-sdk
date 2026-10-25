namespace Strands.Agents;

public sealed partial class Agent
{
    private bool _abortingInterrupts;
    /// <summary>Settles a paused tool batch without replaying tool bodies. Accepted sibling results are retained; unfinished calls receive the supplied error reason. Returns false when no batch is paused.</summary>
    /// <remarks>Requires an inactive agent. Cancellation is observed before persistence; once accepted, the durable transition and its message notification complete without cancellation.</remarks>
    public async ValueTask<bool> AbortInterruptsAsync(string reason, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(reason, nameof(reason)); cancellationToken.ThrowIfCancellationRequested();
        lock (_initializationGate) ValidateSnapshotAccess();
        await TaskCancellation.WaitAsync(InitializeAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        lock (_initializationGate)
        {
            ValidateSnapshotAccess(); cancellationToken.ThrowIfCancellationRequested();
            _invoking = 1; _abortingInterrupts = true;
        }
        try
        {
            InterruptState pending; IReadOnlyList<Message> history;
            lock (_historyGate)
            {
                if (_interruptState is null) return false;
                pending = _interruptState.Copy(); history = Array.AsReadOnly(_messages.ToArray());
            }
            pending.ValidateHistory(history, allowIndependent: _options.ModelHistoryBehavior != ModelHistoryBehavior.Native, independentOwnerProfile: _options.ModelHistoryBehavior);
            var completed = pending.CompletedToolResults.ToDictionary(result => result.ToolUseId, StringComparer.Ordinal);
            var results = pending.AssistantMessage.Content.OfType<ToolUseBlock>().Select(call => (ContentBlock)(completed.TryGetValue(call.ToolUseId, out var result)
                ? result : new ToolResultBlock(call.ToolUseId, reason, ToolResultStatus.Error))).ToArray();
            var message = new Message(Role.User, results);
            Message[] appended = pending.AssistantMessageIsIndependent && _options.ModelHistoryBehavior == ModelHistoryBehavior.TypeScript
                ? [pending.AssistantMessage, message] : [message];
            var write = _options.SessionStore is null ? null : await PrepareSessionWriteAsync(history.Concat(appended).ToArray(), null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (write is not null)
            {
                await PersistRepositoryAbortAsync(message, write.Snapshot, CancellationToken.None).ConfigureAwait(false);
                _savedStateVersion = write.StateVersion; _savedModelStateVersion = write.ModelStateVersion;
                _restoredSourceHistoryDirty = false;
                RecordSavedConversationState(write.Snapshot);
            }
            lock (_historyGate) { _messages.AddRange(appended); _interruptState = null; }
            Exception? primary = null;
            try
            {
                foreach (var added in appended)
                    await Hooks.InvokeAsync(new MessageAddedEvent(this, added), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                try { await SaveSessionAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) when (primary is not null) { AttachCleanupError(primary, cleanup); }
            }
            return true;
        }
        finally
        {
            Action? report;
            lock (_initializationGate) { _abortingInterrupts = false; Volatile.Write(ref _invoking, 0); report = PublishPendingProviderUpdatesUnderGate(); }
            report?.Invoke();
        }
    }
    private async ValueTask<SessionWrite> PrepareSessionWriteAsync(IReadOnlyList<Message> messages, InterruptState? interrupts, CancellationToken cancellationToken)
    {
        if (_options.SessionStore is SessionCatalogs.SnapshotSessionStore)
            return await PrepareCatalogSessionWriteAsync(messages, interrupts, cancellationToken).ConfigureAwait(false);
        var state = State.GetSnapshot(out var version);
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> modelState;
        long modelVersion;
        int? modelHistoryCount;
        ModelInputReceipt? inputReceipt;
        lock (_historyGate)
        {
            modelState = ModelState.GetSnapshot(out modelVersion);
            modelHistoryCount = UsesNativeStatefulHistory ? _modelHistoryCount : null;
            inputReceipt = UsesNativeStatefulHistory ? _modelInputReceipt : null;
        }
        var conversationState = CaptureConversationState();
        var prompt = SystemPrompt;
        var promptContent = SystemPromptContent;
        var stash = _contextManager?.Stash;
        var entries = stash is { IsDurable: false } ? await stash.TakeSnapshotAsync(cancellationToken).ConfigureAwait(false) : null;
        var snapshot = new SessionSnapshot(inputReceipt is null ? 1 : 2, messages)
        {
            HistoryProfile = _options.ModelHistoryBehavior,
            State = state,
            ModelState = modelState,
            HasSystemPrompt = true,
            SystemPrompt = prompt,
            SystemPromptContent = promptContent,
            InterruptState = interrupts,
            ModelHistoryCount = modelHistoryCount,
            ModelInputReceipt = inputReceipt,
            AgentId = Id,
            ContextStash = entries,
            ContextStashStorageType = stash is { IsDurable: true } ? stash.StorageTypeName : null,
            ConversationManagerState = conversationState
        };
        return new(snapshot, version, modelVersion);
    }
    private sealed record SessionWrite(SessionSnapshot Snapshot, long StateVersion, long ModelStateVersion);
}
