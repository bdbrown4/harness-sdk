using System.Text.Json;
using Strands.Agents.Tasks;

namespace Strands.Agents.Tests.Tasks;

public sealed class BackgroundTaskManagerTests
{
    [Fact]
    public async Task SubmissionDeduplicatesOnlyTheSamePassAndRetainsCallerStateByReference()
    {
        var started = new TaskCompletionSource<BackgroundToolExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ToolResultBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new BackgroundTaskManager(new(), execution => { started.TrySetResult(execution); return finish.Task; });
        var state = new Dictionary<string, object?> { ["caller"] = new object() };
        var tool = Tool();
        var first = manager.Submit(Call(), state, "pass-1", tool);
        var repeated = manager.Submit(Call(), state, "pass-1", tool);
        Assert.Equal(first.TaskId, repeated.TaskId);
        var execution = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(state, execution.InvocationState);
        Assert.Same(tool, execution.Tool);
        finish.TrySetResult(new("call", "done"));
        await manager.WaitAsync(first.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.TaskId, manager.Submit(Call(), state, "pass-1", tool).TaskId);
        var anotherPass = manager.Submit(Call(), state, "pass-2", tool);
        Assert.NotEqual(first.TaskId, anotherPass.TaskId);
        await manager.WaitAsync(anotherPass.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        manager.Remove(new[] { first.TaskId });
        Assert.NotEqual(first.TaskId, manager.Submit(Call(), state, "pass-1", tool).TaskId);
        await manager.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(state);
    }

    [Fact]
    public async Task ToolErrorsRetainRichContentWhileBodyExceptionsBecomeExecutionErrors()
    {
        var expected = new InvalidOperationException("body failed");
        var manager = new BackgroundTaskManager(new(), execution => execution.ToolUse.ToolUseId == "error"
            ? Task.FromResult(new ToolResultBlock("error", new ContentBlock[] { new JsonBlock(JsonSerializer.SerializeToElement(new { code = 42 })) }, ToolResultStatus.Error) { Error = expected })
            : Task.FromException<ToolResultBlock>(expected));
        var first = manager.Submit(Call("error"), new Dictionary<string, object?>(), "pass", Tool());
        var second = manager.Submit(Call("exception"), new Dictionary<string, object?>(), "pass", Tool());
        var failed = await manager.WaitAsync(first.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        var thrown = await manager.WaitAsync(second.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundTaskFailureType.ToolError, failed.Error!.Type);
        Assert.Equal("body failed", failed.Error.Message);
        Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single(failed.Result!.ContentBlocks)).Json.GetProperty("code").GetInt32());
        Assert.Equal(BackgroundTaskFailureType.ExecutionError, thrown.Error!.Type);
        Assert.Null(thrown.Result);
    }

    internal static ToolUseBlock Call(string id = "call") => new(id, "work", JsonSerializer.SerializeToElement(new { value = "original" }));
    internal static ITool Tool() => new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("unused"));

    [Fact]
    public async Task TaskLocalHumanInputReplaysWithStableAnswersWithoutTouchingAnotherTask()
    {
        var manager = new BackgroundTaskManager(new(), execution =>
        {
            var first = execution.Interrupts.Resolve("first", JsonSerializer.SerializeToElement("first?"), null, InterruptSource.Tool);
            var second = execution.Interrupts.Resolve("second", null, null, InterruptSource.Middleware);
            return Task.FromResult(new ToolResultBlock(execution.ToolUse.ToolUseId, first.GetString() + "/" + second.GetString()));
        });
        var firstTask = manager.Submit(Call("first-task"), new Dictionary<string, object?>(), "pass", Tool());
        var otherTask = manager.Submit(Call("other-task"), new Dictionary<string, object?>(), "pass", Tool());
        var firstQuestion = await manager.WaitAsync(firstTask.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        var otherQuestion = await manager.WaitAsync(otherTask.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        var question = Assert.Single(firstQuestion.InterruptState!.Interrupts);
        Assert.NotEqual(question.Id, Assert.Single(otherQuestion.InterruptState!.Interrupts).Id);
        Assert.Throws<ArgumentException>(() => manager.Resume(firstTask.TaskId, new[] { new InterruptResponse("foreign", JsonSerializer.SerializeToElement("bad")) }));
        Assert.Empty(manager.Get(firstTask.TaskId)!.InterruptState!.Responses);
        manager.Resume(firstTask.TaskId, new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("yes")) });
        var secondQuestion = await manager.WaitAsync(firstTask.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        var second = Assert.Single(secondQuestion.InterruptState!.Interrupts, value => value.Name == "second");
        Assert.Throws<ArgumentException>(() => manager.Resume(firstTask.TaskId, new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("overwrite")) }));
        manager.Resume(firstTask.TaskId, new[] { new InterruptResponse(second.Id, JsonSerializer.SerializeToElement("go")) });
        var completed = await manager.WaitAsync(firstTask.TaskId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("yes/go", completed.Result!.Content);
        Assert.Null(completed.InterruptState);
        Assert.Equal(BackgroundTaskStatus.InputRequired, manager.Get(otherTask.TaskId)!.Status);
        manager.Cancel(otherTask.TaskId);
        await manager.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ForeignInterruptBecomesExecutionFailureAndPreemptiveNullIsARealAnswer()
    {
        var manager = new BackgroundTaskManager(new(), execution =>
        {
            if (execution.ToolUse.ToolUseId == "foreign") throw new InterruptException(new Interrupt("unrelated", "question", "foreign"));
            var answer = execution.Interrupts.Resolve("question", null, JsonSerializer.SerializeToElement<object?>(null), InterruptSource.Hook);
            return Task.FromResult(new ToolResultBlock(execution.ToolUse.ToolUseId, answer.ValueKind.ToString()));
        });
        var foreign = manager.Submit(Call("foreign"), new Dictionary<string, object?>(), "pass", Tool());
        var answered = manager.Submit(Call("answered"), new Dictionary<string, object?>(), "pass", Tool());
        Assert.Equal(BackgroundTaskFailureType.ExecutionError, (await manager.WaitAsync(foreign.TaskId).WaitAsync(TimeSpan.FromSeconds(5))).Error!.Type);
        Assert.Equal("Null", (await manager.WaitAsync(answered.TaskId).WaitAsync(TimeSpan.FromSeconds(5))).Result!.Content);
    }

    [Fact]
    public async Task WaitingForIdleTimeoutLeavesTheWorkRunning()
    {
        var finish = new TaskCompletionSource<ToolResultBlock>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new BackgroundTaskManager(new(), _ => finish.Task);
        var task = manager.Submit(Call(), new Dictionary<string, object?>(), "pass", Tool());
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => manager.WaitForIdleAsync(TimeSpan.FromMilliseconds(50)));
            Assert.Equal(BackgroundTaskStatus.Working, manager.Get(task.TaskId)!.Status);
        }
        finally { finish.TrySetResult(new("call", "done")); await manager.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
