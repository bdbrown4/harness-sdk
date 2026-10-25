using System.Text.Json;

namespace Strands.Agents.A2A;

public sealed partial class A2AServer
{
    private readonly HashSet<Task> _storeReads = [];
    private static readonly AsyncLocal<StoreReadScope?> StoreRead = new();

    private async Task<HostedTask?> LoadStoredTaskAsync(string id, CancellationToken cancellationToken)
    {
        if (_options.TaskStore is not { } store) return null;
        cancellationToken.ThrowIfCancellationRequested();
        for (var scope = StoreRead.Value; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope.Server, this)) throw new InvalidOperationException("A task-store callback cannot recursively load from its own server.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken shutdown;
        lock (_gate)
        {
            if (_disposeTask is not null) throw new ObjectDisposedException(nameof(A2AServer));
            shutdown = _shutdown.Token; _storeReads.Add(completion.Task);
        }
        var previousStore = StoreRead.Value; var previousHost = Scope.Value;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
            StoreRead.Value = new(this, previousStore); Scope.Value = new(this, previousHost);
            var task = await store.LoadAsync(id, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (task is null) return null;
            var restored = HostedTask.Restore(task.Value, _options.MaxTaskBytes);
            if (restored.Id != id) throw new A2AProtocolException("The stored task identifier does not match its requested key.");
            linked.Token.ThrowIfCancellationRequested();
            return restored;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("The task-store read was canceled.", error, cancellationToken); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new TaskPersistenceException(error); }
        finally
        {
            StoreRead.Value = previousStore; Scope.Value = previousHost;
            lock (_gate) _storeReads.Remove(completion.Task);
            completion.TrySetResult(true);
        }
    }
    private static async ValueTask PersistTaskAsync(IA2ATaskStore store, JsonElement task, CancellationToken cancellationToken)
    {
        try { await store.SaveAsync(task, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new TaskPersistenceException(error); }
    }
    private sealed class TaskPersistenceException(Exception innerException) : Exception("The A2A task store could not complete the operation.", innerException);
    private sealed record StoreReadScope(A2AServer Server, StoreReadScope? Parent);
}
