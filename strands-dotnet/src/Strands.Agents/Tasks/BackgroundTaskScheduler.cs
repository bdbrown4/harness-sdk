namespace Strands.Agents.Tasks;

internal enum BackgroundTaskStatus { Queued, Working, InputRequired, Completed, Failed, Cancelled }
internal enum BackgroundTaskFailureType { ToolError, ExecutionError, Timeout }
internal sealed record BackgroundTaskFailure(BackgroundTaskFailureType Type, string Message);
internal sealed record BackgroundTaskSnapshot(string TaskId, string ToolUseId, string ToolName, BackgroundTaskStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset LastUpdatedAt, ToolResultBlock? Result = null, BackgroundTaskFailure? Error = null,
    InterruptState? InterruptState = null)
{
    internal string InvocationStateId { get; init; } = string.Empty;
    internal bool IsTerminal => Status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed or BackgroundTaskStatus.Cancelled;
    internal bool IsSettled => IsTerminal || Status == BackgroundTaskStatus.InputRequired;
    internal BackgroundTaskSnapshot Copy() => this with { Result = Result is null ? null : (ToolResultBlock)Message.CloneBlock(Result), InterruptState = InterruptState?.Copy() };
}
internal sealed record BackgroundTaskExecution(string TaskId, string ToolUseId, string ToolName, string InvocationStateId, InterruptState? State, CancellationToken CancellationToken);
internal sealed record BackgroundTaskOutcome(BackgroundTaskStatus Status, ToolResultBlock? Result = null, BackgroundTaskFailure? Error = null, InterruptState? State = null);

internal sealed class BackgroundTaskScheduler
{
    private readonly object _gate = new();
    private readonly int _maxConcurrency;
    private readonly TimeSpan? _timeout;
    private readonly Func<BackgroundTaskExecution, Task<BackgroundTaskOutcome>> _execute;
    private readonly Action<BackgroundTaskSnapshot>? _onTaskUpdated;
    private readonly Dictionary<string, BackgroundTaskSnapshot> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Execution> _active = new(StringComparer.Ordinal);
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Queue<BackgroundTaskSnapshot> _notifications = new();
    private readonly List<Exception> _cleanupErrors = [];
    private bool _notifying;
    private bool _stopping;
    private TaskCompletionSource<bool> _changed = Signal();

    internal BackgroundTaskScheduler(int maxConcurrency, TimeSpan? timeout, Func<BackgroundTaskExecution, Task<BackgroundTaskOutcome>> execute, Action<BackgroundTaskSnapshot>? onTaskUpdated = null)
    {
        ValidateLimits(maxConcurrency, timeout);
        _maxConcurrency = maxConcurrency; _timeout = timeout; _execute = execute ?? throw new ArgumentNullException(nameof(execute)); _onTaskUpdated = onTaskUpdated;
    }
    internal static void ValidateLimits(int maxConcurrency, TimeSpan? timeout)
    {
        if (maxConcurrency <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (timeout is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    internal IReadOnlyList<Exception> CleanupErrors { get { lock (_gate) return _cleanupErrors.ToArray(); } }
    internal BackgroundTaskSnapshot Submit(string toolUseId, string toolName, string invocationStateId = "")
    {
        Guard.NotNullOrWhiteSpace(toolUseId, nameof(toolUseId)); Guard.NotNullOrWhiteSpace(toolName, nameof(toolName));
        lock (_gate)
        {
            if (_stopping) throw new InvalidOperationException("The background task scheduler is shutting down.");
            var now = DateTimeOffset.UtcNow;
            var task = new BackgroundTaskSnapshot(Guid.NewGuid().ToString(), toolUseId, toolName, BackgroundTaskStatus.Queued, now, now) { InvocationStateId = invocationStateId };
            _tasks.Add(task.TaskId, task); Notify(task); Enqueue(task.TaskId); StartQueued();
            return _tasks[task.TaskId].Copy();
        }
    }
    internal BackgroundTaskSnapshot? Get(string taskId) { lock (_gate) return _tasks.TryGetValue(taskId, out var task) ? task.Copy() : null; }
    internal IReadOnlyList<BackgroundTaskSnapshot> List() { lock (_gate) return _tasks.Values.Select(task => task.Copy()).ToArray(); }
    internal async Task StopAsync()
    {
        string[] pending;
        lock (_gate) { _stopping = true; pending = _tasks.Values.Where(task => !task.IsTerminal).Select(task => task.TaskId).ToArray(); }
        foreach (var id in pending) Cancel(id);
        await WaitForIdleAsync().ConfigureAwait(false);
        var errors = CleanupErrors;
        if (errors.Count > 0) throw new AggregateException("Background task cancellation callbacks failed.", errors);
    }
    internal BackgroundTaskSnapshot Cancel(string taskId)
    {
        Execution? active;
        BackgroundTaskSnapshot result;
        lock (_gate)
        {
            var task = Require(taskId);
            if (task.IsTerminal) return task.Copy();
            Commit(task with { Status = BackgroundTaskStatus.Cancelled, InterruptState = null });
            _queued.Remove(taskId); _active.TryGetValue(taskId, out active);
            result = _tasks[taskId].Copy();
        }
        Abort(active);
        return result;
    }
    internal async Task<BackgroundTaskSnapshot> WaitAsync(string taskId, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (_gate) { var task = Require(taskId); if (task.IsSettled) return task.Copy(); changed = _changed.Task; }
            await TaskCancellation.WaitAsync(changed, cancellationToken).ConfigureAwait(false);
        }
    }
    internal async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (_gate) { if (_queued.Count == 0 && _active.Count == 0) return; changed = _changed.Task; }
            await TaskCancellation.WaitAsync(changed, cancellationToken).ConfigureAwait(false);
        }
    }
    internal void Remove(IReadOnlyList<string> taskIds)
    {
        Guard.NotNull(taskIds, nameof(taskIds));
        var unique = taskIds.Distinct(StringComparer.Ordinal).ToArray();
        lock (_gate)
        {
            foreach (var id in unique) if (!Require(id).IsTerminal) throw new InvalidOperationException("A background task cannot be removed before reaching a terminal status.");
            foreach (var id in unique) _tasks.Remove(id);
            Pulse();
        }
    }
    internal BackgroundTaskSnapshot Resume(string taskId, Func<InterruptState, (InterruptState State, bool Ready)> update)
    {
        Guard.NotNull(update, nameof(update));
        lock (_gate)
        {
            var task = Require(taskId);
            if (task.Status != BackgroundTaskStatus.InputRequired || task.InterruptState is null) throw new InvalidOperationException("The background task is not waiting for input.");
            var resumed = update(task.InterruptState.Copy());
            if (resumed.State is null) throw new InvalidOperationException("Resuming a background task must retain its interrupt state.");
            Commit(task with { Status = resumed.Ready ? BackgroundTaskStatus.Queued : BackgroundTaskStatus.InputRequired, InterruptState = resumed.State });
            if (resumed.Ready) { Enqueue(taskId); StartQueued(); }
            return _tasks[taskId].Copy();
        }
    }

    private void Enqueue(string taskId) { if (!_active.ContainsKey(taskId) && _queued.Add(taskId)) _queue.Enqueue(taskId); }
    private void StartQueued()
    {
        while (_active.Count < _maxConcurrency && _queue.Count > 0)
        {
            var id = _queue.Dequeue();
            if (!_queued.Remove(id) || !_tasks.TryGetValue(id, out var task) || task.Status != BackgroundTaskStatus.Queued) continue;
            var execution = new Execution(); _active.Add(id, execution);
            Commit(task with { Status = BackgroundTaskStatus.Working });
            _ = Task.Run(() => ExecuteAsync(id, execution));
        }
    }
    private async Task ExecuteAsync(string taskId, Execution execution)
    {
        try
        {
            BackgroundTaskSnapshot task;
            lock (_gate)
            {
                if (!_tasks.TryGetValue(taskId, out var current) || current.Status != BackgroundTaskStatus.Working) return;
                task = current.Copy();
                if (_timeout is { } duration) execution.Timeout = new Timer(_ => TimeOut(taskId, execution), null, duration, System.Threading.Timeout.InfiniteTimeSpan);
            }
            var outcome = await _execute(new(taskId, task.ToolUseId, task.ToolName, task.InvocationStateId, task.InterruptState, execution.Cancellation.Token)).ConfigureAwait(false);
            Validate(outcome, task.ToolUseId);
            lock (_gate)
                if (_tasks.TryGetValue(taskId, out var current) && current.Status == BackgroundTaskStatus.Working)
                    Commit(current with { Status = outcome.Status, Result = outcome.Result, Error = outcome.Error, InterruptState = outcome.State });
        }
        catch (Exception error)
        {
            lock (_gate)
                if (_tasks.TryGetValue(taskId, out var task) && task.Status == BackgroundTaskStatus.Working)
                    Commit(task with { Status = BackgroundTaskStatus.Failed, Error = new(BackgroundTaskFailureType.ExecutionError, error.Message) });
        }
        finally
        {
            lock (_gate)
            {
                _active.Remove(taskId); execution.Timeout?.Dispose();
                if (_tasks.TryGetValue(taskId, out var task) && task.Status == BackgroundTaskStatus.Queued) Enqueue(taskId);
                Pulse(); StartQueued();
            }
            execution.Cancellation.Dispose();
        }
    }
    private void TimeOut(string taskId, Execution execution)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task) || task.Status != BackgroundTaskStatus.Working || !_active.TryGetValue(taskId, out var active) || !ReferenceEquals(active, execution)) return;
            Commit(task with { Status = BackgroundTaskStatus.Failed, InterruptState = null, Error = new(BackgroundTaskFailureType.Timeout, "Background task execution timed out after " + _timeout + ".") });
        }
        Abort(execution);
    }
    private void Abort(Execution? execution)
    {
        if (execution is null) return;
        execution.Timeout?.Dispose();
        try { execution.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* A body that just exited no longer needs cancellation. */ }
        catch (AggregateException error) { lock (_gate) _cleanupErrors.AddRange(error.InnerExceptions); }
    }
    private static void Validate(BackgroundTaskOutcome outcome, string toolUseId)
    {
        if (outcome is null || outcome.Status is not (BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed or BackgroundTaskStatus.InputRequired))
            throw new InvalidOperationException("Background execution must complete, fail, or request input.");
        if (outcome.Status == BackgroundTaskStatus.Completed && outcome.Result is null || outcome.Status == BackgroundTaskStatus.Failed && outcome.Error is null || outcome.Status == BackgroundTaskStatus.InputRequired && outcome.State is null)
            throw new InvalidOperationException("A background execution outcome is missing its result, failure, or interrupt state.");
        if (outcome.Result is { } result && (result.ToolUseId != toolUseId || !Enum.IsDefined(typeof(ToolResultStatus), result.Status)))
            throw new InvalidOperationException("A background result must retain its tool correlation and a valid status.");
    }
    private BackgroundTaskSnapshot Require(string taskId) => _tasks.TryGetValue(taskId, out var task) ? task : throw new KeyNotFoundException("Background task '" + taskId + "' was not found.");
    private void Commit(BackgroundTaskSnapshot task)
    {
        var detached = (task with { LastUpdatedAt = DateTimeOffset.UtcNow }).Copy();
        _tasks[task.TaskId] = detached; Notify(detached); Pulse();
    }
    private void Pulse() { var previous = _changed; _changed = Signal(); previous.TrySetResult(true); }
    private void Notify(BackgroundTaskSnapshot task)
    {
        if (_onTaskUpdated is null) return;
        _notifications.Enqueue(task.Copy());
        if (_notifying) return;
        _notifying = true; _ = Task.Run(NotifyQueued);
    }
    private void NotifyQueued()
    {
        while (true)
        {
            BackgroundTaskSnapshot task;
            lock (_gate) { if (_notifications.Count == 0) { _notifying = false; return; } task = _notifications.Dequeue(); }
            try { _onTaskUpdated!(task); } catch { /* Observers cannot roll back a committed task transition. */ }
        }
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Execution { internal CancellationTokenSource Cancellation { get; } = new(); internal Timer? Timeout { get; set; } }
}
