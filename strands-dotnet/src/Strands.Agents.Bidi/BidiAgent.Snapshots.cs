using System.Text.Json;

namespace Strands.Agents.Bidi;

/// <summary>Automatic latest-session persistence boundaries.</summary>
public enum BidiSessionSaveStrategy
{
    /// <summary>Every message addition/replacement, plus stop and before restart.</summary>
    Message,
    /// <summary>Stop and before restart; response completion is not a save boundary.</summary>
    Stop,
    /// <summary>Automatic latest writes occur only when an immutable trigger fires or fails.</summary>
    Trigger
}

public sealed partial class BidiAgent
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    private const SnapshotFields BidiFields = SnapshotFields.Messages | SnapshotFields.State | SnapshotFields.SystemPrompt;
    /// <summary>This conversation's application JSON state, independent of the borrowed tool host's state.</summary>
    public StateStore State { get; private set; } = new();
    /// <summary>The opaque persistence key, generated even when no store is configured.</summary>
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    private bool _hasPromptOverride = false;
    private string? _systemPrompt = null;
    /// <summary>The optional system prompt. A restored explicit null clears the prompt instead of falling back to the tool host.</summary>
    public string? SystemPrompt { get { lock (_gate) return _hasPromptOverride ? _systemPrompt : _options.SystemPrompt ?? _options.ToolAgent.SystemPrompt; } }
    /// <summary>Restores native session state and dispatches initialization once; failure is cached.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => InitializePublicAsync(cancellationToken);
    /// <summary>Captures selected current history/state. Live connections, active tool bodies and provider resumption handles are excluded.</summary>
    public async ValueTask<AgentSnapshot> TakeSnapshotAsync(SnapshotOptions options, CancellationToken cancellationToken = default)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Preset is { } preset && !Enum.IsDefined(typeof(SnapshotPreset), preset) || ((options.Include | options.Exclude) & ~BidiFields) != 0) throw new ArgumentException("Bidi snapshots support messages, state and system prompt.", nameof(options));
        var fields = ((options.Preset is null ? SnapshotFields.None : SnapshotFields.Messages | SnapshotFields.State) | options.Include) & ~options.Exclude;
        if (fields == SnapshotFields.None) throw new ArgumentException("No snapshot fields selected.", nameof(options));
        var appData = AgentSnapshot.CloneValues(options.AppData ?? throw new ArgumentException("AppData cannot be null.", nameof(options)), nameof(options));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if ((fields & SnapshotFields.Messages) != 0) data.Add("messages", JsonSerializer.SerializeToElement(_messages.Select(Copy).ToArray(), SnapshotJson));
            if ((fields & SnapshotFields.State) != 0) data.Add("state", JsonSerializer.SerializeToElement(State.GetAll(), SnapshotJson));
            if ((fields & SnapshotFields.SystemPrompt) != 0) data.Add("systemPrompt", JsonSerializer.SerializeToElement(SystemPromptContent, SnapshotJson));
            return new(data, appData);
        }
    }
    /// <summary>Validates a complete detached snapshot before replacing supplied fields while stopped; unknown fields and AppData are ignored.</summary>
    public async ValueTask LoadSnapshotAsync(AgentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = PrepareSnapshot(snapshot);
        RejectCallbackLifecycle(); lock (_gate) ValidateSnapshotLoad();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) { ValidateSnapshotLoad(); cancellationToken.ThrowIfCancellationRequested(); ApplySnapshot(prepared); }
        }
        finally { _lifecycle.Release(); }
    }
    /// <summary>Saves current history/state to the borrowed session store; no-op when no store is configured.</summary>
    public async ValueTask SaveSessionAsync(CancellationToken cancellationToken = default)
    {
        for (var scope = SessionScope.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Owner, this)) throw new InvalidOperationException("A session callback cannot await its own persistence.");
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_explicitSaves++ == 0) _explicitSaveDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await SaveSessionCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { lock (_gate) if (--_explicitSaves == 0) _explicitSaveDrain!.TrySetResult(true); }
    }
    private void ValidateSnapshotLoad()
    {
        ThrowIfDisposed();
        if (_conversation is not null || _initialization is { IsCompleted: false }) throw new InvalidOperationException("Stop the bidirectional conversation and finish initialization before loading a snapshot.");
    }
    private static PreparedSnapshot PrepareSnapshot(AgentSnapshot snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (snapshot.Scope != "agent" || snapshot.SchemaVersion != "1.0") throw new InvalidDataException("Bidi snapshots require scope agent and schema1.0.");
        try
        {
            Message[]? messages = null;
            if (snapshot.Data.TryGetValue("messages", out var history))
            {
                if (history.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Snapshot messages must be an array.");
                messages = history.Deserialize<Message[]>(SnapshotJson)?.Select(Copy).ToArray() ?? throw new InvalidDataException("Snapshot messages are missing.");
                if (messages.Any(message => message.Role is not (Role.User or Role.Assistant))) throw new InvalidDataException("Invalid message role.");
                ConversationHistory.Validate(messages);
            }
            IReadOnlyDictionary<string, JsonElement>? state = null;
            if (snapshot.Data.TryGetValue("state", out var values))
            {
                if (values.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Snapshot state must be an object.");
                var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var value in values.EnumerateObject()) { if (copy.ContainsKey(value.Name)) throw new InvalidDataException("Duplicate application state key."); copy.Add(value.Name, value.Value.Clone()); }
                state = copy;
            }
            var hasPrompt = snapshot.Data.TryGetValue("systemPrompt", out var prompt);
            IReadOnlyList<ContentBlock>? content = null;
            if (hasPrompt)
                content = prompt.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => FromText(prompt.GetString()),
                    JsonValueKind.Array => SessionSnapshot.CopySystemPromptContent(prompt.Deserialize<ContentBlock[]>(SnapshotJson) ?? throw new InvalidDataException("System content is missing.")),
                    _ => throw new InvalidDataException("Snapshot systemPrompt must be text/cache-point content, a legacy string, or null.")
                };
            return new(messages, state, hasPrompt, content);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { throw new InvalidDataException("Malformed bidi snapshot.", error); }
    }
    private void ApplySnapshot(PreparedSnapshot snapshot)
    {
        if (snapshot.Messages is { } messages) { _messages.Clear(); _messages.AddRange(messages); }
        if (snapshot.State is { } state) State.Replace(state);
        if (snapshot.HasPrompt) ApplyPrompt(snapshot.Prompt);
    }
    private sealed record PreparedSnapshot(Message[]? Messages, IReadOnlyDictionary<string, JsonElement>? State, bool HasPrompt, IReadOnlyList<ContentBlock>? Prompt);
}
