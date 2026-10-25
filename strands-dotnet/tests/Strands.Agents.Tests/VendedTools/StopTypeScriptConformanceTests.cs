using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class StopTypeScriptConformanceTests
{
    [Fact]
    public async Task MultipleFactoriesShareOneOwnedLifecycleInstallation()
    {
        await using var agent = Agent();
        var observed = 0;
        agent.Hooks.Register<AfterToolsEvent>((_, _) => { observed++; return default; });
        var baseline = RegistrationCount(agent);
        var state = State();
        var first = Ts("first"); var second = Ts("second");
        await first.InvokeAsync(Input("first"), Context(agent, state, "one", "first"));
        await second.InvokeAsync(Input("second"), Context(agent, state, "two", "second"));
        Assert.Equal(baseline + 4, RegistrationCount(agent));
        var after = After(agent, state, "two");
        await agent.Hooks.InvokeAsync(after);
        Assert.Equal("second", EndText(after));
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task ConcurrentFirstUsesInstallOneOwnedLifecycleSet()
    {
        await using var agent = Agent();
        var baseline = RegistrationCount(agent); var tool = Ts();
        var operations = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
            await tool.InvokeAsync(Input("request-" + index), Context(agent, State(), "call-" + index)))).ToArray();
        await Wait(Task.WhenAll(operations));
        var results = await Task.WhenAll(operations);
        Assert.Equal(16, operations.Length);
        Assert.All(results, result => Assert.StartsWith("request-", result));
        Assert.Equal(baseline + 4, RegistrationCount(agent));
    }

    [Fact]
    public async Task OneFactoryKeepsTwoAgentOwnersIndependent()
    {
        var tool = Ts(); var state = State();
        var enteredA = Gate(); var enteredB = Gate(); var release = Gate(); var effects = 0;
        var peerA = Tool("peer", async (_, _, _) => { enteredA.TrySetResult(true); await release.Task; Interlocked.Increment(ref effects); return "peer-a"; });
        var peerB = Tool("peer", async (_, _, _) => { enteredB.TrySetResult(true); await release.Task; Interlocked.Increment(ref effects); return "peer-b"; });
        var shared = new HookRegistry();
        var modelA = new Model(Call(("stop-a", "stop", "{\"message\":\"agent-a\"}"), ("peer-a", "peer", "{}")));
        var modelB = new Model(Call(("stop-b", "stop", "{\"message\":\"agent-b\"}"), ("peer-b", "peer", "{}")));
        await using var a = new Agent(new() { Model = modelA, Tools = [tool, peerA], Hooks = shared, ToolExecution = ToolExecutionStrategy.Sequential });
        await using var b = new Agent(new() { Model = modelB, Tools = [tool, peerB], Hooks = shared, ToolExecution = ToolExecutionStrategy.Sequential });
        var first = a.InvokeAsync("go", new() { InvocationState = state });
        var second = b.InvokeAsync("go", new() { InvocationState = state });
        try { await Wait(Task.WhenAll(enteredA.Task, enteredB.Task)); Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(true); await Wait(Task.WhenAll(first, second)); }
        Assert.Equal("agent-a", (await first).Message!.Text);
        Assert.Equal("agent-b", (await second).Message!.Text);
        Assert.Equal(2, effects); Assert.Equal(1, modelA.Calls); Assert.Equal(1, modelB.Calls);
    }

    [Fact]
    public async Task ForeignOwnerEventsCannotConsumeTheOwnerReceipt()
    {
        await using var owner = Agent(); await using var other = Agent();
        var state = State(); var tool = Ts();
        await tool.InvokeAsync(Input("owner only"), Context(owner, state, "same-id"));
        var foreign = After(other, state, "same-id");
        await owner.Hooks.InvokeAsync(foreign);
        Assert.Null(foreign.EndTurn);
        var actual = After(owner, state, "same-id"); await owner.Hooks.InvokeAsync(actual);
        Assert.Equal("owner only", EndText(actual));
    }

    [Fact]
    public async Task AParallelBatchUsesTheLastAcceptedStopRequest()
    {
        var firstAccepted = Gate(); var releaseLast = Gate(); var peerEffects = 0;
        var first = Ts("first"); var last = Ts("last");
        var firstWrapper = new FunctionTool(first.Spec, async (input, context, token) => { var result = await first.InvokeAsync(input, context, token); firstAccepted.TrySetResult(true); return result; });
        var lastWrapper = new FunctionTool(last.Spec, async (input, context, token) => { await firstAccepted.Task; await releaseLast.Task; return await last.InvokeAsync(input, context, token); });
        var peer = Tool("peer", (_, _, _) => { peerEffects++; return new("peer completed"); });
        var model = new Model(Call(("first-use", "first", "{\"message\":\"earlier\"}"), ("last-use", "last", "{\"message\":\"later\"}"), ("peer-use", "peer", "{}")));
        await using var agent = new Agent(new() { Model = model, Tools = [firstWrapper, lastWrapper, peer], ToolExecution = ToolExecutionStrategy.Concurrent });
        var operation = agent.InvokeAsync("go");
        try { await Wait(firstAccepted.Task); Assert.False(operation.IsCompleted); }
        finally { releaseLast.TrySetResult(true); await Wait(operation); }
        Assert.Equal("later", (await operation).Message!.Text);
        Assert.Equal(1, model.Calls); Assert.Equal(1, peerEffects);
        Assert.Equal(3, agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Count());
    }

    [Fact]
    public async Task ASeparateRawStateCannotOverwriteTheCurrentBatchRequest()
    {
        var tool = Ts(); var current = State(); var foreign = State(); var entered = Gate(); var release = Gate();
        var peer = Tool("peer", async (_, _, _) => { entered.TrySetResult(true); await release.Task; return "peer"; });
        var model = new Model(Call(("real-id", "stop", "{\"message\":\"current\"}"), ("peer-id", "peer", "{}")), Text("normal next"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool, peer], ToolExecution = ToolExecutionStrategy.Sequential });
        var operation = agent.InvokeAsync("go", new() { InvocationState = current });
        try { await Wait(entered.Task); await tool.InvokeAsync(Input("foreign"), Context(agent, foreign, "foreign-id")); Assert.False(operation.IsCompleted); }
        finally { release.TrySetResult(true); await Wait(operation); }
        Assert.Equal("current", (await operation).Message!.Text);
        var stale = After(agent, foreign, "foreign-id"); await agent.Hooks.InvokeAsync(stale); Assert.Null(stale.EndTurn);
        Assert.Equal("normal next", (await agent.InvokeAsync(null, new() { InvocationState = current })).Message!.Text);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task ShutdownRemovesOnlyOwnedStopHooks()
    {
        await using var agent = Agent(); var applicationCalls = 0;
        agent.Hooks.Register<ShutdownEvent>((_, _) => { applicationCalls++; return default; });
        var baseline = RegistrationCount(agent);
        await Ts().InvokeAsync(Input("done"), Context(agent, State(), "stop-id"));
        Assert.Equal(baseline + 4, RegistrationCount(agent));
        await agent.ShutdownAsync();
        Assert.Equal(baseline, RegistrationCount(agent));
        await agent.ShutdownAsync(); Assert.Equal(1, applicationCalls);
    }

    [Fact]
    public async Task FirstUseAfterShutdownRejectsWithoutPartialHooks()
    {
        await using var agent = Agent(); await agent.ShutdownAsync();
        var baseline = RegistrationCount(agent); var state = State();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await Ts().InvokeAsync(Input("too late"), Context(agent, state, "late-id")));
        Assert.Equal(baseline, RegistrationCount(agent));
        var after = After(agent, state, "late-id"); await agent.Hooks.InvokeAsync(after); Assert.Null(after.EndTurn);
    }

    [Fact]
    public void RetainedFactoryAndApplicationRegistryDoNotRetainTheAgent()
    {
        var tool = Ts(); var registry = new HookRegistry(); var owner = InstallAndRelease(tool, registry);
        for (var attempt = 0; attempt < 8 && IsAlive(owner); attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(IsAlive(owner));
        GC.KeepAlive(tool); GC.KeepAlive(registry);
    }

    [Fact]
    public async Task DifferentStatesWithRepeatedIdsRemainSeparate()
    {
        await using var agent = Agent(); var tool = Ts(); var a = State(); var b = State();
        await tool.InvokeAsync(Input("state-a"), Context(agent, a, "repeated"));
        await tool.InvokeAsync(Input("state-b"), Context(agent, b, "repeated"));
        var first = After(agent, a, "repeated"); var second = After(agent, b, "repeated");
        await agent.Hooks.InvokeAsync(first); await agent.Hooks.InvokeAsync(second);
        Assert.Equal("state-a", EndText(first)); Assert.Equal("state-b", EndText(second));
    }

    [Fact]
    public async Task AnUnrelatedBatchCannotConsumeAPendingRequest()
    {
        await using var agent = Agent(); var state = State();
        await Ts().InvokeAsync(Input("correlated"), Context(agent, state, "actual-id"));
        var unrelated = After(agent, state, "other-id"); await agent.Hooks.InvokeAsync(unrelated); Assert.Null(unrelated.EndTurn);
        var actual = After(agent, state, "actual-id"); await agent.Hooks.InvokeAsync(actual); Assert.Equal("correlated", EndText(actual));
        var again = After(agent, state, "actual-id"); await agent.Hooks.InvokeAsync(again); Assert.Null(again.EndTurn);
    }

    [Fact]
    public async Task UserChangedResultStatusDoesNotInventASuccessOnlyRestriction()
    {
        var model = new Model(Call(("stop-id", "stop", "{\"message\":\"selected finish\"}")));
        await using var agent = new Agent(new() { Model = model, Tools = [Ts()] });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Result = new(value.ToolUse.ToolUseId, "user result", ToolResultStatus.Error); return default; });
        Assert.Equal("selected finish", (await agent.InvokeAsync("go")).Message!.Text);
        var result = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Equal("user result", result.Content); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task BeforeInvocationExpiresUnsettledRawRequests()
    {
        var model = new Model(Call(("reused-id", "work", "{}")), Text("normal result"));
        var tool = Ts(); var state = State();
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("work", (_, _, _) => new("accepted"))] });
        await tool.InvokeAsync(Input("stale raw"), Context(agent, state, "reused-id"));
        var result = await agent.InvokeAsync("go", new() { InvocationState = state });
        Assert.Equal("normal result", result.Message!.Text); Assert.Equal(2, model.Calls);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant && message.Text == "stale raw");
    }

    [Fact]
    public async Task AfterInvocationExpiresAbandonedRequests()
    {
        await using var agent = Agent(); var state = State(); var tool = Ts();
        await tool.InvokeAsync(Input("abandoned"), Context(agent, state, "abandoned-id"));
        var independent = new IOException("independent invocation failure");
        await agent.Hooks.InvokeAsync(new AfterInvocationEvent(agent, null, independent, state));
        var after = After(agent, state, "abandoned-id"); await agent.Hooks.InvokeAsync(after); Assert.Null(after.EndTurn);
        await tool.InvokeAsync(Input("new request"), Context(agent, state, "new-id"));
        var current = After(agent, state, "new-id"); await agent.Hooks.InvokeAsync(current); Assert.Equal("new request", EndText(current));
    }

    [Fact]
    public async Task IndependentAfterToolFailureRemainsPrimaryAndDoesNotLeak()
    {
        var primary = new IOException("independent after-tool failure"); var fail = true; var effects = 0;
        var stop = Ts();
        var wrapper = new FunctionTool(stop.Spec, async (input, context, token) => { var result = await stop.InvokeAsync(input, context, token); effects++; return result; });
        var model = new Model(Call(("stop-id", "stop", "{}")), Text("normal after failure"));
        await using var agent = new Agent(new() { Model = model, Tools = [wrapper] });
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => { if (fail) { fail = false; throw primary; } return default; });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("go")));
        Assert.Equal("normal after failure", (await agent.InvokeAsync(null)).Message!.Text);
        Assert.Equal(1, effects); Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task EarlyStreamDisposalPhysicallyJoinsTheBatchAndExpiresTheReceipt()
    {
        var entered = Gate(); var release = Gate(); var effects = 0; var state = State();
        var peer = Tool("peer", async (_, _, _) => { entered.TrySetResult(true); await release.Task; effects++; return "settled peer"; });
        var model = new Model(Call(("stop-id", "stop", "{}"), ("peer-id", "peer", "{}")), Text("normal after dispose"));
        await using var agent = new Agent(new() { Model = model, Tools = [Ts(), peer], ToolExecution = ToolExecutionStrategy.Concurrent });
        var iterator = agent.StreamAsync("go", new() { InvocationState = state }).GetAsyncEnumerator();
        Task? disposal = null;
        try
        {
            while (await iterator.MoveNextAsync()) if (iterator.Current is ToolResultEvent { Result.ToolUseId: "stop-id" }) break;
            await Wait(entered.Task);
            disposal = iterator.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(true); disposal ??= iterator.DisposeAsync().AsTask(); await Wait(disposal); }
        Assert.Equal(1, effects);
        Assert.Equal("normal after dispose", (await agent.InvokeAsync(null, new() { InvocationState = state })).Message!.Text);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task OriginalOperationCancellationWinsBeforeInstallation()
    {
        await using var agent = Agent(); var baseline = RegistrationCount(agent); var state = State();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Ts().InvokeAsync(Input("ignored"), Context(agent, state, "call"), cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken); Assert.Equal(baseline, RegistrationCount(agent));
        var after = After(agent, state, "call"); await agent.Hooks.InvokeAsync(after); Assert.Null(after.EndTurn);
    }

    [Fact]
    public async Task CapturedContextCancellationWinsBeforeInstallation()
    {
        await using var agent = Agent(); var baseline = RegistrationCount(agent); var state = State();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var context = Context(agent, state, "call") with { ExecutionCancelSignal = cancelled.Token };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Ts().InvokeAsync(Input("ignored"), context));
        Assert.Equal(cancelled.Token, error.CancellationToken); Assert.Equal(baseline, RegistrationCount(agent));
        Assert.Equal("normal", (await agent.InvokeAsync("later")).Message!.Text);
    }

    [Fact]
    public async Task CallerCancellationStillWinsAfterAnAcceptedStopAndSiblingJoin()
    {
        var entered = Gate(); var release = Gate(); var effects = 0;
        var peer = Tool("peer", async (_, _, _) => { entered.TrySetResult(true); await release.Task; effects++; return "accepted peer"; });
        var model = new Model(Call(("stop-id", "stop", "{}"), ("peer-id", "peer", "{}")));
        await using var agent = new Agent(new() { Model = model, Tools = [Ts(), peer], ToolExecution = ToolExecutionStrategy.Sequential });
        using var caller = new CancellationTokenSource(); var operation = agent.InvokeAsync("go", cancellationToken: caller.Token);
        try { await Wait(entered.Task); caller.Cancel(); Assert.False(operation.IsCompleted); }
        finally { release.TrySetResult(true); await operation.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
        var result = await operation;
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.True(caller.IsCancellationRequested);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant && message.Text == Stop.DefaultMessage);
        Assert.Equal(1, effects); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task StatefulModelsKeepTheExistingEndTurnAcknowledgementGuard()
    {
        var events = Call(("stop-id", "stop", "{}"));
        events[events.Length - 1] = new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = Json("\"provider-receipt\"") } };
        var model = new StatefulModel(events);
        await using var agent = new Agent(new() { Model = model, Tools = [Ts()] });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("go"));
        Assert.Contains("server-side tool acknowledgement", error.Message);
        Assert.Equal("provider-receipt", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant && message.Text == Stop.DefaultMessage);
        Assert.Equal(Stop.DefaultMessage, Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task TypeScriptUtf16CapRejectsAstralOverflowWithoutInstallingHooks()
    {
        await using var agent = Agent(); var baseline = RegistrationCount(agent); var tool = Ts(maximum: 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await tool.InvokeAsync(Input("😀"), Context(agent, State(), "astral")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tool.InvokeAsync(Json("{\"message\":42}"), Context(agent, State(), "number")));
        Assert.Equal(baseline, RegistrationCount(agent));
    }

    [Fact]
    public async Task TypeScriptNullEmptyAbsentFallbackIsNotRecapped()
    {
        await using var agent = Agent(); var tool = Ts(maximum: 1);
        foreach (var input in new[] { "{}", "{\"message\":null}", "{\"message\":\"\"}" })
        {
            var state = State(); Assert.Equal(Stop.DefaultMessage, await tool.InvokeAsync(Json(input), Context(agent, state, "fallback")));
            var after = After(agent, state, "fallback"); await agent.Hooks.InvokeAsync(after); Assert.Equal(Stop.DefaultMessage, EndText(after));
        }
        var whitespace = State(); Assert.Equal(" ", await tool.InvokeAsync(Input(" "), Context(agent, whitespace, "space")));
        var literal = After(agent, whitespace, "space"); await agent.Hooks.InvokeAsync(literal); Assert.Equal(" ", EndText(literal));
    }

    [Fact]
    public async Task TypeScriptInputFailuresPublishNeitherHooksNorRequests()
    {
        await using var agent = Agent(); var baseline = RegistrationCount(agent); var tool = Ts(maximum: 3); var state = State();
        foreach (var input in new[] { "null", "[]", "42", "\"text\"", "{\"message\":true}", "{\"message\":[]}", "{\"message\":{}}", "{\"message\":\"long\"}" })
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await tool.InvokeAsync(Json(input), Context(agent, state, "invalid")));
        Assert.Equal(baseline, RegistrationCount(agent));
        var after = After(agent, state, "invalid"); await agent.Hooks.InvokeAsync(after); Assert.Null(after.EndTurn);
        Assert.Equal("ok", await tool.InvokeAsync(Input("ok"), Context(agent, state, "valid")));
    }

    [Fact]
    public async Task TypeScriptUnicodeAtTheCapKeepsTheOriginalMessage()
    {
        await using var agent = Agent(); var tool = Ts(maximum: 2);
        foreach (var message in new[] { "😀", "e\u0301", "\né" })
        {
            var state = State(); Assert.Equal(message, await tool.InvokeAsync(Input(message), Context(agent, state, "unicode")));
            var after = After(agent, state, "unicode"); await agent.Hooks.InvokeAsync(after); Assert.Equal(message, EndText(after)); Assert.Empty(state);
        }
    }

    private static Agent Agent() => new(new() { Model = new Model(Text("normal")) });
    private static IReadOnlyDictionary<string, object?> State() => new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    private static ITool Ts(string name = "stop", int maximum = 4096) => Stop.Create(new() { Name = name, MaximumMessageLength = maximum, Behavior = StopBehavior.TypeScriptEndTurn });
    private static ToolContext Context(Agent agent, IReadOnlyDictionary<string, object?> state, string id, string name = "stop") => new(agent, new(id, name, Json("{}")), state);
    private static AfterToolsEvent After(Agent agent, IReadOnlyDictionary<string, object?> state, string id) => new(agent, [new(id, "accepted")], state);
    private static string EndText(AfterToolsEvent value) => Assert.IsType<TextBlock>(Assert.Single(value.EndTurn!)).Text;
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static JsonElement Input(string message) => JsonSerializer.SerializeToElement(new { message });
    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> body) => new(new(name, name, Json("{\"type\":\"object\"}")), body);
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static int RegistrationCount(Agent agent)
    {
        var hooks = agent.Hooks;
        var gate = typeof(HookRegistry).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hooks)!;
        lock (gate) return ((ICollection)typeof(HookRegistry).GetField("_registrations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(hooks)!).Count;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Agent> InstallAndRelease(ITool tool, HookRegistry registry)
    {
        var agent = new Agent(new() { Model = new Model(Text("unused")), Hooks = registry });
        tool.InvokeAsync(Input("done"), Context(agent, State(), "collected-owner")).AsTask().GetAwaiter().GetResult();
        return new(agent);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<Agent> owner) => owner.TryGetTarget(out _);
    private static ModelStreamEvent[] Text(string value) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static ModelStreamEvent[] Call(params (string Id, string Name, string Input)[] calls)
    {
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        for (var index = 0; index < calls.Length; index++) { var call = calls[index]; events.Add(new ModelContentBlockStartEvent(index, new(call.Id, call.Name, Json("{}")))); events.Add(new ModelContentBlockDeltaEvent(index, ToolInput: call.Input)); events.Add(new ModelContentBlockStopEvent(index)); }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse)); return events.ToArray();
    }
    private sealed class Model(params ModelStreamEvent[][] turns) : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var selected = turns[Calls++]; foreach (var item in selected) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); } }
    }
    private sealed class StatefulModel(ModelStreamEvent[] events) : IStatefulModel
    {
        public bool Stateful => true;
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); } }
    }
}
