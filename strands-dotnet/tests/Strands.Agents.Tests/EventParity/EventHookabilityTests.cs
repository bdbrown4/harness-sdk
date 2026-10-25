namespace Strands.Agents.Tests.EventParity;

public sealed class EventHookabilityTests
{
    [Fact]
    public async Task ModelUpdatesAreHookedOnceBeforeTheSameInstanceIsStreamedWithOriginalContextAndToken()
    {
        var state = new Dictionary<string, object?>(); var hooks = new HookRegistry(); var observed = new List<ModelStreamUpdateEvent>();
        using var caller = new CancellationTokenSource();
        hooks.Register<ModelStreamUpdateEvent>((value, token) => { Assert.Equal(caller.Token, token); Assert.Same(state, value.InvocationState); observed.Add(value); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("answer")), Hooks = hooks });
        var streamed = new List<ModelStreamUpdateEvent>();
        await foreach (var item in agent.StreamAsync("question", new() { InvocationState = state }, caller.Token))
            if (item is ModelStreamUpdateEvent update) { Assert.Contains(update, observed); Assert.Same(agent, update.Agent); streamed.Add(update); }
        Assert.Equal(5, observed.Count); Assert.Equal(observed, streamed); Assert.Empty(state);
    }

    [Fact]
    public async Task FinalResultIsHookedOnlyAfterTheFinalContinuationBoundary()
    {
        var state = new Dictionary<string, object?>(); var hooks = new HookRegistry(); var resultHooks = new List<AgentResultEvent>(); var cycles = 0;
        hooks.Register<AfterInvocationEvent>((value, _) => { if (++cycles == 1) value.Resume = new InvocationContinuation("next"); return default; });
        hooks.Register<AgentResultEvent>((value, _) => { Assert.Equal(2, cycles); Assert.Same(state, value.InvocationState); resultHooks.Add(value); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("first"), EventTestModel.Text("last")), Hooks = hooks });
        var streamed = new List<AgentResultEvent>();
        await foreach (var item in agent.StreamAsync("question", new() { InvocationState = state })) if (item is AgentResultEvent result) streamed.Add(result);
        Assert.Same(Assert.Single(streamed), Assert.Single(resultHooks)); Assert.Same(agent, resultHooks[0].Agent); Assert.Equal("last", resultHooks[0].Result.ToString());
    }

    [Fact]
    public async Task ACompletedBlockFollowsItsRawStopAndPrecedesTheCompletedMessageAndAfterHook()
    {
        var hooks = new HookRegistry(); var order = new List<string>(); var state = new Dictionary<string, object?>();
        using var caller = new CancellationTokenSource();
        hooks.Register<ModelStreamUpdateEvent>((value, _) => { if (value.Event is ModelContentBlockStopEvent) order.Add("raw-stop"); return default; });
        hooks.Register<ContentBlockEvent>((value, token) => { Assert.Equal(caller.Token, token); Assert.Equal("answer", Assert.IsType<TextBlock>(value.ContentBlock).Text); Assert.Same(state, value.InvocationState); order.Add("block"); return default; });
        hooks.Register<ModelMessageEvent>((value, token) => { Assert.Equal(caller.Token, token); Assert.Equal("answer", value.Message.Text); order.Add("message"); return default; });
        hooks.Register<AfterModelCallEvent>((_, _) => { order.Add("after"); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("answer")), Hooks = hooks });
        await agent.InvokeAsync("question", new() { InvocationState = state }, caller.Token);
        Assert.Equal(new[] { "raw-stop", "block", "message", "after" }, order);
    }
}
