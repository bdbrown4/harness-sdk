using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Bidi;

public sealed partial class BidiAgent
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private Task? _initialization;
    private Task _historyTail = Task.CompletedTask;
    private bool _sessionRestored;
    private int _explicitSaves;
    private TaskCompletionSource<bool>? _explicitSaveDrain;
    private static readonly AsyncLocal<CallbackScope?> SessionScope = new();

    private async Task InitializePublicAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Task? existing;
        lock (_gate) { ThrowIfDisposed(); existing = _initialization; }
        if (existing is not null)
        {
            if (!existing.IsCompleted) RejectCallbackWait("An initialization callback cannot await its own initialization.");
            await WaitAsync(existing, cancellationToken).ConfigureAwait(false); return;
        }
        RejectCallbackLifecycle(); await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = Scope.Value; Scope.Value = new(this, previous);
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await WaitAsync(EnsureInitializedAsync(operation.Token), cancellationToken).ConfigureAwait(false);
        }
        finally { Scope.Value = previous; _lifecycle.Release(); }
    }
    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? completion = null; Task result;
        lock (_gate)
        {
            if (_initialization is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _initialization = completion.Task; }
            result = _initialization;
        }
        if (completion is not null) _ = CompleteInitializationAsync(completion, cancellationToken);
        return result;
    }
    private async Task CompleteInitializationAsync(TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        try
        {
            if (_options.SessionManager is { } manager)
            {
                var prior = SessionScope.Value; SessionScope.Value = new(this, prior);
                AgentSnapshot? snapshot;
                try { snapshot = await manager.LoadInitialAsync(this, cancellationToken).ConfigureAwait(false); }
                finally { SessionScope.Value = prior; }
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot is not null) { var prepared = PrepareSnapshot(snapshot); lock (_gate) ApplySnapshot(prepared); }
            }
            if (_options.SessionStore is { } store)
            {
                var prior = SessionScope.Value; SessionScope.Value = new(this, prior);
                SessionSnapshot? snapshot;
                try { snapshot = await store.LoadAsync(SessionId, cancellationToken).ConfigureAwait(false); }
                finally { SessionScope.Value = prior; }
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot is not null)
                {
                    if (snapshot.SchemaVersion != 1 || snapshot.Messages is null) throw new InvalidDataException("Unsupported bidi session snapshot.");
                    var content = snapshot.CopyValidatedSystemPromptContent();
                    var data = new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(snapshot.Messages, SnapshotJson) };
                    if (snapshot.State is { } state) data.Add("state", JsonSerializer.SerializeToElement(state, SnapshotJson));
                    if (snapshot.HasSystemPrompt) data.Add("systemPrompt", content is null ? JsonSerializer.SerializeToElement(snapshot.SystemPrompt, SnapshotJson) : JsonSerializer.SerializeToElement(content, SnapshotJson));
                    var prepared = PrepareSnapshot(new(data));
                    lock (_gate) ApplySnapshot(prepared);
                }
            }
            _sessionRestored = true;
            await Hooks.InvokeAsync(new BidiInitializedEvent(this), cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private async ValueTask SaveSessionCoreAsync(CancellationToken cancellationToken, bool boundary = false)
    {
        if ((_options.SessionStore is null && _options.SessionManager is null) || !_sessionRestored) return;
        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previous = SessionScope.Value; var previousScope = Scope.Value;
        SessionScope.Value = new(this, previous); Scope.Value = new(this, previousScope);
        try
        {
            if (_options.SessionManager is { } manager)
            {
                if (boundary) await manager.SaveAutomaticAsync(this, true, cancellationToken).ConfigureAwait(false);
                else await manager.SaveLatestAsync(this, cancellationToken).ConfigureAwait(false);
                return;
            }
            SessionSnapshot snapshot;
            lock (_gate) snapshot = new(1, Array.AsReadOnly(_messages.Select(Copy).ToArray())) { State = State.GetAll(), HasSystemPrompt = true, SystemPrompt = SystemPrompt, SystemPromptContent = SystemPromptContent };
            await _options.SessionStore!.SaveAsync(SessionId, snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally { SessionScope.Value = previous; Scope.Value = previousScope; _sessionGate.Release(); }
    }
    private Task QueueHistoryLocked(IReadOnlyList<IBidiHookEvent> events, CancellationToken cancellationToken)
    {
        var previous = _historyTail;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _historyTail = completion.Task;
        _ = DispatchHistoryAsync(previous, events, completion, cancellationToken);
        return completion.Task;
    }
    private async Task DispatchHistoryAsync(Task previous, IReadOnlyList<IBidiHookEvent> events, TaskCompletionSource<bool> completion, CancellationToken cancellationToken)
    {
        // Always leave the synchronous history lock before invoking application callbacks.
        await Task.Yield();
        try { await previous.ConfigureAwait(false); } catch (Exception) { /* Each accepted batch owns and reports its own failure. */ }
        var priorScope = Scope.Value; var priorSend = SendScope.Value;
        Scope.Value = new(this, priorScope); SendScope.Value = new(this, priorSend);
        Exception? primary = null;
        try
        {
            foreach (var value in events)
                try { await Hooks.InvokeAsync(value, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { Add(ref primary, error); }
            if ((_options.SessionManager?.SaveLatestOn ?? _options.SessionSaveStrategy) == BidiSessionSaveStrategy.Message)
                try { await SaveSessionCoreAsync(primary is not null || cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false); }
                catch (Exception error) { Add(ref primary, error); }
        }
        finally { Scope.Value = priorScope; SendScope.Value = priorSend; }
        if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
    }
    private Task FlushHistoryAsync() { lock (_gate) return _historyTail; }

    private async Task RepairRecoveredHistoryAsync(CancellationToken cancellationToken)
    {
        Task? notification = null;
        lock (_gate)
        {
            ConversationHistory.Validate(_messages);
            var events = new List<IBidiHookEvent>();
            for (var index = 0; index < _messages.Count; index++)
            {
                var original = _messages[index];
                if (!TryBidiMetadata(original, out var kind, out var status)) continue;
                Message? replacement = null;
                if (kind == "tool_dispatch")
                    replacement = new(original.Role, original.Content.Select(block => block is ToolResultBlock result ? UnknownResult(result.ToolUseId) : block).ToArray(), original.TrackingId, ContentMetadata("tool_result", "incomplete"));
                else if (status == "pending" && kind is "text" or "reasoning" or "transcript")
                    replacement = new(original.Role, [kind == "reasoning" ? new ReasoningBlock(string.Empty) : new TextBlock(kind == "transcript" ? "[Transcript unavailable.]" : string.Empty)], original.TrackingId, ContentMetadata(kind, "incomplete"));
                if (replacement is null) continue;
                _messages[index] = replacement; events.Add(new BidiMessageUpdatedEvent(this, replacement));
            }
            if (_messages.LastOrDefault() is { Role: Role.Assistant } last && last.Content.OfType<ToolUseBlock>().Any())
            {
                var results = new Message(Role.User, last.Content.OfType<ToolUseBlock>().Select(call => (ContentBlock)UnknownResult(call.ToolUseId)).ToArray(), metadata: ContentMetadata("tool_result", "incomplete"));
                _messages.Add(results); events.Add(new BidiMessageAddedEvent(this, results));
            }
            if (events.Count != 0) notification = QueueHistoryLocked(events, cancellationToken);
        }
        if (notification is not null) await notification.ConfigureAwait(false);

        static ToolResultBlock UnknownResult(string id) => new(id, "Tool execution outcome is unknown after restoration; its body was not resumed.", ToolResultStatus.Error);
    }
    private static bool TryBidiMetadata(Message message, out string? kind, out string? status)
    {
        kind = status = null;
        if (message.Metadata is not { ValueKind: JsonValueKind.Object } metadata || !metadata.TryGetProperty("custom", out var custom) || custom.ValueKind != JsonValueKind.Object ||
            !custom.TryGetProperty("bidi", out var bidi) || bidi.ValueKind != JsonValueKind.Object) return false;
        if (bidi.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String) kind = kindValue.GetString();
        if (bidi.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String) status = statusValue.GetString();
        return kind is not null;
    }
}
