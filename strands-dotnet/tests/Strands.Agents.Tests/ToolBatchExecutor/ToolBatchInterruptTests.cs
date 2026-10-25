using static Strands.Agents.Tests.ToolBatchExecutor.BatchTestSupport;

namespace Strands.Agents.Tests.ToolBatchExecutor;

public sealed class ToolBatchInterruptTests
{
    [Fact]
    public async Task RealPauseReturnsNullAndPreservesUnadmittedCallsUntilResume()
    {
        var completed = 0; var approved = 0; var unadmitted = 0; var round = 0;
        var scheduler = new Scheduler(async (batch, _) =>
        {
            if (Interlocked.Increment(ref round) == 1) { Assert.NotNull(await batch.ExecuteAsync(0)); Assert.Null(await batch.ExecuteAsync(1)); }
            else { Assert.NotNull(await batch.ExecuteAsync(1)); Assert.NotNull(await batch.ExecuteAsync(2)); }
        });
        var model = new Model(Call("0"), Call("1"), Call("2"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ToolBatchExecutor = scheduler,
            Tools = [Tool((_, context, _) =>
            {
                if (context.ToolUse.ToolUseId == "0") completed++;
                else if (context.ToolUse.ToolUseId == "1") { Assert.True(context.Interrupt("approve").GetBoolean()); approved++; }
                else unadmitted++;
                return new("ok");
            })]
        });
        var paused = await agent.InvokeAsync("run"); Assert.Equal(StopReasons.Interrupt, paused.StopReason); var question = Assert.Single(paused.Interrupts);
        Assert.Equal(1, completed); Assert.Equal(0, approved); Assert.Equal(0, unadmitted);
        var pending = Assert.IsType<InterruptState>(agent.InterruptState); Assert.Single(pending.CompletedToolResults); Assert.Equal("0", pending.CompletedToolResults[0].ToolUseId);
        Assert.Equal(Role.Assistant, agent.Messages[^1].Role); Assert.Equal(1, model.Requests);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Parse("true"))] });
        Assert.Equal("finished", result.ToString()); Assert.Null(agent.InterruptState); Assert.Equal(1, completed); Assert.Equal(1, approved); Assert.Equal(1, unadmitted); Assert.Equal(2, model.Requests);
    }

    [Fact]
    public async Task CompletedResultsAreSeededEvenWhenResumeSchedulerDoesNotRescheduleThem()
    {
        var bodies = 0; var round = 0;
        var scheduler = new Scheduler(async (batch, _) => { if (Interlocked.Increment(ref round) == 1) await batch.ExecuteAsync(0); await batch.ExecuteAsync(1); });
        await using var agent = new Agent(new()
        {
            Model = new Model(Call("first"), Call("approval")),
            ToolBatchExecutor = scheduler,
            Tools = [Tool((_, context, _) => { if (context.ToolUse.ToolUseId == "approval") context.Interrupt("approve"); bodies++; return new("side effect"); })]
        });
        var paused = await agent.InvokeAsync("run"); Assert.Equal(1, bodies);
        await agent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, Parse("true"))] });
        Assert.Equal(2, bodies); var results = agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "first", "approval" }, results.Select(result => result.ToolUseId)); Assert.All(results, result => Assert.Equal(ToolResultStatus.Success, result.Status));
    }

    [Fact]
    public async Task NewStructuredDecoderRestoresAnAcceptedResponseWithoutReschedulingIt()
    {
        var round = 0; var accepted = 0;
        var scheduler = new Scheduler(async (batch, _) => { if (++round == 1) await batch.ExecuteAsync(0); await batch.ExecuteAsync(1); });
        var options = new StructuredOutputOptions<ToolBatchBoundaryTests.Answer> { Schema = Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"]}") };
        await using var agent = new Agent(new()
        {
            Model = new Model(Call("typed", StructuredOutputTool<ToolBatchBoundaryTests.Answer>.ToolName, "{\"value\":7}"), Call("approval")),
            ToolBatchExecutor = scheduler,
            Tools = [Tool((_, context, _) => { context.Interrupt("approve"); accepted++; return new("approved"); })]
        });
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("run", options));
        var question = Assert.Single(agent.InterruptState!.Interrupts); Assert.Single(agent.InterruptState.CompletedToolResults);
        var result = await agent.InvokeStructuredAsync(null, options, new() { InterruptResponses = [new(question.Id, Parse("true"))] });
        Assert.Equal(7, result.Output.Value); Assert.Equal("typed", result.Result.StructuredOutputToolUseId); Assert.Equal(1, accepted);
    }

    [Fact]
    public async Task SchedulerFailureCannotReplacePausedOrUnadmittedCallsWithSyntheticResults()
    {
        var expected = new InvalidOperationException("scheduler failed after pause"); var bodies = 0;
        var scheduler = new Scheduler(async (batch, _) => { Assert.Null(await batch.ExecuteAsync(0)); throw expected; });
        await using var agent = Agent(Tool((_, context, _) => { bodies++; context.Interrupt("approve"); return new("not yet"); }), scheduler, calls: 2);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("run")));
        Assert.Empty(agent.InterruptState!.CompletedToolResults); Assert.Single(agent.InterruptState.Interrupts); Assert.Equal(1, bodies);
        Assert.DoesNotContain(agent.Messages.SelectMany(value => value.Content), value => value is ToolResultBlock);
    }
}
