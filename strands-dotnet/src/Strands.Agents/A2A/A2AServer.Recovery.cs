using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private async Task<Execution> StartAsync(HttpRequestMessage request, JsonElement parameters, CancellationToken cancellationToken)
    {
        var message = A2AJson.RequiredObject(parameters, "message");
        var push = await AuthorizeMessagePushAsync(request, parameters, cancellationToken).ConfigureAwait(false);
        if (A2AJson.OptionalString(message, "taskId") is { } id) await RestoreTaskForOperationAsync(id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Start(parameters, push);
    }
    private async Task RestoreTaskForOperationAsync(string id, CancellationToken cancellationToken)
    {
        lock (_gate) if (_tasks.TryGetValue(id, out var existing) && existing.Started) return;
        var restored = await LoadStoredTaskAsync(id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (restored is null) throw new A2ARpcException(-32001, "The task was not found.");
        var state = restored.Snapshot(0).GetProperty("status").GetProperty("state").GetString();
        if (state != "input-required") throw new A2ARpcException(-32004, "The persisted task has no resumable paused execution.");
        lock (_gate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(A2AServer));
            cancellationToken.ThrowIfCancellationRequested();
            if (_tasks.TryGetValue(id, out var current) && current.Started) return;
            MakeTaskRoom(); restored.LastUsed = ++_sequence; _tasks.Add(id, restored);
        }
    }
    private void MakeTaskRoom()
    {
        if (_tasks.Count < _options.MaxTasks) return;
        var evicted = _tasks.Values.Where(value => value.Terminal && !value.Active).OrderBy(value => value.LastUsed).FirstOrDefault();
        if (evicted is null) throw new A2ARpcException(-32000, "The task capacity is occupied by active or paused executions.");
        _tasks.Remove(evicted.Id);
        if (_options.TaskStore is null && _options.PushNotifications is not null) _retiredPushTasks.Enqueue(evicted.Id);
    }
}
