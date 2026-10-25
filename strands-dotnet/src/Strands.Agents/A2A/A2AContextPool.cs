namespace Strands.Agents.A2A;

internal sealed class A2AContextPool(A2AExecutorOptions options) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private long _sequence;
    private bool _disposed;
    private Task? _disposeTask;

    internal async ValueTask<Lease> AcquireAsync(string id, CancellationToken cancellationToken)
        => await AcquireCoreAsync(id, create: true, cancellationToken).ConfigureAwait(false);
    internal async ValueTask<Lease> AcquireExistingAsync(string id, CancellationToken cancellationToken)
        => await AcquireCoreAsync(id, create: false, cancellationToken).ConfigureAwait(false);
    private async ValueTask<Lease> AcquireCoreAsync(string id, bool create, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        CancellationToken shutdown;
        Entry? evicted = null;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(A2AExecutor));
            shutdown = _shutdown.Token;
            if (!_entries.TryGetValue(id, out entry!))
            {
                if (!create) throw new A2ARpcException(-32001, "The task context was not found.");
                if (_entries.Count >= options.MaxContexts)
                {
                    evicted = _entries.Values.Where(value => value.Reservations == 0 && value.Agent?.InterruptState is null).OrderBy(value => value.LastUsed).FirstOrDefault();
                    if (evicted is null) throw new A2ARpcException(-32000, "The A2A context capacity is occupied by active or paused executions.");
                    _entries.Remove(evicted.Id);
                }
                _entries.Add(id, entry = new(id));
            }
            entry.Reservations++; entry.LastUsed = ++_sequence;
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
        var acquired = false;
        try
        {
            if (evicted?.Agent is { } previous && options.OwnsAgents) await previous.DisposeAsync().ConfigureAwait(false);
            await entry.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            linked.Token.ThrowIfCancellationRequested();
            if (entry.Agent is null) entry.Agent = await CreateAgentAsync(entry, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return new(this, entry, linked);
        }
        catch
        {
            if (acquired) entry.Gate.Release();
            ReleaseReservation(entry); linked.Dispose();
            throw;
        }
    }
    private async Task<Agent> CreateAgentAsync(Entry entry, CancellationToken cancellationToken)
    {
        var agent = await options.AgentFactory(entry.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The A2A agent factory returned null.");
        var transferred = true;
        try
        {
            lock (_gate)
            {
                if (_entries.Values.Any(value => !ReferenceEquals(value, entry) && ReferenceEquals(value.Agent, agent)))
                { transferred = false; throw new InvalidOperationException("The A2A agent factory must return independent agents for different contexts."); }
                entry.Agent = agent;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await agent.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return agent;
        }
        catch (Exception primary)
        {
            if (options.OwnsAgents && transferred)
            {
                try { await agent.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { A2AHttp.AddCleanup(primary, cleanup); }
            }
            lock (_gate) if (ReferenceEquals(entry.Agent, agent)) entry.Agent = null;
            throw;
        }
    }
    private void ReleaseReservation(Entry entry)
    {
        lock (_gate)
        {
            entry.Reservations--; entry.LastUsed = ++_sequence;
            if (entry.Reservations == 0 && entry.Agent is null && _entries.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry)) _entries.Remove(entry.Id);
        }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? completion = null;
        Entry[] entries = [];
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true; entries = _entries.Values.ToArray();
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
            }
        }
        if (completion is not null) _ = DisposeCoreAsync(entries, completion);
        return new(_disposeTask!);
    }
    private async Task DisposeCoreAsync(Entry[] entries, TaskCompletionSource<bool> completion)
    {
        Exception? primary = null;
        try { _shutdown.Cancel(); }
        catch (Exception error) { primary = error; }
        foreach (var entry in entries)
        {
            await entry.Gate.WaitAsync().ConfigureAwait(false);
            try { if (entry.Agent is { } agent && options.OwnsAgents) await agent.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { if (primary is null) primary = error; else A2AHttp.AddCleanup(primary, error); }
            finally { entry.Gate.Release(); }
        }
        lock (_gate) _entries.Clear();
        _shutdown.Dispose();
        if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
    }
    internal sealed class Entry(string id)
    {
        internal string Id { get; } = id;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int Reservations { get; set; }
        internal long LastUsed { get; set; }
        internal Agent? Agent { get; set; }
        internal string? PendingTaskId { get; set; }
    }
    internal sealed class Lease(A2AContextPool owner, Entry entry, CancellationTokenSource cancellation) : IDisposable
    {
        private int _released;
        internal Agent Agent => entry.Agent!;
        internal string? PendingTaskId { get => entry.PendingTaskId; set => entry.PendingTaskId = value; }
        internal CancellationToken CancellationToken => cancellation.Token;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            entry.Gate.Release(); owner.ReleaseReservation(entry); cancellation.Dispose();
        }
    }
}
