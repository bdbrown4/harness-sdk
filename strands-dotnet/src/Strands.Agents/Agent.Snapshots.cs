using System.Text.Json;

namespace Strands.Agents;

public sealed partial class Agent
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    internal static void ValidateSnapshotData(AgentSnapshot snapshot)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        _ = PrepareSnapshot(snapshot);
    }

    /// <summary>Initializes the agent and captures selected inactive in-memory state. This does not save a session or transfer external resource ownership.</summary>
    public async ValueTask<AgentSnapshot> TakeSnapshotAsync(SnapshotOptions options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(options, nameof(options));
        Guard.NotNull(options.AppData, nameof(options.AppData));
        cancellationToken.ThrowIfCancellationRequested();
        var fields = ResolveSnapshotFields(options);
        var appData = AgentSnapshot.CloneValues(options.AppData, nameof(options.AppData));
        lock (_initializationGate) ValidateSnapshotAccess();
        await TaskCancellation.WaitAsync(InitializeAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        lock (_initializationGate)
        {
            ValidateSnapshotAccess();
            cancellationToken.ThrowIfCancellationRequested();
            var inputReceipt = UsesNativeStatefulHistory ? _modelInputReceipt : null;
            var pair = fields & (SnapshotFields.Messages | SnapshotFields.ModelState);
            if (inputReceipt is not null && pair != SnapshotFields.None && pair != (SnapshotFields.Messages | SnapshotFields.ModelState))
                throw new InvalidOperationException("An input receipt requires both history and provider state in the same checkpoint.");
            var includeReceipt = inputReceipt is not null && pair != SnapshotFields.None;
            var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if ((fields & SnapshotFields.Messages) != 0)
            {
                data["messages"] = JsonSerializer.SerializeToElement(Messages, SnapshotJson);
                if (_options.ModelHistoryBehavior != ModelHistoryBehavior.Native)
                    data["historyProfile"] = JsonSerializer.SerializeToElement(_options.ModelHistoryBehavior);
                if (UsesNativeStatefulHistory) data["modelHistoryCount"] = JsonSerializer.SerializeToElement(_modelHistoryCount);
            }
            if ((fields & SnapshotFields.State) != 0) data["state"] = JsonSerializer.SerializeToElement(State.GetAll(), SnapshotJson);
            if ((fields & SnapshotFields.SystemPrompt) != 0) data["systemPrompt"] = SystemPromptContent is { } content
                ? JsonSerializer.SerializeToElement(content, SnapshotJson) : JsonSerializer.SerializeToElement(SystemPrompt, SnapshotJson);
            if ((fields & SnapshotFields.ModelState) != 0) data["modelState"] = JsonSerializer.SerializeToElement(ModelState.GetAll(), SnapshotJson);
            if ((fields & SnapshotFields.InterruptState) != 0) data["interruptState"] = JsonSerializer.SerializeToElement(InterruptState, SnapshotJson);
            if ((fields & SnapshotFields.ConversationManagerState) != 0 && CaptureConversationState() is { } conversationState) data["conversationManagerState"] = conversationState;
            if (includeReceipt) data["modelInputReceipt"] = JsonSerializer.SerializeToElement(inputReceipt, SnapshotJson);
            var captured = new AgentSnapshot(data, appData, schemaVersion: includeReceipt ? "2.0" : "1.0");
            if (includeReceipt) _ = PrepareSnapshot(captured);
            return captured;
        }
    }

    /// <summary>Validates a checkpoint, initializes the agent and replaces only supplied fields while inactive. No session write is performed.</summary>
    public async ValueTask LoadSnapshotAsync(AgentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(snapshot, nameof(snapshot));
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = PrepareSnapshot(snapshot);
        ValidateStoredHistoryProfile(prepared.HistoryProfile);
        if (prepared.Messages is not null || prepared.InterruptState is { AssistantMessageIsIndependent: true } or { SourceHistoryOwned: true })
            prepared.InterruptState?.ValidateHistory(prepared.Messages ?? Messages, allowIndependent: _options.ModelHistoryBehavior != ModelHistoryBehavior.Native, independentOwnerProfile: _options.ModelHistoryBehavior);
        ValidateRetainedHistoryMetadata(prepared.ModelHistoryCount, prepared.ModelInputReceipt);
        if (prepared.ModelInputReceipt is not null && !IsStateful)
            throw new InvalidDataException("An input receipt requires a stateful model.");
        if (prepared.Messages is not null && _options.SessionStore is SessionRepositories.RepositoryAgentJournal)
            throw new InvalidOperationException("Public history replacement is not supported while an indexed session journal is bound.");
        var conversationState = PrepareConversationState(prepared.ConversationManagerState);
        _backgroundTasks?.ValidateStoredState(prepared.State);
        _backgroundTasks?.AssertCanLoadSnapshot();
        lock (_initializationGate) ValidateSnapshotAccess();
        await TaskCancellation.WaitAsync(InitializeAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        Action? providerUpdateReport;
        lock (_initializationGate)
        {
            ValidateSnapshotAccess();
            _backgroundTasks?.AssertCanLoadSnapshot();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSnapshotProviderState(prepared);
            var effectiveInterrupt = prepared.HasInterruptState ? prepared.InterruptState : InterruptState;
            var effectiveMessages = prepared.Messages ?? Messages;
            ConversationHistory.Validate(effectiveMessages, prepared.Messages is null ? _options.ModelHistoryBehavior : prepared.HistoryProfile);
            effectiveInterrupt?.ValidateHistory(effectiveMessages, allowIndependent: _options.ModelHistoryBehavior != ModelHistoryBehavior.Native, independentOwnerProfile: _options.ModelHistoryBehavior);
            conversationState?.Apply();
            if (prepared.Messages is { } messages)
            {
                if (UsesNativeStatefulHistory && prepared.ModelHistoryCount is null && prepared.ModelState is null && ModelState.GetAll().Count > 0)
                    throw new InvalidDataException("Replacing stateful history requires its acknowledgement cursor or explicit provider state.");
                lock (_historyGate)
                {
                    _messages.Clear();
                    _messages.AddRange(messages);
                    _modelHistoryCount = UsesNativeStatefulHistory ? prepared.ModelHistoryCount ?? 0 : 0;
                    _modelInputReceipt = UsesNativeStatefulHistory ? prepared.ModelInputReceipt : null;
                }
            }
            if (prepared.State is { } state) { State.Replace(state); _backgroundTasks?.LoadState(); }
            if (prepared.ModelState is { } modelState) ModelState.Replace(modelState);
            if (prepared.HasSystemPrompt) { SystemPrompt = prepared.SystemPrompt; SystemPromptContent = prepared.SystemPromptContent; }
            if (prepared.HasInterruptState) lock (_historyGate) _interruptState = prepared.InterruptState?.Copy();
            providerUpdateReport = PublishPendingProviderUpdatesUnderGate();
        }
        providerUpdateReport?.Invoke();
    }

    private void ValidateSnapshotAccess()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _invoking) != 0 || _directToolCalls != 0 || _classicReductions != 0 || _catalogOperation is not null || _initialization is { IsCompleted: false })
            throw new InvalidOperationException("Checkpoints require an inactive agent whose initialization is not already running.");
    }

    private void ValidateSnapshotProviderState(PreparedSnapshot prepared)
    {
        ValidateRetainedHistoryMetadata(prepared.ModelHistoryCount, prepared.ModelInputReceipt);
        if (!UsesNativeStatefulHistory)
        {
            if (prepared.ModelInputReceipt is not null) throw new InvalidDataException("An input receipt requires a stateful model.");
            return;
        }
        var currentState = ModelState.GetAll();
        var effectiveState = prepared.ModelState ?? currentState;
        var effectiveCursor = prepared.Messages is null ? EffectiveModelHistoryCount : prepared.ModelInputReceipt?.ConsumedMessageCount ?? prepared.ModelHistoryCount ?? 0;
        if (effectiveCursor > 0 && effectiveState.Count == 0)
            throw new InvalidDataException("Acknowledged server history requires its provider conversation state.");
        if (prepared.Messages is null && EffectiveModelHistoryCount > 0 && prepared.ModelState is { } replacement &&
            (replacement.Count != currentState.Count || replacement.Any(pair => !currentState.TryGetValue(pair.Key, out var value) || !JsonElement.DeepEquals(pair.Value, value))))
            throw new InvalidDataException("Changing acknowledged provider state requires its corresponding conversation history and cursor.");
    }

    private static SnapshotFields ResolveSnapshotFields(SnapshotOptions options)
    {
        if (options.Preset is { } preset && !Enum.IsDefined(typeof(SnapshotPreset), preset))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown checkpoint preset.");
        if ((options.Include & ~SnapshotFields.All) != 0 || (options.Exclude & ~SnapshotFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown checkpoint field.");
        var fields = ((options.Preset is null ? SnapshotFields.None : SnapshotFields.All) | options.Include) & ~options.Exclude;
        if (fields == SnapshotFields.None) throw new ArgumentException("Select at least one checkpoint field using a preset or include.", nameof(options));
        return fields;
    }

    private static PreparedSnapshot PrepareSnapshot(AgentSnapshot snapshot)
    {
        if (snapshot.Scope != "agent") throw new InvalidDataException("Expected checkpoint scope 'agent'.");
        if (snapshot.SchemaVersion is not ("1.0" or "2.0")) throw new InvalidDataException("Unsupported native checkpoint schema version.");
        if (snapshot.SchemaVersion == "1.0" && snapshot.Data.ContainsKey("modelInputReceipt"))
            throw new InvalidDataException("A version-one checkpoint cannot contain an input receipt.");
        try
        {
            Message[]? messages = null;
            Dictionary<string, JsonElement>? state = null, modelState = null;
            string? systemPrompt = null;
            IReadOnlyList<ContentBlock>? systemPromptContent = null;
            int? modelHistoryCount = null;
            ModelInputReceipt? modelInputReceipt = null;
            var historyProfile = snapshot.Data.TryGetValue("historyProfile", out var historyData)
                ? (ModelHistoryBehavior)historyData.GetInt32() : ModelHistoryBehavior.Native;
            if (!Enum.IsDefined(typeof(ModelHistoryBehavior), historyProfile))
                throw new InvalidDataException("The stored history profile is unknown.");
            if (snapshot.Data.ContainsKey("historyProfile") && !snapshot.Data.ContainsKey("messages"))
                throw new InvalidDataException("A stored history profile requires checkpoint messages.");
            JsonElement? conversationManagerState = snapshot.Data.TryGetValue("conversationManagerState", out var managerData) && managerData.ValueKind != JsonValueKind.Null
                ? Conversation.ConversationManager.ValidateEnvelope(managerData) : null;
            var hasInterruptState = snapshot.Data.TryGetValue("interruptState", out var interruptData);
            var interruptState = hasInterruptState && interruptData.ValueKind != JsonValueKind.Null
                ? interruptData.Deserialize<InterruptState>(SnapshotJson) ?? throw new InvalidDataException("Checkpoint interrupts must be an object or null.") : null;
            if (snapshot.Data.TryGetValue("messages", out var messageData))
            {
                messages = messageData.Deserialize<Message[]>(SnapshotJson) ?? throw new InvalidDataException("Checkpoint messages cannot be null.");
                ConversationHistory.Validate(messages, historyProfile);
                interruptState?.ValidateHistory(messages, allowIndependent: true, independentOwnerProfile: historyProfile == ModelHistoryBehavior.Native ? (ModelHistoryBehavior?)null : historyProfile);
            }
            if (snapshot.Data.TryGetValue("state", out var stateData))
                state = stateData.Deserialize<Dictionary<string, JsonElement>>(SnapshotJson) ?? throw new InvalidDataException("Checkpoint state must be an object.");
            if (snapshot.Data.TryGetValue("modelState", out var modelStateData))
                modelState = modelStateData.Deserialize<Dictionary<string, JsonElement>>(SnapshotJson) ?? throw new InvalidDataException("Checkpoint provider state must be an object.");
            var hasSystemPrompt = snapshot.Data.TryGetValue("systemPrompt", out var promptData);
            if (hasSystemPrompt)
            {
                if (promptData.ValueKind == JsonValueKind.Array)
                {
                    systemPromptContent = SessionSnapshot.CopySystemPromptContent(promptData.Deserialize<ContentBlock[]>(SnapshotJson)
                        ?? throw new InvalidDataException("Checkpoint system content must be an array."), allowGuardContent: true);
                    systemPrompt = SessionSnapshot.GetSystemPromptText(systemPromptContent);
                }
                else
                {
                    if (promptData.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new InvalidDataException("Checkpoint system prompt must be text, an array or null.");
                    systemPrompt = promptData.ValueKind == JsonValueKind.Null ? null : promptData.GetString();
                }
            }
            if (snapshot.Data.TryGetValue("modelHistoryCount", out var cursorData))
            {
                if (messages is null) throw new InvalidDataException("An acknowledgement cursor requires checkpoint messages.");
                modelHistoryCount = cursorData.GetInt32();
                new SessionSnapshot(1, messages) { ModelHistoryCount = modelHistoryCount }.ValidateModelHistory();
            }
            if (snapshot.SchemaVersion == "2.0")
            {
                if (messages is null || modelState is null || modelHistoryCount is null || !snapshot.Data.TryGetValue("modelInputReceipt", out var receiptData))
                    throw new InvalidDataException("A version-two checkpoint requires history, its cursor, provider state and an input receipt.");
                modelInputReceipt = receiptData.Deserialize<ModelInputReceipt>(SnapshotJson)
                    ?? throw new InvalidDataException("A version-two checkpoint requires a nonnull input receipt.");
                new SessionSnapshot(2, messages) { ModelHistoryCount = modelHistoryCount, ModelInputReceipt = modelInputReceipt, ModelState = modelState }
                    .ValidateModelInputReceipt();
            }
            if (messages is not null)
                new SessionSnapshot(snapshot.SchemaVersion == "2.0" ? 2 : 1, messages)
                { HistoryProfile = historyProfile, ModelHistoryCount = modelHistoryCount, ModelInputReceipt = modelInputReceipt }.ValidateHistoryProfile();
            return new(messages, state, modelState, hasSystemPrompt, systemPrompt, systemPromptContent, modelHistoryCount, modelInputReceipt, hasInterruptState, interruptState, conversationManagerState, historyProfile);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Checkpoint fields are malformed.", error);
        }
    }

    private sealed record PreparedSnapshot(Message[]? Messages, Dictionary<string, JsonElement>? State,
        Dictionary<string, JsonElement>? ModelState, bool HasSystemPrompt, string? SystemPrompt, IReadOnlyList<ContentBlock>? SystemPromptContent, int? ModelHistoryCount, ModelInputReceipt? ModelInputReceipt,
        bool HasInterruptState, InterruptState? InterruptState, JsonElement? ConversationManagerState, ModelHistoryBehavior HistoryProfile);
}
