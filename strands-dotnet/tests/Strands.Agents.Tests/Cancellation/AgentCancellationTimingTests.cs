using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationTimingTests
{
    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task RetainedIdleRequestUsesTheSelectedInputCheckpoint(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Text("not accepted"));
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation, ExplicitCancellationTiming = timing });
        agent.Cancel("stop");
        var result = await agent.InvokeAsync("input");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal("stop", result.ToString());
        Assert.Equal(timing == ExplicitCancellationTiming.ModelOrTool ? 1 : 0, agent.Messages.Count(m => m.Role == Role.User));
        Assert.Equal(timing == ExplicitCancellationTiming.ModelOrTool ? 1 : 0, model.Requests.Count);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task BeforeInvocationRequestHasAnExplicitInputBoundary(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Text("not accepted"));
        var beforeModel = 0;
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeInvocationEvent>((_, _) => { agent.Cancel(); return default; });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { beforeModel++; return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input")).StopReason);
        var late = timing == ExplicitCancellationTiming.ModelOrTool ? 1 : 0;
        Assert.Equal(late, agent.Messages.Count(m => m.Role == Role.User));
        Assert.Equal(late, model.Requests.Count);
        Assert.Equal(late, beforeModel);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task AcceptedInputObserverRequestDoesNotLoseTheUserTurn(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Text("not accepted"));
        var beforeModel = 0;
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<MessageAddedEvent>((value, _) => { if (value.Message.Role == Role.User) agent.Cancel(); return default; });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { beforeModel++; return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("accepted input")).StopReason);
        Assert.Equal("accepted input", Assert.Single(agent.Messages, m => m.Role == Role.User).Text);
        var late = timing == ExplicitCancellationTiming.ModelOrTool ? 1 : 0;
        Assert.Equal(late, model.Requests.Count);
        Assert.Equal(late, beforeModel);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task BeforeModelRequestCanReachTheProviderBoundaryWithoutAcceptingChunks(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Text("not accepted"));
        var chunks = 0;
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        agent.Hooks.Register<ModelStreamUpdateEvent>((_, _) => { chunks++; return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("accepted input")).StopReason);
        Assert.Single(agent.Messages, m => m.Role == Role.User);
        Assert.Equal(timing == ExplicitCancellationTiming.NativeEarly ? 0 : 1, model.Requests.Count);
        Assert.Equal(0, chunks);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly, false)]
    [InlineData(ExplicitCancellationTiming.NativeEarly, true)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, true)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, true)]
    public async Task DenialPrecedenceIsSelectedWithoutStartingTheProvider(ExplicitCancellationTiming timing, bool atModel)
    {
        var model = new CancellationTestModel(Text("not accepted"));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        if (atModel) agent.Hooks.Register<BeforeModelCallEvent>((value, _) => { agent.Cancel(); value.CancelReason = "denied"; return default; });
        else agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { agent.Cancel(); value.CancelReason = "denied"; return default; });
        var result = await agent.InvokeAsync("input");
        Assert.Equal(timing == ExplicitCancellationTiming.NativeEarly ? StopReasons.Cancelled : StopReasons.EndTurn, result.StopReason);
        if (timing != ExplicitCancellationTiming.NativeEarly) Assert.Equal("denied", result.ToString());
        Assert.Empty(model.Requests);
        Assert.Equal(atModel ? 1 : 0, agent.Messages.Count(m => m.Role == Role.User));
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task AcceptedOrdinaryAnswerUsesTheSelectedPostModelCheckpoint(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Text("completed answer"));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<AfterModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        var result = await agent.InvokeAsync("input");
        Assert.Equal(timing == ExplicitCancellationTiming.NativeEarly ? StopReasons.Cancelled : StopReasons.EndTurn, result.StopReason);
        Assert.Equal("completed answer", result.ToString());
        Assert.Equal("completed answer", agent.Messages.Last().Text);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task FreshToolUseStopsAtTheSelectedBatchBoundary(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Call(("save", "save", "{}")));
        var effects = 0; var before = 0; var after = 0;
        var tool = Tool("save", (_, _, _) => { effects++; return new("not run"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], ExplicitCancellationTiming = timing });
        agent.Hooks.Register<AfterModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        agent.Hooks.Register<BeforeToolsEvent>((_, _) => { before++; return default; });
        agent.Hooks.Register<AfterToolsEvent>((_, _) => { after++; return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input")).StopReason);
        Assert.Equal(0, effects);
        Assert.Equal(timing == ExplicitCancellationTiming.ModelOrTool ? 1 : 0, before);
        Assert.Equal(timing == ExplicitCancellationTiming.ModelOrTool ? 1 : 0, after);
        Assert.Equal("save", Assert.Single(agent.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()).ToolUseId);
    }

    [Fact]
    public void UnknownTimingFailsBeforeConstructionSideEffects()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Model = new CancellationTestModel(), ExplicitCancellationTiming = (ExplicitCancellationTiming)123 }));
    }
}
