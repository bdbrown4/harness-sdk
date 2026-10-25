using Strands.Agents.Storage;

namespace Strands.Agents.SessionCatalogs;

internal sealed class SnapshotSessionStore(SnapshotSessionManager manager, IStorage storage, Agent owner, string bindingId) : ISessionStore
{
    private int _released;
    internal SnapshotSessionManager Manager { get; } = manager;
    internal Agent Owner { get; } = owner;
    internal string BindingId { get; } = bindingId;
    internal bool IsReleased => Volatile.Read(ref _released) != 0;
    private string Prefix => Manager.SessionId + "/scopes/agent/" + BindingId + "/snapshots/";
    private string Key(string? id) => Prefix + (id is null ? "snapshot_latest.json" : "immutable_history/snapshot_" + id + ".json");

    public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateSession(sessionId);
        return ReadSnapshotAsync(null, cancellationToken);
    }

    public async ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateSession(sessionId);
        _ = await WriteSnapshotAsync(snapshot, true, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<SessionSnapshot?> ReadSnapshotAsync(string? id, CancellationToken cancellationToken)
    {
        if (id is not null) SnapshotSessionIds.Validate(id);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await storage.ReadAsync(Key(id), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes is null ? null : SnapshotSessionCodec.Read((byte[])bytes.Clone(), Manager.SessionId, BindingId, id, Owner.Model is IStatefulModel { Stateful: true });
    }

    internal async ValueTask<string?> WriteSnapshotAsync(SessionSnapshot snapshot, bool latest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Owner.ValidateCatalogSession(snapshot);
        var id = latest ? null : SnapshotSessionIds.New();
        var bytes = SnapshotSessionCodec.Write(snapshot, Manager.SessionId, BindingId, id, Owner.Model is IStatefulModel { Stateful: true });
        cancellationToken.ThrowIfCancellationRequested();
        await storage.WriteAsync(Key(id), bytes, cancellationToken).ConfigureAwait(false);
        // A successful backend return confirms this write even if cancellation raced its acceptance.
        return id;
    }

    internal async ValueTask WriteImmutableAndLatestAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Owner.ValidateCatalogSession(snapshot);
        var id = SnapshotSessionIds.New();
        var stateful = Owner.Model is IStatefulModel { Stateful: true };
        var immutable = SnapshotSessionCodec.Write(snapshot, Manager.SessionId, BindingId, id, stateful);
        var latest = SnapshotSessionCodec.Write(snapshot, Manager.SessionId, BindingId, null, stateful);
        cancellationToken.ThrowIfCancellationRequested();
        await storage.WriteAsync(Key(id), immutable, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await storage.WriteAsync(Key(null), latest, cancellationToken).ConfigureAwait(false);
        // Only the accepted latest return allows the Agent to advance its saved frontier.
    }

    internal async ValueTask<IReadOnlyList<string>> ListAsync(int? limit, string? cursor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is <= 0) return Array.Empty<string>();
        if (cursor is not null) SnapshotSessionIds.Validate(cursor);
        var prefix = Prefix + "immutable_history/";
        var keys = await storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = keys.Where(key => key.StartsWith(prefix + "snapshot_", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal))
            .Select(key => key.Substring(prefix.Length + 9, key.Length - prefix.Length - 14))
            .Where(SnapshotSessionIds.IsValid).Distinct(StringComparer.Ordinal)
            .Where(id => cursor is null || string.CompareOrdinal(id, cursor) > 0).OrderBy(id => id, StringComparer.Ordinal);
        return Array.AsReadOnly((limit is { } count ? ids.Take(count) : ids).ToArray());
    }

    internal void Release() { if (Interlocked.Exchange(ref _released, 1) == 0) Manager.Release(this); }
    private void ValidateSession(string sessionId)
    {
        if (sessionId != Manager.SessionId) throw new InvalidOperationException("The catalog adapter belongs to another session.");
    }
}
