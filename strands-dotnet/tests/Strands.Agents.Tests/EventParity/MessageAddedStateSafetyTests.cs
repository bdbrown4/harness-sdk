using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.EventParity;

public sealed class MessageAddedStateSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumingAnInterruptUsesTheNewBagWhileAnOutsideAbortIsUnbound(bool abort)
    {
        var firstState = MessageAddedStateHelpers.State(); var nextState = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen); var effects = 0;
        var tool = MessageAddedStateHelpers.Tool(context => { Assert.True(context.Interrupt("approval").GetBoolean()); effects++; return "approved"; });
        var model = new EventTestModel(MessageAddedStateHelpers.Call(), EventTestModel.Text("done"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });
        var paused = await agent.InvokeAsync("task", new() { InvocationState = firstState }); var question = Assert.Single(paused.Interrupts);
        MessageAddedStateHelpers.AssertState(seen, agent, firstState); seen.Clear();

        if (abort)
        {
            Assert.True(await agent.AbortInterruptsAsync("aborted"));
            var added = Assert.Single(seen); Assert.Empty(added.InvocationState); Assert.NotSame(firstState, added.InvocationState);
            Assert.Equal(0, effects); Assert.Single(model.Requests);
        }
        else
        {
            Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync(null, new() { InvocationState = nextState, InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] })).StopReason);
            Assert.Equal(1, effects); Assert.Equal(2, model.Requests.Count); Assert.Equal(2, seen.Count); MessageAddedStateHelpers.AssertState(seen, agent, nextState);
        }
        Assert.Null(agent.InterruptState);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task ExplicitCancellationTextUsesItsOriginalBagInEveryTimingProfile(ExplicitCancellationTiming timing)
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        hooks.Register<BeforeInvocationEvent>((value, _) => { value.Agent.Cancel("cancelled text"); return default; });
        await using var agent = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("unused")), Hooks = hooks, ExplicitCancellationTiming = timing });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("task", new() { InvocationState = state })).StopReason);
        Assert.Single(seen, value => value.Message.Text == "cancelled text"); MessageAddedStateHelpers.AssertState(seen, agent, state);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Fact]
    public async Task HardCallerCancellationKeepsItsCallbackTokenAndAcceptedInputState()
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        var entered = MessageAddedStateHelpers.Signal(); var joined = false; CancellationToken? thrownToken = null; using var caller = new CancellationTokenSource();
        hooks.Register<BeforeModelCallEvent>(async (_, token) =>
        { Assert.Equal(caller.Token, token); entered.TrySetResult(true); try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException error) { thrownToken = error.CancellationToken; throw; } finally { joined = true; } });
        var model = new EventTestModel(); await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var invocation = agent.InvokeAsync("task", new() { InvocationState = state }, caller.Token);
        try { await MessageAddedStateHelpers.Within(entered.Task); }
        finally { caller.Cancel(); await MessageAddedStateHelpers.Within(Record.ExceptionAsync(async () => await invocation)); }
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal(caller.Token, thrownToken); Assert.True(joined); Assert.Empty(model.Requests);
        Assert.Single(seen); MessageAddedStateHelpers.AssertState(seen, agent, state);
    }

    [Fact]
    public async Task DeferredCancellationFinishesAcceptedPeerToolsAndCorrelatesTheirCommit()
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen); var calls = 0;
        var tool = new FunctionTool(new("probe", "State probe", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, token) =>
        { token.ThrowIfCancellationRequested(); Assert.False(context.CancelSignal.IsCancellationRequested); if (++calls == 1) context.Agent.Cancel("after tools", afterCurrentTools: true); return new ValueTask<string>("accepted"); });
        ModelStreamEvent[] turn = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("one", "probe", JsonSerializer.SerializeToElement(new { }))), new ModelContentBlockStopEvent(0), new ModelContentBlockStartEvent(1, new ToolUseBlock("two", "probe", JsonSerializer.SerializeToElement(new { }))), new ModelContentBlockStopEvent(1), new ModelMessageStopEvent(StopReasons.ToolUse)];
        var model = new EventTestModel(turn); await using var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = [tool], ToolExecution = ToolExecutionStrategy.Sequential });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("task", new() { InvocationState = state })).StopReason);
        Assert.Equal(2, calls); Assert.Single(model.Requests); Assert.Equal(2, seen.SelectMany(value => value.Message.Content.OfType<ToolResultBlock>()).Count());
        MessageAddedStateHelpers.AssertState(seen, agent, state);
    }

    [Fact]
    public async Task EarlyStreamClosePhysicallyJoinsToolCleanupAndItsMessageHookKeepsTheBag()
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        var cleanupEntered = MessageAddedStateHelpers.Signal(); var release = MessageAddedStateHelpers.Signal(); var bodyCalls = 0;
        var tool = new StreamingFunctionTool(new("probe", "State probe", JsonSerializer.SerializeToElement(new { type = "object" })), Stream);
        var model = new EventTestModel(MessageAddedStateHelpers.Call()); await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });
        var iterator = agent.StreamAsync("task", new() { InvocationState = state }).GetAsyncEnumerator(); Task? disposal = null;
        try
        {
            var progress = false;
            while (await iterator.MoveNextAsync()) if (iterator.Current is ToolStreamUpdateEvent) { progress = true; break; }
            Assert.True(progress); disposal = iterator.DisposeAsync().AsTask(); await MessageAddedStateHelpers.Within(cleanupEntered.Task); Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(true); await MessageAddedStateHelpers.Within(disposal ?? iterator.DisposeAsync().AsTask()); }
        Assert.Equal(1, bodyCalls); Assert.Single(model.Requests); Assert.Contains(seen, value => value.Message.Content.OfType<ToolResultBlock>().Any());
        MessageAddedStateHelpers.AssertState(seen, agent, state);

        async IAsyncEnumerable<IToolStreamEvent> Stream(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken token)
        {
            bodyCalls++; Assert.Same(state, context.InvocationState);
            try { yield return new ToolStreamEvent("working"); await Task.Delay(Timeout.Infinite, token); }
            finally { cleanupEntered.TrySetResult(true); await release.Task.ConfigureAwait(false); }
            yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, "completed"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedAcceptedMessageHookRetainsStatePrimaryAndDirtyRetry(bool afterFails)
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        var primary = new IOException("message observer"); var save = new IOException("accepted save"); var after = new IOException("after cleanup"); var failedSave = false; var hookCalls = 0;
        var store = new IncomingInputStore(); store.SaveFailure = _ => { if (failedSave) return null; failedSave = true; return save; };
        hooks.Register<MessageAddedEvent>((_, _) => { hookCalls++; throw primary; });
        hooks.Register<AfterInvocationEvent>((value, _) => { Assert.Same(primary, value.Error); if (afterFails) throw after; return default; });
        var model = new EventTestModel(); await using var agent = new Agent(new() { Model = model, Hooks = hooks, SessionStore = store, SessionId = "state-dirty" });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("accepted", new() { InvocationState = state })));
        Assert.Equal(1, hookCalls); Assert.Single(seen); MessageAddedStateHelpers.AssertState(seen, agent, state);
        Assert.Equal(2, store.Attempts.Count); Assert.Single(store.Saves); Assert.Equal("accepted", Assert.Single(store.Snapshot!.Messages).Text); Assert.Empty(model.Requests);
        var errors = Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]); Assert.Contains(save, errors); Assert.Equal(afterFails ? 2 : 1, errors.Count);
        if (afterFails) Assert.Contains(after, errors);
    }

    [Fact]
    public async Task DirectToolsSnapshotsSessionRestoreAndWholeContextReplacementDoNotInventEvents()
    {
        var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen); var effects = 0;
        var store = new IncomingInputStore { Snapshot = new SessionSnapshot(1, [Message.User("restored")]) };
        await using var agent = new Agent(new() { Model = new EventTestModel(), Hooks = hooks, Tools = [MessageAddedStateHelpers.Tool(_ => { effects++; return "direct"; })], SessionStore = store, SessionId = "restore" });
        await agent.InitializeAsync(); Assert.Equal("restored", Assert.Single(agent.Messages).Text); Assert.Empty(seen);
        Assert.Equal("direct", (await agent.InvokeToolAsync(new("direct", "probe", JsonSerializer.SerializeToElement(new { })), MessageAddedStateHelpers.State())).Content);
        Assert.Equal(1, effects); Assert.Empty(seen);
        var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages });
        await agent.LoadSnapshotAsync(snapshot); Assert.Empty(seen);
        await agent.ReplaceContextAsync([Message.User("replacement")], CancellationToken.None); Assert.Empty(seen);
        Assert.Equal("replacement", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task ARepairOfRestoredTrafficUsesTheCurrentInvocationBag()
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        var pending = new Message(Role.Assistant, [new ToolUseBlock("old-call", "absent", JsonSerializer.SerializeToElement(new { }))]);
        var store = new IncomingInputStore { Snapshot = new SessionSnapshot(1, [Message.User("prior"), pending]) };
        var model = new EventTestModel(EventTestModel.Text("done"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, SessionStore = store, SessionId = "repair" });
        await agent.InvokeAsync(null, new() { InvocationState = state });
        Assert.Equal(2, seen.Count); Assert.Equal("old-call", Assert.Single(seen[0].Message.Content.OfType<ToolResultBlock>()).ToolUseId);
        MessageAddedStateHelpers.AssertState(seen, agent, state);
    }

    [Fact]
    public async Task AfterToolsEndTurnCorrelatesItsAdditionalAssistantWithoutAnotherModelCall()
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("ended")]; return default; });
        var model = new EventTestModel(MessageAddedStateHelpers.Call());
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = [MessageAddedStateHelpers.Tool(_ => "result")] });
        Assert.Equal("ended", (await agent.InvokeAsync("task", new() { InvocationState = state })).ToString());
        Assert.Single(model.Requests); Assert.Equal(4, seen.Count); MessageAddedStateHelpers.AssertState(seen, agent, state);
    }
}
