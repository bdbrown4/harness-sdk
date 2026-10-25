using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed partial class AgentTests
{
    [Theory]
    [InlineData(ToolExecutionStrategy.Sequential)]
    [InlineData(ToolExecutionStrategy.Concurrent)]
    public async Task PipelineFailurePreservesSuccessfulSiblingResults(ToolExecutionStrategy strategy)
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => value.ToolUse.ToolUseId == "bad"
            ? ValueTask.FromException(new InvalidOperationException("hook failed")) : ValueTask.CompletedTask);
        var calls = 0;
        var tool = new FunctionTool(new("save", "Save state", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult("saved"); });
        var agent = new Agent(new()
        {
            Model = new ScriptedModel(Call(("good", "save", "{}"), ("bad", "save", "{}"))),
            Tools = [tool],
            Hooks = hooks,
            ToolExecution = strategy
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("save"));

        Assert.Equal(1, calls);
        var results = agent.Messages[^1].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(ToolResultStatus.Success, results[0].Status);
        Assert.Equal("saved", results[0].Content);
        Assert.Equal(ToolResultStatus.Error, results[1].Status);
    }

    [Fact]
    public async Task RejectsOverlappingInvocationsAndReleasesGuardOnDisposal()
    {
        var model = new ScriptedModel(Text("ready"));
        var agent = new Agent(new AgentOptions { Model = model });
        await using (var stream = agent.StreamAsync("first").GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("second"));
        }
        Assert.Equal("ready", (await agent.InvokeAsync(null)).ToString());
    }

    [Fact]
    public async Task TurnLimitCompletesToolBatchBeforeStopping()
    {
        var model = new ScriptedModel(Call(("a", "missing", "{}")));
        var agent = new Agent(new AgentOptions { Model = model, MaxTurns = 1 });

        var result = await agent.InvokeAsync("go");

        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Equal(1, result.Turns);
        Assert.Single(model.Requests);
        Assert.Equal("a", Assert.IsType<ToolResultBlock>(agent.Messages[^1].Content[0]).ToolUseId);
    }

    [Fact]
    public async Task CallsToolAndContinuesWithCorrelatedResult()
    {
        var state = new Dictionary<string, object?> { ["request"] = 42 };
        var tool = new FunctionTool(new("echo", "Echo a value", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { value = new { type = "string" } },
            required = new[] { "value" }
        })), (input, context, _) =>
        {
            Assert.Same(state, context.InvocationState);
            Assert.Equal("call-1", context.ToolUse.ToolUseId);
            return ValueTask.FromResult(input.GetProperty("value").GetString()!);
        });
        var model = new ScriptedModel(Call(("call-1", "echo", "{\"value\":\"echoed\"}")), Text("done"));
        var agent = new Agent(new AgentOptions { Model = model, Tools = [tool] });

        var result = await agent.InvokeAsync("echo", new() { InvocationState = state });

        Assert.Equal("done", result.ToString());
        Assert.Equal(2, result.Turns);
        Assert.Same(state, result.InvocationState);
        Assert.Single(state);
        var reply = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]);
        Assert.Equal("call-1", reply.ToolUseId);
        Assert.Equal("echoed", reply.Content);
        Assert.Equal(ToolResultStatus.Success, reply.Status);
    }

    [Fact]
    public async Task InvokesModelAndCommitsConversation()
    {
        var model = new ScriptedModel(Text("hello", new Usage(3, 2)));
        var agent = new Agent(new AgentOptions { Model = model, SystemPrompt = "be useful" });

        var result = await agent.InvokeAsync("hi");

        Assert.Equal("hello", result.ToString());
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(new Usage(3, 2), result.Usage);
        Assert.Equal(1, result.Turns);
        Assert.Equal([Role.User, Role.Assistant], agent.Messages.Select(m => m.Role));
        Assert.Equal("hi", Assert.Single(model.Requests).Messages[0].Text);
        Assert.Equal("be useful", model.Requests[0].SystemPrompt);
    }


}
