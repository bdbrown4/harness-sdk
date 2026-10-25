using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.Bidi;

/// <summary>Persists latest and immutable native snapshots through borrowed byte storage.</summary>
public sealed class BidiSnapshotSessionManager
{
    private readonly IStorage _storage;
    private readonly Func<BidiAgent, bool>? _trigger;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Creates a scoped snapshot catalog. Storage ownership remains with its caller.</summary>
    public BidiSnapshotSessionManager(IStorage storage, string sessionId = "default-session", BidiSessionSaveStrategy saveLatestOn = BidiSessionSaveStrategy.Message, Func<BidiAgent, bool>? snapshotTrigger = null)
    {
        if (storage is null) throw new ArgumentNullException(nameof(storage));
        ValidateIdentifier(sessionId, nameof(sessionId));
        if (!Enum.IsDefined(typeof(BidiSessionSaveStrategy), saveLatestOn)) throw new ArgumentOutOfRangeException(nameof(saveLatestOn));
        _storage = storage is IStorageCharacteristics { IsNamespaced: true } ? storage : storage.Namespace("session");
        SessionId = sessionId; SaveLatestOn = saveLatestOn; _trigger = snapshotTrigger;
    }
    /// <summary>The session namespace selected by this manager.</summary>
    public string SessionId { get; }
    /// <summary>The automatic latest-write policy. An immutable trigger also updates latest.</summary>
    public BidiSessionSaveStrategy SaveLatestOn { get; }
    /// <summary>Lists immutable handles oldest first, optionally after an exclusive cursor.</summary>
    public async ValueTask<IReadOnlyList<string>> ListSnapshotIdsAsync(BidiAgent agent, int? limit = null, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        var prefix = Prefix(agent) + "immutable_history/";
        if (startAfter is not null) BidiSnapshotIds.Validate(startAfter);
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is <= 0) return Array.Empty<string>();
        var keys = await _storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = keys.Where(key => key.StartsWith(prefix + "snapshot_", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal))
            .Select(key => key.Substring(prefix.Length + 9, key.Length - prefix.Length - 14))
            .Where(BidiSnapshotIds.IsValid).Where(id => startAfter is null || string.CompareOrdinal(id, startAfter) > 0)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
        return Array.AsReadOnly((limit is { } count ? ids.Take(count) : ids).ToArray());
    }
    /// <summary>Restores latest, or an immutable checkpoint, into a stopped conversation.</summary>
    public async ValueTask<bool> RestoreSnapshotAsync(BidiAgent agent, string? snapshotId = null, CancellationToken cancellationToken = default)
    {
        var key = Key(agent, snapshotId);
        agent.ValidateCatalogRestore(); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return false;
        await agent.LoadSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return true;
    }
    /// <summary>Saves latest when isLatest is true, otherwise returns a fresh immutable handle.</summary>
    public ValueTask<string?> SaveSnapshotAsync(BidiAgent agent, bool isLatest, CancellationToken cancellationToken = default)
    {
        _ = Prefix(agent);
        return agent.WriteCatalogAsync(snapshot => SaveOneAsync(agent, snapshot, isLatest, cancellationToken), cancellationToken);
    }
    /// <summary>Deletes all catalog entries under this session namespace.</summary>
    public async ValueTask DeleteSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = SessionId + "/";
        var keys = await _storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        { cancellationToken.ThrowIfCancellationRequested(); await _storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal ValueTask<AgentSnapshot?> LoadInitialAsync(BidiAgent agent, CancellationToken cancellationToken) => ReadAsync(Key(agent, null), cancellationToken);
    internal async ValueTask SaveLatestAsync(BidiAgent agent, CancellationToken cancellationToken)
    { _ = await SaveOneAsync(agent, agent.CaptureCatalogSnapshot(), true, cancellationToken).ConfigureAwait(false); }

    internal async ValueTask SaveAutomaticAsync(BidiAgent agent, bool boundary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var triggered = false; var triggerFailed = false;
        if (boundary && _trigger is not null)
        {
            try { triggered = _trigger(agent); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                triggerFailed = true;
                try { SdkLogging.Warning(error, "Snapshot trigger failed; immutable checkpoint skipped: {Error}", error.Message); }
                catch (Exception) { /* Diagnostics cannot discard the required latest write. */ }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!triggered && !triggerFailed && (!boundary && SaveLatestOn != BidiSessionSaveStrategy.Message || boundary && SaveLatestOn == BidiSessionSaveStrategy.Trigger)) return;
        var data = JsonSerializer.SerializeToUtf8Bytes(agent.CaptureCatalogSnapshot(), Json);
        if (triggered) await _storage.WriteAsync(Key(agent, BidiSnapshotIds.New()), (byte[])data.Clone(), cancellationToken).ConfigureAwait(false);
        await _storage.WriteAsync(Key(agent, null), (byte[])data.Clone(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask<string?> SaveOneAsync(BidiAgent agent, AgentSnapshot snapshot, bool isLatest, CancellationToken cancellationToken)
    {
        var id = isLatest ? null : BidiSnapshotIds.New();
        await _storage.WriteAsync(Key(agent, id), JsonSerializer.SerializeToUtf8Bytes(snapshot, Json), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return id;
    }

    private async ValueTask<AgentSnapshot?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var data = await _storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (data is null) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<AgentSnapshot>(data, Json) ?? throw new InvalidDataException("Snapshot is null.");
            BidiAgent.ValidateCatalogSnapshot(snapshot);
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { throw new InvalidDataException("Malformed native bidi snapshot.", error); }
    }

    private string Prefix(BidiAgent agent)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        return SessionId + "/scopes/agent/" + agent.AgentId + "/snapshots/";
    }
    private string Key(BidiAgent agent, string? id)
    {
        if (id is not null) BidiSnapshotIds.Validate(id);
        return Prefix(agent) + (id is null ? "snapshot_latest.json" : "immutable_history/snapshot_" + id + ".json");
    }
    internal static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')))
            throw new ArgumentException("Identifiers must contain only ASCII letters, digits, underscores, hyphens or dots and cannot be dot segments.", parameter);
    }
}
