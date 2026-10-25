using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.A2A;

/// <summary>Persists task JSON through a borrowed storage backend using opaque hashed task keys.</summary>
/// <remarks>The backend controls durability and atomic replacement. This adapter does not dispose it or persist native agent state.</remarks>
public sealed class StorageA2ATaskStore : IA2ATaskStore
{
    private readonly IStorage _storage;
    private readonly string _prefix;
    private readonly int _maxTaskBytes;
    /// <summary>Uses a borrowed backend and an isolated namespace. The default per-task limit is 16 MiB.</summary>
    public StorageA2ATaskStore(IStorage storage, string prefix = "a2a/tasks", int maxTaskBytes = 16 * 1024 * 1024)
    {
        Guard.NotNull(storage, nameof(storage)); if (maxTaskBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTaskBytes));
        _storage = storage; _prefix = StorageKey.Normalize(prefix); _maxTaskBytes = maxTaskBytes;
    }
    /// <inheritdoc />
    public async ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); A2AJson.RequireObject(task);
        var raw = task.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > _maxTaskBytes) throw new A2AProtocolException("The task exceeded MaxTaskBytes.");
        var id = A2ATaskRecord.Validate(task); var bytes = Encoding.UTF8.GetBytes(raw);
        cancellationToken.ThrowIfCancellationRequested();
        await _storage.WriteAsync(Key(id), bytes, cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId)); cancellationToken.ThrowIfCancellationRequested();
        var bytes = await _storage.ReadAsync(Key(taskId), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null) return null;
        if (bytes.Length > _maxTaskBytes) throw new A2AProtocolException("The stored task exceeded MaxTaskBytes.");
        JsonElement task;
        try { task = JsonSerializer.Deserialize<JsonElement>(bytes); }
        catch (JsonException error) { throw new A2AProtocolException("The stored task is malformed JSON.", error); }
        if (A2ATaskRecord.Validate(task) != taskId) throw new A2AProtocolException("The stored task identifier does not match its requested key.");
        cancellationToken.ThrowIfCancellationRequested();
        return task;
    }
    private string Key(string taskId)
    {
        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(taskId));
        return _prefix + "/" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".json";
    }
}
