namespace Strands.Agents.Mcp;

/// <summary>Owns one independently filtered tool-change observer and its accepted snapshot.</summary>
public sealed class McpToolChangeSubscription : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly McpClient _owner;
    private readonly McpClient.ToolChangeChannel _channel;
    private readonly Func<McpToolsChanged, CancellationToken, ValueTask> _callback;
    private readonly McpToolListOptions _discovery;
    private readonly McpToolChangeOptions _options;
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _initialPhysical = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private McpToolsChanged? _current;
    private TaskCompletionSource<bool>? _worker;
    private TaskCompletionSource<bool>? _disposal;
    private Exception? _terminal;
    private long _notifications;
    private bool _pending;
    private bool _stopping;
    internal volatile bool IsCancelling;

    internal McpToolChangeSubscription(McpClient owner, McpClient.ToolChangeChannel channel,
        Func<McpToolsChanged, CancellationToken, ValueTask> callback, McpToolListOptions discovery, McpToolChangeOptions options)
    {
        _owner = owner; _channel = channel; _callback = callback; _discovery = discovery; _options = options;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(channel.Lifetime.Token);
    }

    internal CancellationToken LifetimeToken => _lifetime.Token;
    internal Task Drained => _drained.Task;
    internal Task RefreshDrain { get { lock (_gate) return _worker?.Task ?? Task.CompletedTask; } }
    internal void CompleteInitial() => _initialPhysical.TrySetResult(true);

    /// <summary>The most recently accepted immutable snapshot.</summary>
    public McpToolsChanged Current { get { lock (_gate) return _current ?? throw new InvalidOperationException("Initial MCP tool discovery has not completed."); } }
    /// <summary>Completes when this observer stops, or faults when its notification channel is lost.</summary>
    public Task Completion => _completion.Task;
    /// <summary>Removes this observer and physically joins its accepted work.</summary>
    public ValueTask DisposeAsync()
    {
        _owner.RefuseToolChangeCallbackReentry();
        return new(BeginStop(null));
    }

    internal void Initialize(IReadOnlyList<McpTool> tools)
    {
        TaskCompletionSource<bool>? worker;
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException(_lifetime.Token);
            _current = new(0, Array.Empty<string>(), tools);
            worker = ReserveWorker();
        }
        if (worker is not null) _ = RefreshAsync(worker);
    }

    internal void Notify()
    {
        TaskCompletionSource<bool>? worker;
        lock (_gate)
        {
            if (_stopping) return;
            _notifications++;
            _pending = true;
            worker = ReserveWorker();
        }
        if (worker is not null) _ = RefreshAsync(worker);
    }

    private TaskCompletionSource<bool>? ReserveWorker()
    {
        if (_stopping || !_pending || _current is null || _worker is not null) return null;
        return _worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task RefreshAsync(TaskCompletionSource<bool> worker)
    {
        var receipt = new McpClient.ToolChangeCallbackScope(_owner, this);
        try
        {
            while (true)
            {
                long observed;
                lock (_gate)
                {
                    if (_stopping || !_pending) return;
                    _pending = false;
                    observed = _notifications;
                }
                try
                {
                    while (true)
                    {
                        if (_options.Debounce != TimeSpan.Zero) await Task.Delay(_options.Debounce, _lifetime.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (_options.Profile != McpToolChangeProfile.TypeScript || observed == _notifications)
                            { if (_options.Profile == McpToolChangeProfile.TypeScript) _pending = false; break; }
                            observed = _notifications;
                        }
                    }
                    var tools = await _owner.ListToolsAsync(_discovery, _lifetime.Token).ConfigureAwait(false);
                    _lifetime.Token.ThrowIfCancellationRequested();
                    McpToolsChanged snapshot;
                    lock (_gate)
                    {
                        if (_stopping) return;
                        var previous = _current!;
                        snapshot = new(checked(previous.Version + 1), previous.Tools.Select(tool => tool.Spec.Name), tools);
                        _current = snapshot;
                    }
                    await _callback(snapshot, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    if (_options.Profile == McpToolChangeProfile.TypeScript) lock (_gate) _pending = false;
                    _owner.WarnToolChangeFailure(error);
                }
            }
        }
        finally
        {
            TaskCompletionSource<bool>? next;
            lock (_gate)
            {
                if (ReferenceEquals(_worker, worker)) _worker = null;
                next = ReserveWorker();
            }
            receipt.Dispose();
            worker.TrySetResult(true);
            if (next is not null) _ = RefreshAsync(next);
        }
    }

    internal void Stop(Exception? error) => _ = BeginStop(error);

    private Task BeginStop(Exception? error)
    {
        TaskCompletionSource<bool> completion;
        Task? worker;
        lock (_gate)
        {
            _terminal ??= error;
            if (_disposal is not null) return _disposal.Task;
            completion = _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopping = true;
            worker = _worker?.Task;
        }
        _ = CloseAsync(worker, completion);
        return completion.Task;
    }

    private async Task CloseAsync(Task? worker, TaskCompletionSource<bool> completion)
    {
        Exception? failure = _terminal;
        try
        {
            IsCancelling = true;
            try { _lifetime.Cancel(); }
            catch (Exception error) { McpClient.AppendToolChangeFailure(ref failure, error); }
            finally { IsCancelling = false; }
            await _initialPhysical.Task.ConfigureAwait(false);
            if (worker is not null) await worker.ConfigureAwait(false);
        }
        catch (Exception error) { McpClient.AppendToolChangeFailure(ref failure, error); }
        finally { _drained.TrySetResult(true); }
        try
        {
            var channelClose = _owner.ReleaseToolChangeSubscription(_channel, this);
            if (channelClose is not null) await channelClose.ConfigureAwait(false);
        }
        catch (Exception error) { McpClient.AppendToolChangeFailure(ref failure, error); }
        finally { _lifetime.Dispose(); }
        if (failure is null) { _completion.TrySetResult(true); completion.TrySetResult(true); }
        else
        {
            _completion.TrySetException(failure); completion.TrySetException(failure);
            _ = _completion.Task.Exception; _ = completion.Task.Exception;
        }
    }
}
