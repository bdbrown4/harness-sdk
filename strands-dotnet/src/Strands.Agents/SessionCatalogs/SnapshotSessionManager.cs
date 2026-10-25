using System.Runtime.ExceptionServices;
using Strands.Agents.SessionCatalogs;
using Strands.Agents.Storage;

namespace Strands.Agents;

/// <summary>Stores complete native latest and immutable checkpoints in borrowed byte storage.</summary>
/// <remarks>The selected Agent.Id must be stable when reopening a scope. Automatic checkpoint policy is explicit; the catalog never owns the backend.</remarks>
public sealed class SnapshotSessionManager : IPlugin
{
    private readonly IStorage _storage;
    private readonly IStorage _rawStorage;
    private readonly object _gate = new();
    private readonly Dictionary<string, SnapshotSessionStore> _bindings = new(StringComparer.Ordinal);
    private bool _deleting;

    /// <summary>Creates a lazy session catalog without storage I/O.</summary>
    /// <param name="storage">The borrowed byte backend; a preselected namespace is used as-is.</param>
    /// <param name="sessionId">A nonempty single-segment catalog identity.</param>
    public SnapshotSessionManager(IStorage storage, string sessionId = "default-session")
        : this(storage, sessionId, SnapshotSessionSaveStrategy.Invocation) { }

    /// <summary>Creates a borrowed-storage catalog with an explicit automatic checkpoint profile.</summary>
    /// <param name="storage">The borrowed byte backend.</param>
    /// <param name="sessionId">The catalog identity.</param>
    /// <param name="saveLatestOn">The latest-write strategy.</param>
    /// <param name="snapshotTrigger">An optional synchronous immutable-checkpoint predicate.</param>
    public SnapshotSessionManager(IStorage storage, string sessionId, SnapshotSessionSaveStrategy saveLatestOn, Func<Agent, bool>? snapshotTrigger = null)
    {
        Guard.NotNull(storage, nameof(storage));
        SnapshotSessionCodec.ValidateIdentifier(sessionId, nameof(sessionId));
        if (!Enum.IsDefined(typeof(SnapshotSessionSaveStrategy), saveLatestOn)) throw new ArgumentOutOfRangeException(nameof(saveLatestOn));
        _rawStorage = storage;
        _storage = storage is IStorageCharacteristics { IsNamespaced: true } ? storage : storage.Namespace("session");
        SessionId = sessionId;
        SaveLatestOn = saveLatestOn;
        SnapshotTrigger = snapshotTrigger;
    }

    /// <inheritdoc />
    public string Name => "strands:snapshot-session-manager";
    /// <summary>The catalog's session identity.</summary>
    public string SessionId { get; }
    /// <summary>The selected automatic checkpoint strategy.</summary>
    public SnapshotSessionSaveStrategy SaveLatestOn { get; }
    internal Func<Agent, bool>? SnapshotTrigger { get; }
    /// <inheritdoc />
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(agent, nameof(agent));
        cancellationToken.ThrowIfCancellationRequested();
        _ = agent.GetSnapshotSessionStore(this);
        return default;
    }
    /// <inheritdoc />
    public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();

    /// <summary>Saves latest, or creates an immutable checkpoint and returns its opaque handle.</summary>
    /// <param name="target">An inactive agent bound to this manager.</param>
    /// <param name="isLatest">True replaces latest; false creates an immutable record without changing latest.</param>
    /// <param name="cancellationToken">Cancellation before an accepted storage write.</param>
    /// <returns>Null for latest, otherwise the accepted immutable handle.</returns>
    public ValueTask<string?> SaveSnapshotAsync(Agent target, bool isLatest, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(target, nameof(target));
        return target.SaveCatalogSnapshotAsync(this, isLatest, cancellationToken);
    }

    /// <summary>Lists immutable handles oldest first; latest is excluded.</summary>
    /// <param name="target">The agent bound to this manager.</param>
    /// <param name="limit">An optional result cap; nonpositive returns an empty list without I/O.</param>
    /// <param name="startAfter">An optional exclusive canonical UUIDv7 cursor.</param>
    /// <param name="cancellationToken">Cancellation for listing.</param>
    /// <returns>A detached ordinally ordered list.</returns>
    public ValueTask<IReadOnlyList<string>> ListSnapshotIdsAsync(Agent target, int? limit = null, string? startAfter = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(target, nameof(target));
        return target.GetSnapshotSessionStore(this).ListAsync(limit, startAfter, cancellationToken);
    }

    /// <summary>Restores latest or an immutable checkpoint without replaying model or tool bodies.</summary>
    /// <param name="target">An inactive agent bound to this manager.</param>
    /// <param name="snapshotId">The immutable handle, or null for latest.</param>
    /// <param name="cancellationToken">Cancellation before live restoration commits.</param>
    /// <returns>False if no requested record exists.</returns>
    public ValueTask<bool> RestoreSnapshotAsync(Agent target, string? snapshotId = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(target, nameof(target));
        if (snapshotId is not null) SnapshotSessionIds.Validate(snapshotId);
        return target.RestoreCatalogSnapshotAsync(this, snapshotId, cancellationToken);
    }

    /// <summary>Deletes this session's catalog records and known automatically scoped context originals.</summary>
    /// <param name="cancellationToken">Cancellation before each accepted storage mutation.</param>
    public async ValueTask DeleteSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var releases = new List<Action>();
        SnapshotSessionStore[] bindings;
        lock (_gate)
        {
            if (_deleting) throw new InvalidOperationException("This snapshot session is already being deleted.");
            bindings = _bindings.Values.OrderBy(store => store.BindingId, StringComparer.Ordinal).ToArray();
            _deleting = true;
        }
        Exception? failure = null;
        try
        {
            foreach (var store in bindings)
                releases.Add(store.Owner.ReserveCatalogDeletion(this, cancellationToken));
            var prefix = SessionId + "/";
            var keys = (await _storage.ListAsync(prefix, cancellationToken).ConfigureAwait(false)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            // Validate the complete detached result before the first destructive operation.
            foreach (var key in keys)
            {
                if (key is null || key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("The catalog listing contains a key outside the selected session.");
                string normalized;
                try { normalized = StorageKey.Normalize(key); }
                catch (StorageException error) { throw new InvalidDataException("The catalog listing contains an invalid key.", error); }
                if (!string.Equals(key, normalized, StringComparison.Ordinal))
                    throw new InvalidDataException("The catalog listing contains a noncanonical key.");
            }
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var stashBindings = bindings.Where(store => store.Owner.HasCatalogSessionStash).ToArray();
            if (stashBindings.Length == 0)
                await ClearSessionStashAsync(null, cancellationToken).ConfigureAwait(false);
            else
                foreach (var store in stashBindings)
                    await ClearSessionStashAsync(store.Owner, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            for (var index = releases.Count - 1; index >= 0; index--)
            {
                try { releases[index](); }
                catch (Exception error)
                {
                    if (failure is null) failure = error;
                    else if (!ReferenceEquals(failure, error))
                    {
                        if (failure.Data["Strands.CleanupErrors"] is not List<Exception> errors) failure.Data["Strands.CleanupErrors"] = errors = [];
                        if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error);
                    }
                }
            }
            lock (_gate) _deleting = false;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async ValueTask ClearSessionStashAsync(Agent? owner, CancellationToken cancellationToken)
    {
        try
        {
            if (owner is null)
                // Source fallback assumes originals share the exact constructor backend.
                await new Context.Stash(_rawStorage, SessionId, "catalog-cleanup").ClearSessionAsync(cancellationToken).ConfigureAwait(false);
            else
                await owner.ClearCatalogSessionStashAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // Python profile: failed context cleanup is diagnostic after catalog deletion.
            try { SdkLogging.Warning(error, "Failed to clear snapshot session context: {Error}", error.Message); }
            catch (Exception) { /* Observers cannot turn best-effort context cleanup into a failure. */ }
        }
    }

    internal static SnapshotSessionManager? Select(AgentOptions options)
    {
        var managers = options.Plugins.OfType<SnapshotSessionManager>().ToArray();
        if (managers.Length > 1) throw new ArgumentException("Only one snapshot catalog can bind to an agent.", nameof(options));
        if (managers.Length == 0) return null;
        if (options.SessionStore is not null || options.Plugins.OfType<SessionRepositories.RepositorySessionManager>().Any())
            throw new ArgumentException("A snapshot catalog cannot be combined with another session store or indexed manager.", nameof(options));
        if (options.SessionId is not null && options.SessionId != managers[0].SessionId)
            throw new ArgumentException("The configured catalog session identifiers conflict.", nameof(options));
        SnapshotSessionCodec.ValidateIdentifier(options.Id, nameof(options.Id));
        return managers[0];
    }

    internal SnapshotSessionStore Bind(Agent agent)
    {
        var store = new SnapshotSessionStore(this, _storage, agent, agent.Id);
        lock (_gate)
        {
            if (_deleting) throw new InvalidOperationException("A new agent cannot bind while its snapshot session is being deleted.");
            if (_bindings.ContainsKey(store.BindingId)) throw new ArgumentException("Agent identifiers must be unique within a snapshot manager.", nameof(agent));
            _bindings.Add(store.BindingId, store);
        }
        return store;
    }

    internal void Release(SnapshotSessionStore store)
    {
        lock (_gate)
            if (_bindings.TryGetValue(store.BindingId, out var current) && ReferenceEquals(current, store)) _bindings.Remove(store.BindingId);
    }
}
