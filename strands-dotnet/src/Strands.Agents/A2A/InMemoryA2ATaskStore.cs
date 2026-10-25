using System.Text;
using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>An in-memory task store with explicit record and aggregate size limits.</summary>
/// <remarks>Byte limits count retained UTF-8 JSON, rather than process memory. Records are never silently evicted.</remarks>
public sealed class InMemoryA2ATaskStore : IA2ATaskStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (JsonElement Task, int Bytes)> _tasks = new(StringComparer.Ordinal);
    private readonly int _maxTasks;
    private readonly int _maxTaskBytes;
    private readonly long _maxTotalBytes;
    private long _totalBytes;
    /// <summary>Configures limits without allocating task records. Capacity errors preserve every existing record.</summary>
    public InMemoryA2ATaskStore(int maxTasks = 1000, int maxTaskBytes = 16 * 1024 * 1024, long maxTotalBytes = 64 * 1024 * 1024)
    {
        if (maxTasks <= 0) throw new ArgumentOutOfRangeException(nameof(maxTasks));
        if (maxTaskBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTaskBytes));
        if (maxTotalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
        _maxTasks = maxTasks;
        _maxTaskBytes = maxTaskBytes; _maxTotalBytes = maxTotalBytes;
    }
    /// <inheritdoc />
    public ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        A2AJson.RequireObject(task); var size = Encoding.UTF8.GetByteCount(task.GetRawText());
        if (size > _maxTaskBytes) throw new A2AProtocolException("The task exceeded MaxTaskBytes.");
        var id = A2ATaskRecord.Validate(task);
        var detached = task.Clone();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_tasks.ContainsKey(id) && _tasks.Count >= _maxTasks) throw new A2AProtocolException("The task store exceeded MaxTasks.");
            var retainedBytes = _totalBytes - (_tasks.TryGetValue(id, out var existing) ? existing.Bytes : 0);
            if (size > _maxTotalBytes - retainedBytes) throw new A2AProtocolException("The task store exceeded MaxTotalBytes.");
            _tasks[id] = (detached, size); _totalBytes = retainedBytes + size;
        }
        return default;
    }
    /// <inheritdoc />
    public ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId)); cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return new(_tasks.TryGetValue(taskId, out var task) ? task.Task.Clone() : (JsonElement?)null);
    }
}
