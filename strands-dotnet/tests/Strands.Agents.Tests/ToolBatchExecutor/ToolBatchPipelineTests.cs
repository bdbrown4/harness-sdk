using Strands.Agents.Tasks;
using static Strands.Agents.Tests.ToolBatchExecutor.BatchTestSupport;

namespace Strands.Agents.Tests.ToolBatchExecutor;

public sealed class ToolBatchPipelineTests
{
    [Fact]
    public async Task RetryRemainsInsideOneAcceptedSchedulingTask()
    {
        var bodies = 0; var after = 0;
        var scheduler = new Scheduler(async (batch, _) => { var task = batch.ExecuteAsync(0); Assert.Same(task, batch.ExecuteAsync(0)); Assert.NotNull(await task); });
        await using var agent = Agent(Tool((_, _, _) => { bodies++; return new("ok"); }), scheduler, calls: 1);
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = ++after == 1; return default; });
        await agent.InvokeAsync("run"); Assert.Equal(2, bodies); Assert.Equal(2, after); Assert.Equal(1, scheduler.Calls);
        Assert.Single(agent.Messages[^1].Content.OfType<ToolResultBlock>());
    }

    [Fact]
    public async Task HookDenialDoesNotExecuteAnAcceptedBody()
    {
        var bodies = 0; var scheduler = new Scheduler(async (batch, _) => { Assert.NotNull(await batch.ExecuteAsync(0)); });
        await using var agent = Agent(Tool((_, _, _) => { bodies++; return new("unexpected"); }), scheduler, calls: 1);
        agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        await agent.InvokeAsync("run"); Assert.Equal(0, bodies); var result = Assert.Single(agent.Messages[^1].Content.OfType<ToolResultBlock>());
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Contains("denied", result.Content);
    }

    [Fact]
    public async Task SchemaValidationCannotBeBypassedByCustomScheduling()
    {
        var bodies = 0; var scheduler = new Scheduler(async (batch, _) => { await batch.ExecuteAsync(0); });
        var tool = new FunctionTool(new("work", "Required value", Parse("{\"type\":\"object\",\"required\":[\"value\"]}")), (_, _, _) => { bodies++; return new("unexpected"); });
        await using var agent = Agent(tool, scheduler, calls: 1); await agent.InvokeAsync("run"); Assert.Equal(0, bodies);
        Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages[^1].Content.OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task SingleDelegationStillEndsTheParentWithTheChildContent()
    {
        await using var child = new Agent(new() { Model = new Model(), Name = "child" });
        var scheduler = new Scheduler(async (batch, _) => { await batch.ExecuteAsync(0); });
        var parentModel = new Model(Call("delegate", "child", "{\"input\":\"hello\"}"));
        await using var parent = new Agent(new() { Model = parentModel, Tools = [child.AsTool(new AgentToolOptions { Name = "child", Delegate = true })], ToolBatchExecutor = scheduler });
        Assert.Equal("finished", (await parent.InvokeAsync("run")).ToString()); Assert.Equal(1, parentModel.Requests); Assert.Equal(1, scheduler.Calls);
    }

    [Fact]
    public async Task BackgroundDispatchAcknowledgementDoesNotMakeTheSchedulerOwnTheBody()
    {
        var entered = Signal(); var release = Signal(); var acknowledgement = Signal(); var exited = false; ToolResultBlock? visible = null;
        var scheduler = new Scheduler(async (batch, _) => { visible = await batch.ExecuteAsync(0); acknowledgement.TrySetResult(true); });
        var model = new Model(Call("background", input: "{\"_background_execution\":true}"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ToolBatchExecutor = scheduler,
            BackgroundTasks = new(),
            Tools = [Tool(async (input, _, _) => { Assert.False(input.TryGetProperty(BackgroundTaskPolicy.SelectionProperty, out _)); entered.TrySetResult(true); try { await release.Task; return "real body"; } finally { exited = true; } })]
        });
        var invocation = agent.InvokeAsync("run");
        try { await Ready(acknowledgement.Task); await Ready(entered.Task); Assert.Contains("Background task dispatched.", visible!.Content); Assert.False(exited); }
        finally { release.TrySetResult(true); await invocation; }
        Assert.True(exited); Assert.Equal(1, scheduler.Calls);
        Assert.Contains(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Content.Contains("real body"));
    }

    [Fact]
    public async Task ACustomExecutorOverridesTheEnumButStillUsesItsConfiguredParallelBound()
    {
        var order = new List<string>(); var scheduler = new Scheduler(async (batch, _) => { await batch.ExecuteAsync(1); await batch.ExecuteAsync(0); });
        await using var agent = new Agent(new() { Model = new Model(Call("a"), Call("b")), Tools = [Tool((_, context, _) => { order.Add(context.ToolUse.ToolUseId); return new("ok"); })], ToolExecution = ToolExecutionStrategy.Sequential, ToolBatchExecutor = scheduler, MaxParallelTools = 1, MaxTurns = 1 });
        await agent.InvokeAsync("run"); Assert.Equal(new[] { "b", "a" }, order);
    }

    [Fact]
    public async Task CommonAfterToolsEndTurnStillRunsAfterCustomScheduling()
    {
        var model = new Model(Call("work")); var scheduler = new Scheduler(async (batch, _) => { await batch.ExecuteAsync(0); });
        await using var agent = new Agent(new() { Model = model, Tools = [Tool((_, _, _) => new("body"))], ToolBatchExecutor = scheduler });
        agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("chosen")]; return default; });
        Assert.Equal("chosen", (await agent.InvokeAsync("run")).ToString()); Assert.Equal(1, model.Requests);
    }
}
