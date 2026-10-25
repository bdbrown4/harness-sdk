using Strands.Agents.SessionCatalogs;

namespace Strands.Agents;

public sealed partial class Agent
{
    private CatalogOperation? _catalogOperation;

    internal SnapshotSessionStore GetSnapshotSessionStore(SnapshotSessionManager manager)
    {
        lock (_initializationGate)
        {
            ThrowIfDisposed();
            if (_options.SessionStore is not SnapshotSessionStore store || store.IsReleased || !ReferenceEquals(store.Manager, manager) || !ReferenceEquals(store.Owner, this))
                throw new InvalidOperationException("The snapshot catalog is not bound to this agent.");
            return store;
        }
    }

    private void ReleaseSnapshotSessionBinding() => (_options.SessionStore as SnapshotSessionStore)?.Release();

    internal void ValidateCatalogSession(SessionSnapshot snapshot) => _ = PrepareSessionRestore(snapshot, catalog: true);

    private async ValueTask<SessionWrite> PrepareCatalogSessionWriteAsync(IReadOnlyList<Message> messages, InterruptState? interrupts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = State.GetSnapshot(out var version);
        IReadOnlyDictionary<string, System.Text.Json.JsonElement> modelState;
        long modelVersion;
        int? count;
        ModelInputReceipt? inputReceipt;
        lock (_historyGate)
        {
            modelState = ModelState.GetSnapshot(out modelVersion);
            count = UsesNativeStatefulHistory ? (int?)_modelHistoryCount : null;
            inputReceipt = UsesNativeStatefulHistory ? _modelInputReceipt : null;
        }
        var conversation = CaptureConversationState();
        var prompt = SystemPrompt;
        var promptContent = SystemPromptContent;
        var id = Id;
        var copiedMessages = Array.AsReadOnly(messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray());
        var copiedInterrupts = interrupts?.Copy();
        var stash = _contextManager?.Stash;
        var entries = stash is { IsDurable: false } ? await stash.TakeSnapshotAsync(cancellationToken).ConfigureAwait(false) : null;
        var snapshot = new SessionSnapshot(inputReceipt is null ? 1 : 2, copiedMessages)
        {
            HistoryProfile = _options.ModelHistoryBehavior,
            State = state,
            ModelState = modelState,
            HasSystemPrompt = true,
            SystemPrompt = prompt,
            SystemPromptContent = promptContent,
            InterruptState = copiedInterrupts,
            ModelHistoryCount = count,
            ModelInputReceipt = inputReceipt,
            AgentId = id,
            ContextStash = entries,
            ContextStashStorageType = stash is { IsDurable: true } ? stash.StorageTypeName : null,
            ConversationManagerState = conversation
        };
        return new(snapshot, version, modelVersion);
    }

    internal ValueTask<string?> SaveCatalogSnapshotAsync(SnapshotSessionManager manager, bool latest, CancellationToken cancellationToken)
        => WithCatalogOperationAsync(manager, async operation =>
        {
            await InitializeForCatalogAsync(operation, null, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var write = await PrepareSessionWriteAsync(Messages, InterruptState, cancellationToken).ConfigureAwait(false);
            _ = PrepareSessionRestore(write.Snapshot, catalog: true);
            var id = await operation.Store.WriteSnapshotAsync(write.Snapshot, latest, cancellationToken).ConfigureAwait(false);
            _restoredSourceHistoryDirty = false;
            return id;
        }, cancellationToken);

    internal ValueTask<bool> RestoreCatalogSnapshotAsync(SnapshotSessionManager manager, string? id, CancellationToken cancellationToken)
        => WithCatalogOperationAsync(manager, async operation =>
        {
            var snapshot = await operation.Store.ReadSnapshotAsync(id, cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return false;
            var prepared = PrepareSessionRestore(snapshot, catalog: true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!operation.WasInitialized)
            {
                await InitializeForCatalogAsync(operation, prepared, cancellationToken).ConfigureAwait(false);
                return true;
            }
            _backgroundTasks?.AssertCanLoadSnapshot();
            _contextManager?.ValidateCatalogRestore();
            var derived = _contextManager is null ? null : await _contextManager.PrepareCatalogRestoreAsync(prepared.Context, prepared.Snapshot.Messages, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_initializationGate)
            {
                ValidateCatalogOperation(operation);
                _backgroundTasks?.AssertCanLoadSnapshot();
                cancellationToken.ThrowIfCancellationRequested();
            }
            // The exclusive reservation remains held. Custom classic assignments/getters
            // execute outside the admission monitor; there is no await in this commit.
            ApplyPreparedSessionRestore(prepared, initial: false);
            if (derived is not null) _contextManager!.CommitCatalogRestore(derived);
            return true;
        }, cancellationToken);

    private async ValueTask<T> WithCatalogOperationAsync<T>(SnapshotSessionManager manager, Func<CatalogOperation, ValueTask<T>> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CatalogOperation operation;
        lock (_initializationGate)
        {
            ValidateSnapshotAccess();
            if (_inflightIdempotency is not null) throw new ConcurrentInvocationException();
            var store = GetSnapshotSessionStore(manager);
            operation = new(store, _initialization is { Status: TaskStatus.RanToCompletion });
            _catalogOperation = operation;
        }
        Exception? primary = null;
        try { return await body(operation).ConfigureAwait(false); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            {
                Action? report;
                lock (_initializationGate)
                {
                    if (ReferenceEquals(_catalogOperation, operation)) _catalogOperation = null;
                    report = PublishPendingProviderUpdatesUnderGate();
                }
                report?.Invoke();
            }
            catch (Exception cleanup) when (primary is not null) { AttachCleanupError(primary, cleanup); }
        }
    }

    internal Action ReserveCatalogDeletion(SnapshotSessionManager manager, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CatalogOperation operation;
        lock (_initializationGate)
        {
            ValidateSnapshotAccess();
            if (_inflightIdempotency is not null) throw new ConcurrentInvocationException();
            operation = new(GetSnapshotSessionStore(manager), _initialization is { Status: TaskStatus.RanToCompletion });
            _catalogOperation = operation;
        }
        var released = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            Action? report;
            lock (_initializationGate)
            {
                if (ReferenceEquals(_catalogOperation, operation)) _catalogOperation = null;
                report = PublishPendingProviderUpdatesUnderGate();
            }
            report?.Invoke();
        };
    }

    internal bool HasCatalogSessionStash => _contextManager?.Stash is not null;

    internal ValueTask ClearCatalogSessionStashAsync(CancellationToken cancellationToken)
        => _contextManager?.Stash?.ClearSessionAsync(cancellationToken) ?? default;

    private bool UsesCatalogMessageStrategy => _options.SessionStore is SnapshotSessionStore catalog && catalog.Manager.SaveLatestOn == SnapshotSessionSaveStrategy.Message;

    private async ValueTask CompleteCatalogInvocationAsync(SnapshotSessionStore store, CancellationToken cancellationToken)
    {
        // A failed initialization has no completion checkpoint and releases its binding.
        if (!_restored || store.IsReleased || _initialization is not { Status: TaskStatus.RanToCompletion }) return;
        cancellationToken.ThrowIfCancellationRequested();
        var triggered = false;
        var triggerFailed = false;
        if (store.Manager.SnapshotTrigger is { } trigger)
        {
            try { triggered = trigger(this); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                triggerFailed = true;
                try { SdkLogging.Warning(error, "Snapshot trigger failed; immutable checkpoint skipped: {Error}", error.Message); }
                catch (Exception) { /* Diagnostic failures cannot discard the required latest write. */ }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!triggered && !triggerFailed && store.Manager.SaveLatestOn == SnapshotSessionSaveStrategy.Trigger) return;
        var cancellation = Volatile.Read(ref _invocationCancellation);
        var historyVersion = cancellation?.HistoryVersion ?? 0;
        var write = await PrepareSessionWriteAsync(Messages, InterruptState, cancellationToken).ConfigureAwait(false);
        if (triggered) await store.WriteImmutableAndLatestAsync(write.Snapshot, cancellationToken).ConfigureAwait(false);
        else await store.SaveAsync(SessionId!, write.Snapshot, cancellationToken).ConfigureAwait(false);
        _savedStateVersion = write.StateVersion;
        _savedModelStateVersion = write.ModelStateVersion;
        _restoredSourceHistoryDirty = false;
        RecordSavedConversationState(write.Snapshot);
        cancellation?.MarkHistoryPersisted(historyVersion);
    }

    private async Task InitializeForCatalogAsync(CatalogOperation operation, PreparedSessionRestore? selected, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? completion = null;
        Task initialization;
        lock (_initializationGate)
        {
            ValidateCatalogOperation(operation);
            cancellationToken.ThrowIfCancellationRequested();
            if (_initialization is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _initialization = completion.Task;
            }
            else if (selected is not null) throw new InvalidOperationException("The selected initial snapshot no longer owns initialization admission.");
            initialization = _initialization;
        }
        if (completion is not null) _ = CompleteInitializationAsync(completion, cancellationToken, selected);
        // This operation owns admission through actual completion, including a noncooperative backend.
        await initialization.ConfigureAwait(false);
    }

    private void ValidateCatalogOperation(CatalogOperation operation)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(_catalogOperation, operation)) throw new InvalidOperationException("The snapshot catalog operation no longer owns admission.");
    }

    private sealed record CatalogOperation(SnapshotSessionStore Store, bool WasInitialized);
}
