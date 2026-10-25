using System.Text.Json;
using Strands.Agents.Tasks;

namespace Strands.Agents.Tests.Tasks;

public sealed class BackgroundTaskSchedulerTests
{
    [Fact]
    public async Task SubmissionStartsTheRealExecutionAndRetainsItsResult()
    {
        var started = new TaskCompletionSource<BackgroundTaskExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<BackgroundTaskOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<BackgroundTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new BackgroundTaskScheduler(4, null, execution => { started.TrySetResult(execution); return finish.Task; },
            task => { if (task.Status == BackgroundTaskStatus.Completed) completed.TrySetResult(task); });
        var task = scheduler.Submit("call-1", "work");
        var execution = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(task.TaskId, execution.TaskId);
        Assert.Equal("call-1", execution.ToolUseId);
        finish.TrySetResult(new(BackgroundTaskStatus.Completed, new ToolResultBlock("call-1", "done")));
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("done", result.Result!.Content);
    }

    [Fact]
    public async Task CancelCommitsImmediatelyButKeepsItsPhysicalSlotUntilTheBodyExits()
    {
        var started = Signal<BackgroundTaskExecution>();
        var finish = Signal<BackgroundTaskOutcome>();
        var next = Signal<BackgroundTaskExecution>();
        var cancellation = Signal<bool>();
        var scheduler = new BackgroundTaskScheduler(1, null, execution =>
        {
            if (execution.ToolUseId != "first") { next.TrySetResult(execution); return Task.FromResult(Done(execution)); }
            started.TrySetResult(execution);
            execution.CancellationToken.Register(() => cancellation.TrySetResult(true));
            return finish.Task;
        });
        var first = scheduler.Submit("first", "work");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scheduler.Submit("second", "work");
        Assert.Equal(BackgroundTaskStatus.Queued, second.Status);
        Assert.Equal(BackgroundTaskStatus.Cancelled, scheduler.Cancel(first.TaskId).Status);
        await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskStatus.Cancelled, (await scheduler.WaitAsync(first.TaskId)).Status);
        Assert.False(next.Task.IsCompleted);
        Assert.False(scheduler.WaitForIdleAsync().IsCompleted);
        finish.TrySetResult(new(BackgroundTaskStatus.Completed, new ToolResultBlock("first", "too late")));
        await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskStatus.Cancelled, scheduler.Get(first.TaskId)!.Status);
        Assert.Null(scheduler.Get(first.TaskId)!.Result);
        Assert.Equal(BackgroundTaskStatus.Completed, scheduler.Get(second.TaskId)!.Status);
    }

    [Fact]
    public async Task DeadlineCommitsFailureWithoutReleasingAnUncooperativeBody()
    {
        var finish = Signal<BackgroundTaskOutcome>();
        var started = Signal<BackgroundTaskExecution>();
        var next = Signal<bool>();
        var scheduler = new BackgroundTaskScheduler(1, TimeSpan.FromMilliseconds(50), execution =>
        {
            if (execution.ToolUseId != "first") { next.TrySetResult(true); return Task.FromResult(Done(execution)); }
            started.TrySetResult(execution); return finish.Task;
        });
        var first = scheduler.Submit("first", "work");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Submit("second", "work");
        var failed = await scheduler.WaitAsync(first.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskFailureType.Timeout, failed.Error!.Type);
        Assert.False(next.Task.IsCompleted);
        finish.TrySetResult(new(BackgroundTaskStatus.Completed, new ToolResultBlock("first", "late")));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(next.Task.IsCompleted);
        Assert.Equal(BackgroundTaskStatus.Failed, scheduler.Get(first.TaskId)!.Status);
    }

    [Fact]
    public async Task CancelledQueuedTaskNeverExecutesAndWaitCancellationDoesNotCancelWork()
    {
        var release = Signal<BackgroundTaskOutcome>();
        var started = Signal<bool>();
        var count = 0;
        var scheduler = new BackgroundTaskScheduler(1, null, _ => { Interlocked.Increment(ref count); started.TrySetResult(true); return release.Task; });
        var first = scheduler.Submit("first", "work");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = scheduler.Submit("second", "work");
        scheduler.Cancel(queued.TaskId);
        using var cancellation = new CancellationTokenSource();
        var wait = scheduler.WaitAsync(first.TaskId, cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(BackgroundTaskStatus.Working, scheduler.Get(first.TaskId)!.Status);
        release.TrySetResult(new(BackgroundTaskStatus.Completed, new ToolResultBlock("first", "done")));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RemoveValidatesTheWholeBatchBeforeDeletingAnything()
    {
        var pending = Signal<BackgroundTaskOutcome>();
        var scheduler = new BackgroundTaskScheduler(2, null, execution => execution.ToolUseId == "first" ? Task.FromResult(Done(execution)) : pending.Task);
        var first = scheduler.Submit("first", "work");
        await scheduler.WaitAsync(first.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        var second = scheduler.Submit("second", "work");
        Assert.Throws<InvalidOperationException>(() => scheduler.Remove(new[] { first.TaskId, second.TaskId }));
        Assert.NotNull(scheduler.Get(first.TaskId));
        Assert.Throws<KeyNotFoundException>(() => scheduler.Remove(new[] { first.TaskId, "missing" }));
        Assert.NotNull(scheduler.Get(first.TaskId));
        scheduler.Cancel(second.TaskId);
        scheduler.Remove(new[] { first.TaskId, second.TaskId, first.TaskId });
        Assert.Empty(scheduler.List());
        pending.TrySetResult(new(BackgroundTaskStatus.Completed, new ToolResultBlock("second", "late")));
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static BackgroundTaskOutcome Done(BackgroundTaskExecution execution) => new(BackgroundTaskStatus.Completed, new ToolResultBlock(execution.ToolUseId, "done"));

    [Fact]
    public async Task InputRequiredIsIdleAndPartialResponsesDoNotExecuteUntilReady()
    {
        var executions = 0;
        var input = JsonSerializer.SerializeToElement(new { });
        var request = new ToolUseBlock("call", "work", input);
        var state = new InterruptState(new Message(Role.Assistant, new ContentBlock[] { request }), Array.Empty<ToolResultBlock>(),
            new[] { new Interrupt("question", "confirm", "call") }, Array.Empty<InterruptResponse>());
        var scheduler = new BackgroundTaskScheduler(1, null, execution =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(execution.State is null ? new BackgroundTaskOutcome(BackgroundTaskStatus.InputRequired, State: state) : Done(execution));
        });
        var task = scheduler.Submit("call", "work");
        Assert.Equal(BackgroundTaskStatus.InputRequired, (await scheduler.WaitAsync(task.TaskId).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Resume(task.TaskId, current => (current, false));
        Assert.Equal(1, executions);
        scheduler.Resume(task.TaskId, current => (new(current.AssistantMessage, current.CompletedToolResults, current.Interrupts,
            new[] { new InterruptResponse("question", JsonSerializer.SerializeToElement(true)) }), true));
        Assert.Equal(BackgroundTaskStatus.Completed, (await scheduler.WaitAsync(task.TaskId).WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, executions);
        Assert.Throws<InvalidOperationException>(() => scheduler.Resume(task.TaskId, current => (current, true)));
    }

    [Fact]
    public async Task InvalidOutcomeFailsRatherThanLeavingAWorkingTaskForever()
    {
        var scheduler = new BackgroundTaskScheduler(1, null, _ => Task.FromResult(new BackgroundTaskOutcome(BackgroundTaskStatus.Completed)));
        var task = scheduler.Submit("call", "work");
        var result = await scheduler.WaitAsync(task.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskStatus.Failed, result.Status);
        Assert.Equal(BackgroundTaskFailureType.ExecutionError, result.Error!.Type);
    }

    [Fact]
    public async Task ResultSnapshotsAreDetachedAndObserverFailuresDoNotChangeExecution()
    {
        var delivered = Signal<bool>();
        var statuses = new List<BackgroundTaskStatus>();
        using var document = JsonDocument.Parse("{\"answer\":[42]}");
        var scheduler = new BackgroundTaskScheduler(1, null, execution => Task.FromResult(new BackgroundTaskOutcome(BackgroundTaskStatus.Completed,
            new ToolResultBlock(execution.ToolUseId, new ContentBlock[] { new JsonBlock(document.RootElement) }))),
            task => { lock (statuses) statuses.Add(task.Status); if (task.IsTerminal) delivered.TrySetResult(true); throw new InvalidOperationException("observer"); });
        var task = scheduler.Submit("call", "work");
        await scheduler.WaitAsync(task.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        document.Dispose();
        Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single(scheduler.Get(task.TaskId)!.Result!.ContentBlocks!)).Json.GetProperty("answer")[0].GetInt32());
        lock (statuses) Assert.Equal(new[] { BackgroundTaskStatus.Queued, BackgroundTaskStatus.Working, BackgroundTaskStatus.Completed }, statuses);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 2147483648)]
    public void InvalidPhysicalLimitsFailBeforeExecution(int concurrency, double timeoutMs)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new BackgroundTaskScheduler(concurrency, TimeSpan.FromMilliseconds(timeoutMs), execution => Task.FromResult(Done(execution))));
}
