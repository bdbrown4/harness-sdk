namespace Strands.Agents.Mcp;

public sealed partial class McpRouter
{
    private sealed class CallScope : IDisposable
    {
        private int _active = 1;
        internal CallScope(McpRouter router, AgentConnections state)
        { Router = router; State = state; Parent = Active.Value; Active.Value = this; }
        internal McpRouter Router { get; }
        internal AgentConnections State { get; }
        internal CallScope? Parent { get; }
        internal bool IsActive => Volatile.Read(ref _active) != 0;
        public void Dispose() { Interlocked.Exchange(ref _active, 0); Active.Value = Parent; }
    }
    private sealed class AgentConnections(McpRouter router, int maximum)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
        private readonly HashSet<Connection> _retired = [];
        private readonly CancellationTokenSource _shutdown = new();
        private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _operations;
        private int _physicalConnectors;
        private bool _closed;
        private Task? _closing;
        private readonly SemaphoreSlim _connectSlots = new(maximum, maximum);
        internal CancellationToken ShutdownToken => _shutdown.Token;
        internal IDisposable? Subscription { get; set; }
        ~AgentConnections() { _ = CloseAfterCollectionAsync(); }
        private async Task CloseAfterCollectionAsync()
        {
            try { await CloseAsync().ConfigureAwait(false); }
            catch { /* Garbage-collection cleanup is best effort; deterministic cleanup uses Shutdown or Dispose. */ }
        }
        internal async Task AcquireConnectSlotAsync(CancellationToken cancellationToken)
        {
            await _connectSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) _physicalConnectors++;
        }
        internal void ReleaseConnectSlot()
        {
            lock (_gate)
            {
                _connectSlots.Release();
                if (--_physicalConnectors == 0 && _closed) _connectSlots.Dispose();
            }
        }
        internal IDisposable Enter()
        {
            lock (_gate)
            {
                if (_closing is not null) throw new ObjectDisposedException(nameof(McpRouter));
                _retired.RemoveWhere(connection => connection.CleanupSucceeded);
                _operations++;
                return new Release(() => { lock (_gate) { if (--_operations == 0 && _closing is not null) _drained.TrySetResult(true); } });
            }
        }
        internal string List() { lock (_gate) return string.Join(", ", _connections.Keys.OrderBy(value => value, StringComparer.Ordinal)); }
        internal void Publish(string id, Connection candidate)
        {
            lock (_gate)
            {
                if (_closing is not null) throw new ObjectDisposedException(nameof(McpRouter));
                if (_connections.Count >= maximum) throw new McpRouterToolException("Connection limit of " + maximum + " reached. Disconnect an active connection first. Active connection_ids: " + List());
                if (_connections.ContainsKey(id)) throw new McpRouterToolException("Connection '" + id + "' already exists. Disconnect it first or use a different connection_id.");
                _connections.Add(id, candidate);
            }
        }
        internal Connection Remove(string id)
        {
            lock (_gate)
            {
                if (!_connections.TryGetValue(id, out var connection)) throw new McpRouterToolException("No active connection for id '" + id + "'.");
                _connections.Remove(id); _retired.Add(connection); return connection;
            }
        }
        internal void CompleteDisconnect(Connection connection) { lock (_gate) _retired.Remove(connection); }
        internal ConnectionLease Acquire(string id)
        {
            lock (_gate)
            {
                if (!_connections.TryGetValue(id, out var connection)) throw new McpRouterToolException("No active connection for id '" + id + "'.");
                return connection.Enter();
            }
        }
        internal Task CloseAsync()
        {
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                if (_closing is not null) return _closing;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _closing = completion.Task;
                if (_operations == 0) _drained.TrySetResult(true);
            }
            _ = CloseCoreAsync(completion); return completion.Task;
        }
        private async Task CloseCoreAsync(TaskCompletionSource<bool> completion)
        {
            using var scope = new CallScope(router, this);
            Exception? failure = null;
            try { _shutdown.Cancel(); } catch (Exception error) { failure = error; }
            await _drained.Task.ConfigureAwait(false);
            Connection[] connections;
            lock (_gate) { connections = _connections.Values.Concat(_retired).ToArray(); _connections.Clear(); _retired.Clear(); }
            foreach (var connection in connections) try { await connection.CloseAsync().ConfigureAwait(false); } catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
            try { Subscription?.Dispose(); } catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
            _shutdown.Dispose();
            lock (_gate) { _closed = true; if (_physicalConnectors == 0) _connectSlots.Dispose(); }
            GC.SuppressFinalize(this);
            if (failure is null) completion.TrySetResult(true); else completion.TrySetException(failure);
        }
    }
    private sealed class Connection(McpRouter router, AgentConnections state, string serverName, McpClient client)
    {
        private readonly object _gate = new();
        private int _operations;
        private Task? _closing;
        private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ServerName { get; } = serverName;
        internal McpClient Client { get; } = client;
        internal bool CleanupSucceeded { get { lock (_gate) return _closing?.Status == TaskStatus.RanToCompletion; } }
        internal ConnectionLease Enter()
        {
            lock (_gate)
            {
                if (_closing is not null) throw new ObjectDisposedException(nameof(McpRouter));
                _operations++; return new(this, () => { lock (_gate) { if (--_operations == 0 && _closing is not null) _drained.TrySetResult(true); } });
            }
        }
        internal Task CloseAsync()
        {
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                if (_closing is not null) return _closing;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _closing = completion.Task;
                if (_operations == 0) _drained.TrySetResult(true);
            }
            _ = CloseCoreAsync(completion); return completion.Task;
        }
        private async Task CloseCoreAsync(TaskCompletionSource<bool> completion)
        {
            using var scope = new CallScope(router, state);
            try { await _drained.Task.ConfigureAwait(false); await Client.DisposeAsync().ConfigureAwait(false); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
    private sealed class ConnectionLease(Connection connection, Action release) : IDisposable
    {
        private Action? _release = release;
        internal Connection Connection { get; } = connection;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
