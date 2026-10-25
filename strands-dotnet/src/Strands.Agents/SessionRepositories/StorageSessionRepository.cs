using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.SessionRepositories;

/// <summary>Indexed records over borrowed byte storage. Operations serialize within one repository instance.</summary>
/// <remarks>There is no cross-record transaction or create-if-absent guarantee against other instances or processes. Backends must publish each complete write atomically to retain prior records on failed writes.</remarks>
public class StorageSessionRepository : IDeletableSessionRepository, IMultiAgentSessionRepository
{
    private readonly IStorage _storage;
    private readonly SessionRepositoryOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly UTF8Encoding KeyEncoding = new(false, true);
    private static readonly AsyncLocal<OperationScope?> Operations = new();

    /// <summary>Creates a repository without owning or disposing the storage backend.</summary>
    public StorageSessionRepository(IStorage storage, SessionRepositoryOptions? options = null)
    {
        Guard.NotNull(storage, nameof(storage));
        _storage = storage;
        _options = options is null ? new() : options with { };
        if (_options.MaxRecordBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _options = _options with { Prefix = StorageKey.Normalize(_options.Prefix) };
    }
    /// <inheritdoc />
    public ValueTask<SessionRecord> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(session, nameof(session));
        var prefix = SessionPrefix(session.SessionId);
        var bytes = Encode("session", session.SessionId, null, session);
        return RunAsync(async () =>
        {
            if ((await _storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false)).Count != 0) throw new InvalidOperationException("The indexed session already exists.");
            await _storage.WriteAsync(prefix + "session.json", bytes, cancellationToken).ConfigureAwait(false);
            return session;
        }, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var prefix = SessionPrefix(sessionId);
        return RunAsync(async () =>
        {
            var keys = (await _storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false)).ToArray();
            // Validate the entire detached listing before the first mutation of borrowed storage.
            foreach (var key in keys)
                if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length || StorageKey.Normalize(key) != key)
                    throw new InvalidDataException("The indexed session listing contains a key outside its canonical session namespace.");
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask<SessionRecord?> ReadSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var key = SessionPrefix(sessionId) + "session.json";
        return RunAsync(() => ReadAsync(key, "session", sessionId, null, SessionRecordCodec.ReadSession, cancellationToken), cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask CreateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        var key = AgentPrefix(sessionId, agent.AgentId) + "agent.json";
        var bytes = Encode("agent", sessionId, agent.AgentId, agent);
        return RunAsync(() => _storage.WriteAsync(key, bytes, cancellationToken), cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask<SessionAgentRecord?> ReadAgentAsync(string sessionId, string agentId, CancellationToken cancellationToken = default)
    {
        var key = AgentPrefix(sessionId, agentId) + "agent.json";
        return RunAsync(() => ReadAsync(key, "agent", sessionId, agentId, SessionRecordCodec.ReadAgent, cancellationToken), cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask UpdateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        var key = AgentPrefix(sessionId, agent.AgentId) + "agent.json";
        return RunAsync(async () =>
        {
            var previous = await ReadAsync(key, "agent", sessionId, agent.AgentId, SessionRecordCodec.ReadAgent, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The indexed agent does not exist.");
            var replacement = new SessionAgentRecord(agent.AgentId, agent.State, agent.ConversationManagerState, agent.InternalState, previous.CreatedAt, agent.UpdatedAt);
            await _storage.WriteAsync(key, Encode("agent", sessionId, agent.AgentId, replacement), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask CreateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(message, nameof(message));
        var key = MessageKey(sessionId, agentId, message.MessageId);
        var bytes = Encode("message", sessionId, agentId, message);
        return RunAsync(() => _storage.WriteAsync(key, bytes, cancellationToken), cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask<SessionMessageRecord?> ReadMessageAsync(string sessionId, string agentId, long messageId, CancellationToken cancellationToken = default)
    {
        var key = MessageKey(sessionId, agentId, messageId);
        return RunAsync(() => ReadMessageCoreAsync(key, sessionId, agentId, messageId, cancellationToken), cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask UpdateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(message, nameof(message));
        var key = MessageKey(sessionId, agentId, message.MessageId);
        return RunAsync(async () =>
        {
            var previous = await ReadMessageCoreAsync(key, sessionId, agentId, message.MessageId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The indexed message does not exist.");
            var replacement = new SessionMessageRecord(message.Message, message.MessageId, message.RedactMessage, previous.CreatedAt, message.UpdatedAt);
            await _storage.WriteAsync(key, Encode("message", sessionId, agentId, replacement), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string sessionId, string agentId, int? limit = null, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (limit is < 0 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit), "Row offset and limit must be nonnegative.");
        var prefix = AgentPrefix(sessionId, agentId) + "messages/";
        return RunAsync<IReadOnlyList<SessionMessageRecord>>(async () =>
        {
            var keys = await _storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (keys.Count == 0 && await ReadAsync(AgentPrefix(sessionId, agentId) + "agent.json", "agent", sessionId, agentId, SessionRecordCodec.ReadAgent, cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidOperationException("The indexed agent has no message collection.");
            var indexed = keys.Select(key => ParseMessageKey(key, prefix)).OrderBy(item => item.Id).ToArray();
            var selected = limit is { } count ? indexed.Skip(offset).Take(count) : indexed.Skip(offset);
            var records = new List<SessionMessageRecord>();
            foreach (var item in selected)
                records.Add(await ReadMessageCoreAsync(item.Key, sessionId, agentId, item.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("An indexed message disappeared during listing; coordinate external writers."));
            return Array.AsReadOnly(records.ToArray());
        }, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask CreateMultiAgentAsync(string sessionId, string multiAgentId, JsonElement state, CancellationToken cancellationToken = default)
    {
        var key = MultiAgentKey(sessionId, multiAgentId);
        var copy = SessionAgentRecord.CopyObject(state);
        var bytes = Encode("multiAgent", sessionId, multiAgentId, copy);
        return RunAsync(() => _storage.WriteAsync(key, bytes, cancellationToken), cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask<JsonElement?> ReadMultiAgentAsync(string sessionId, string multiAgentId, CancellationToken cancellationToken = default)
    {
        var key = MultiAgentKey(sessionId, multiAgentId);
        return RunAsync(async () =>
        {
            var bytes = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            return bytes is null ? (JsonElement?)null : Decode(bytes, "multiAgent", sessionId, multiAgentId);
        }, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask UpdateMultiAgentAsync(string sessionId, string multiAgentId, JsonElement state, CancellationToken cancellationToken = default)
    {
        var key = MultiAgentKey(sessionId, multiAgentId);
        var copy = SessionAgentRecord.CopyObject(state);
        var bytes = Encode("multiAgent", sessionId, multiAgentId, copy);
        return RunAsync(async () =>
        {
            var previous = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The indexed multi-agent state does not exist.");
            _ = Decode(previous, "multiAgent", sessionId, multiAgentId);
            await _storage.WriteAsync(key, bytes, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private byte[] Encode(string kind, string sessionId, string? ownerId, object record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind, sessionId, ownerId, data = record }, SessionRecordCodec.Options);
        CheckSize(bytes);
        SessionRecordCodec.ValidateJson(bytes);
        return bytes;
    }
    private JsonElement Decode(byte[] bytes, string kind, string sessionId, string? ownerId)
    {
        CheckSize(bytes);
        return SessionRecordCodec.ReadEnvelope(bytes, kind, sessionId, ownerId);
    }
    private void CheckSize(byte[] bytes)
    {
        if (_options.MaxRecordBytes is { } maximum && bytes.Length > maximum) throw new InvalidDataException("The indexed record exceeds MaxRecordBytes.");
    }
    private async ValueTask<T?> ReadAsync<T>(string key, string kind, string sessionId, string? ownerId, Func<JsonElement, T> read, CancellationToken token) where T : class
    {
        var bytes = await _storage.ReadAsync(key, token).ConfigureAwait(false);
        if (bytes is null) return null;
        var record = read(Decode(bytes, kind, sessionId, ownerId));
        if (record is SessionRecord session && session.SessionId != sessionId || record is SessionAgentRecord agent && agent.AgentId != ownerId)
            throw new InvalidDataException("The indexed record identity does not match its requested key.");
        return record;
    }
    private async ValueTask<SessionMessageRecord?> ReadMessageCoreAsync(string key, string sessionId, string agentId, long messageId, CancellationToken token)
    {
        var record = await ReadAsync(key, "message", sessionId, agentId, SessionRecordCodec.ReadMessage, token).ConfigureAwait(false);
        if (record is not null && record.MessageId != messageId) throw new InvalidDataException("The indexed message identity does not match its requested key.");
        return record;
    }
    private async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> action, CancellationToken token)
    {
        for (var active = Operations.Value; active is not null; active = active.Parent)
            if (active.IsOpen && ReferenceEquals(active.Repository, this)) throw new InvalidOperationException("A storage callback cannot await its own indexed repository operation.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var previous = Operations.Value;
        var scope = new OperationScope(this, previous);
        Operations.Value = scope;
        try
        {
            token.ThrowIfCancellationRequested();
            var value = await action().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return value;
        }
        catch (Exception error) when (token.IsCancellationRequested && (error is not OperationCanceledException cancellation || cancellation.CancellationToken != token))
        { throw new OperationCanceledException("The indexed repository operation was cancelled.", error, token); }
        finally { scope.Close(); Operations.Value = previous; _gate.Release(); }
    }
    private async ValueTask RunAsync(Func<ValueTask> action, CancellationToken token)
        => await RunAsync(async () => { await action().ConfigureAwait(false); return true; }, token).ConfigureAwait(false);
    private string SessionPrefix(string sessionId) => _options.Prefix + "/" + Hash(sessionId) + "/";
    private string AgentPrefix(string sessionId, string agentId) => SessionPrefix(sessionId) + "agents/" + Hash(agentId) + "/";
    private string MessageKey(string sessionId, string agentId, long messageId) => AgentPrefix(sessionId, agentId) + "messages/" + messageId.ToString(CultureInfo.InvariantCulture) + ".json";
    private string MultiAgentKey(string sessionId, string multiAgentId) => SessionPrefix(sessionId) + "multiAgents/" + Hash(multiAgentId) + ".json";
    private static string Hash(string id)
    {
        Guard.NotNullOrWhiteSpace(id, nameof(id));
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(KeyEncoding.GetBytes(id))).Replace("-", "").ToLowerInvariant();
    }
    private static (long Id, string Key) ParseMessageKey(string key, string prefix)
    {
        var id = key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal) ? key.Substring(prefix.Length, key.Length - prefix.Length - 5) : "";
        if (!long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || id != value.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("The indexed message collection contains an invalid numeric key.");
        return (value, key);
    }
    private sealed class OperationScope(StorageSessionRepository repository, OperationScope? parent)
    {
        private int _open = 1;
        internal StorageSessionRepository Repository { get; } = repository;
        internal OperationScope? Parent { get; } = parent;
        internal bool IsOpen => Volatile.Read(ref _open) != 0;
        internal void Close() => Interlocked.Exchange(ref _open, 0);
    }
}
