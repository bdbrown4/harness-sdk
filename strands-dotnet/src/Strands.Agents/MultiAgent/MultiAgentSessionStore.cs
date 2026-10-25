using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.MultiAgent;

/// <summary>Loads and saves native orchestration checkpoints using an opaque session identifier.</summary>
public interface IMultiAgentSessionStore
{
    /// <summary>Returns the stored checkpoint, or null when the session does not exist.</summary>
    ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Persists an immutable checkpoint for an opaque session identifier.</summary>
    ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>An orchestration session repository over a borrowed byte-storage backend.</summary>
public sealed class StorageMultiAgentSessionStore : IMultiAgentSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IStorage _storage;
    private readonly int _maxSnapshotBytes;
    /// <summary>Creates a repository without transferring backend ownership. The stored JSON budget defaults to 64 MiB.</summary>
    public StorageMultiAgentSessionStore(IStorage storage, int maxSnapshotBytes = 64 * 1024 * 1024)
    { Guard.NotNull(storage, nameof(storage)); if (maxSnapshotBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxSnapshotBytes)); _storage = storage; _maxSnapshotBytes = maxSnapshotBytes; }
    /// <inheritdoc />
    public async ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await _storage.ReadAsync(Key(sessionId), cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;
        if (bytes.Length > _maxSnapshotBytes) throw new InvalidDataException("The orchestration session exceeds its stored JSON budget.");
        try { return JsonSerializer.Deserialize<MultiAgentSnapshot>(bytes, Json) ?? throw new InvalidDataException("An orchestration session cannot be null."); }
        catch (JsonException error) { throw new InvalidDataException("The orchestration session contains malformed JSON.", error); }
    }
    /// <inheritdoc />
    public async ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(snapshot, nameof(snapshot)); cancellationToken.ThrowIfCancellationRequested();
        var key = Key(sessionId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        if (bytes.Length > _maxSnapshotBytes) throw new InvalidDataException("The orchestration session exceeds its stored JSON budget.");
        await _storage.WriteAsync(key, bytes, cancellationToken).ConfigureAwait(false);
    }
    private static string Key(string sessionId)
    {
        Guard.NotNullOrWhiteSpace(sessionId, nameof(sessionId));
        using var hash = SHA256.Create();
        return "strands/multiagent/" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sessionId))).Replace("-", "").ToLowerInvariant() + ".json";
    }
}
