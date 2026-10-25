using System.Text.Json;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents.Tasks;

internal sealed record BackgroundToolExecution(ITool Tool, ToolUseBlock ToolUse, IReadOnlyDictionary<string, object?> InvocationState,
    BackgroundTaskExecution Execution, BackgroundTaskInterrupts Interrupts)
{
    internal ToolSpec? AdmittedSpec { get; init; }
    internal JsonSchema? AdmittedSchema { get; init; }
}

internal sealed class BackgroundTaskInterrupts : IDisposable
{
    private readonly object _gate = new();
    private readonly string _taskId;
    private readonly ToolUseBlock _toolUse;
    private readonly Dictionary<string, Interrupt> _questions;
    private readonly Dictionary<string, InterruptResponse> _responses;
    private bool _closed;
    private ToolExecutionCheckpoint? _body;
    private readonly Dictionary<string, JsonElement> _context;
    internal BackgroundTaskInterrupts(string taskId, ToolUseBlock toolUse, InterruptState? state)
    {
        _taskId = taskId; _toolUse = (ToolUseBlock)Message.CloneBlock(toolUse);
        _questions = (state?.Interrupts ?? []).ToDictionary(value => value.Id, StringComparer.Ordinal);
        _responses = (state?.Responses ?? []).ToDictionary(value => value.InterruptId, StringComparer.Ordinal);
        _body = state?.ToolExecutions.SingleOrDefault();
        _context = (state?.Context ?? new Dictionary<string, JsonElement>()).ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal);
    }
    internal AgentToolCallContext AgentToolContext() => new(_gate, _context,
        () => { if (_closed) throw new InvalidOperationException("A background agent-tool context cannot outlive its execution."); },
        (question, reopen) =>
        {
            var id = "background:" + _taskId + ":" + AgentToolInterruptJournal.Prefix(_toolUse.ToolUseId) + question.Id;
            if (reopen) _responses.Remove(id);
            if (_responses.TryGetValue(id, out var answer)) return answer.Response.Clone();
            var forwarded = new Interrupt(id, question.Name, _toolUse.ToolUseId, question.Reason, InterruptSource.Tool);
            _questions[id] = forwarded;
            throw new InterruptException(forwarded);
        }, () => _questions.Values.Any(AgentToolInterruptJournal.IsForwarded),
        () =>
        {
            var questions = _questions.Values.Where(AgentToolInterruptJournal.IsForwarded).ToArray();
            if (questions.Length > 0 && !questions.Any(question => _responses.ContainsKey(question.Id))) throw new InterruptException(questions);
        });
    internal JsonElement Resolve(string name, JsonElement? reason, JsonElement? response, InterruptSource source)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        lock (_gate)
        {
            if (_closed) throw new InvalidOperationException("A background interrupt context cannot outlive its execution.");
            var id = Id(name, source);
            if (_responses.TryGetValue(id, out var accepted)) return accepted.Response.Clone();
            if (response.HasValue) return InterruptJson.Copy(response, nameof(response))!.Value;
            throw new InterruptException(new Interrupt(id, name, _toolUse.ToolUseId, reason, source));
        }
    }
    internal InterruptState Capture(IReadOnlyList<Interrupt> interrupts)
    {
        lock (_gate)
        {
            foreach (var interrupt in interrupts)
            {
                if (interrupt.ToolUseId != _toolUse.ToolUseId || interrupt.IsBatch || interrupt.Id != Id(interrupt.Name, interrupt.Source) &&
                    !(interrupt.Source == InterruptSource.Tool && interrupt.Id.StartsWith("background:" + _taskId + ":" + AgentToolInterruptJournal.Prefix(_toolUse.ToolUseId), StringComparison.Ordinal)))
                    throw new InvalidOperationException("A background execution cannot capture an interrupt belonging to another execution.");
            }
            foreach (var interrupt in interrupts) _questions[interrupt.Id] = interrupt;
            return new(new Message(Role.Assistant, new ContentBlock[] { _toolUse }), [], _questions.Values.ToArray(), _responses.Values.ToArray(), _body is null ? [] : new[] { _body }) { Context = _context };
        }
    }
    private string Id(string name, InterruptSource source)
    {
        if (source is not (InterruptSource.Tool or InterruptSource.Hook or InterruptSource.Middleware)) throw new ArgumentOutOfRangeException(nameof(source));
        return "background:" + _taskId + ":" + source + ":" + name;
    }
    public void Dispose() { lock (_gate) _closed = true; }
    internal void RecordBody(ToolUseBlock call, string toolName, ToolResultBlock result) { lock (_gate) _body = new(call, toolName, result); }
    internal ToolResultBlock? ReuseBody(ToolUseBlock call, string toolName)
    {
        lock (_gate)
        {
            if (_body is null) return null;
            if (_body.ToolName != toolName || _body.ToolUse.Name != call.Name || !JsonElement.DeepEquals(_body.ToolUse.Input, call.Input))
                throw new InvalidOperationException("A resumed background body cannot change its completed input or implementation.");
            return (ToolResultBlock)Message.CloneBlock(_body.Result with { IteratorCleanupFailed = _body.IteratorCleanupFailed });
        }
    }
    internal void ResetAttempt() { lock (_gate) _body = null; }
    internal bool IteratorCleanupFailed { get { lock (_gate) return _body?.IteratorCleanupFailed == true; } }
}

internal sealed class BackgroundTaskManager
{
    private readonly object _gate = new();
    private readonly BackgroundTaskScheduler _scheduler;
    private readonly Func<BackgroundToolExecution, Task<ToolResultBlock>> _execute;
    private readonly Dictionary<string, LiveExecution> _executions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string PassId, string ToolUseId), string> _submissions = new();
    internal BackgroundTaskManager(BackgroundTasksConfig config, Func<BackgroundToolExecution, Task<ToolResultBlock>> execute, Action<BackgroundTaskSnapshot>? onTaskUpdated = null)
    {
        Guard.NotNull(config, nameof(config)); _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _scheduler = new(config.MaxConcurrency, config.Timeout, ExecuteAsync, task =>
        {
            if (task.IsTerminal) lock (_gate) _executions.Remove(task.InvocationStateId);
            onTaskUpdated?.Invoke(task);
        });
    }
    internal BackgroundTaskSnapshot Submit(ToolUseBlock toolUse, IReadOnlyDictionary<string, object?> invocationState, string passId, ITool tool,
        ToolSpec? admittedSpec = null, JsonSchema? admittedSchema = null)
    {
        Guard.NotNull(toolUse, nameof(toolUse)); Guard.NotNull(invocationState, nameof(invocationState)); Guard.NotNullOrWhiteSpace(passId, nameof(passId)); Guard.NotNull(tool, nameof(tool));
        var copy = (ToolUseBlock)Message.CloneBlock(toolUse);
        lock (_gate)
        {
            var key = (passId, copy.ToolUseId);
            if (_submissions.TryGetValue(key, out var existingId) && _scheduler.Get(existingId) is { } existing) return existing;
            var executionId = Guid.NewGuid().ToString();
            _executions.Add(executionId, new(copy, invocationState, tool, admittedSpec, admittedSchema));
            try
            {
                var task = _scheduler.Submit(copy.ToolUseId, copy.Name, executionId);
                _submissions[key] = task.TaskId;
                return task;
            }
            catch { _executions.Remove(executionId); throw; }
        }
    }
    internal BackgroundTaskSnapshot? Get(string taskId) => _scheduler.Get(taskId);
    internal IReadOnlyList<BackgroundTaskSnapshot> List() => _scheduler.List();
    internal Task StopAsync() => _scheduler.StopAsync();
    internal Task<BackgroundTaskSnapshot> WaitAsync(string taskId, CancellationToken cancellationToken = default) => _scheduler.WaitAsync(taskId, cancellationToken);
    internal async Task WaitForIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (timeout is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue)) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } delay) deadline.CancelAfter(delay);
        try { await _scheduler.WaitForIdleAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Waiting for background tasks was cancelled.", error, cancellationToken); }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested) { throw new TimeoutException("Timed out waiting for background tasks after " + timeout + ".", error); }
    }
    internal BackgroundTaskSnapshot Cancel(string taskId) => _scheduler.Cancel(taskId);
    internal BackgroundTaskSnapshot Resume(string taskId, IReadOnlyList<InterruptResponse> responses)
    {
        Guard.NotNull(responses, nameof(responses));
        var copy = responses.Select(value => value is null ? throw new ArgumentException("An interrupt response cannot be null.", nameof(responses)) : new InterruptResponse(value.InterruptId, value.Response)).ToArray();
        if (copy.Select(value => value.InterruptId).Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("Responses cannot repeat an interrupt identifier.", nameof(responses));
        return _scheduler.Resume(taskId, state =>
        {
            var known = new HashSet<string>(state.Interrupts.Select(value => value.Id), StringComparer.Ordinal);
            var answered = new HashSet<string>(state.Responses.Select(value => value.InterruptId), StringComparer.Ordinal);
            if (copy.Any(value => !known.Contains(value.InterruptId) || answered.Contains(value.InterruptId))) throw new ArgumentException("Responses must target outstanding task interrupts and cannot overwrite accepted responses.", nameof(responses));
            var combined = state.Responses.Concat(copy).ToArray();
            var next = new InterruptState(state.AssistantMessage, state.CompletedToolResults, state.Interrupts, combined, state.ToolExecutions) { AssistantMessageIsIndependent = state.AssistantMessageIsIndependent, SourceHistoryOwned = state.SourceHistoryOwned, Context = state.Context };
            return (next, combined.Length == state.Interrupts.Count);
        });
    }
    internal void Remove(IReadOnlyList<string> taskIds)
    {
        lock (_gate)
        {
            var removed = taskIds.Select(id => _scheduler.Get(id)).ToArray();
            _scheduler.Remove(taskIds);
            var ids = new HashSet<string>(taskIds, StringComparer.Ordinal);
            foreach (var key in _submissions.Where(pair => ids.Contains(pair.Value)).Select(pair => pair.Key).ToArray()) _submissions.Remove(key);
            foreach (var task in removed) if (task is not null) _executions.Remove(task.InvocationStateId);
        }
    }
    private async Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskExecution execution)
    {
        LiveExecution live;
        lock (_gate) live = _executions.TryGetValue(execution.InvocationStateId, out var value) ? value : throw new InvalidOperationException("Background task execution context is missing.");
        using var interrupts = new BackgroundTaskInterrupts(execution.TaskId, live.ToolUse, execution.State);
        try
        {
            var result = await _execute(new(live.Tool, (ToolUseBlock)Message.CloneBlock(live.ToolUse), live.InvocationState, execution, interrupts)
            { AdmittedSpec = live.AdmittedSpec, AdmittedSchema = live.AdmittedSchema }).ConfigureAwait(false);
            if (result.Status == ToolResultStatus.Success) return new(BackgroundTaskStatus.Completed, result);
            return new(BackgroundTaskStatus.Failed, result, new(BackgroundTaskFailureType.ToolError,
                result.Error?.Message ?? (!string.IsNullOrEmpty(result.Content) ? result.Content : "Background tool execution failed")));
        }
        catch (InterruptException interrupted) { return new(BackgroundTaskStatus.InputRequired, State: interrupts.Capture(interrupted.Interrupts)); }
    }
    private sealed record LiveExecution(ToolUseBlock ToolUse, IReadOnlyDictionary<string, object?> InvocationState, ITool Tool,
        ToolSpec? AdmittedSpec, JsonSchema? AdmittedSchema);
}
