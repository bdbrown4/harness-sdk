using System.Runtime.CompilerServices;
using System.Text.Json;
using static Strands.Agents.Tests.Idempotency.InflightIdempotencyTests;

namespace Strands.Agents.Tests.Idempotency;

public sealed class InflightConformanceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Case")]
    public async Task TokensAreExactAndMayBeEmptyWhileDifferentOrMissingTokensStillReject(string token)
    {
        var model = new ControlledModel();
        await using var agent = new Agent(new() { Model = model });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = token });
        Task<AgentResult>? joined = null;
        try
        {
            await Ready(model.Entered.Task);
            joined = agent.InvokeAsync("ignored", new() { IdempotencyToken = token });
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("missing"));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("different", new() { IdempotencyToken = token + "x" }));
            if (token == "Case") await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("lower", new() { IdempotencyToken = "case" }));
        }
        finally { model.Release.TrySetResult(true); await Observe(first); if (joined is not null) await Observe(joined); }
        Assert.Same(await first, await joined!);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task ManyConcurrentDuplicatesObserveOneResultAndOnlyPrimaryEventsReachHooks()
    {
        var model = new ControlledModel(); var hooks = new HookRegistry(); var finals = 0; var messages = 0; var before = 0;
        hooks.Register<AgentResultEvent>((_, _) => { Interlocked.Increment(ref finals); return default; });
        hooks.Register<MessageAddedEvent>((_, _) => { Interlocked.Increment(ref messages); return default; });
        hooks.Register<BeforeInvocationEvent>((_, _) => { Interlocked.Increment(ref before); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "race" });
        Task<AgentResult>[] duplicates = [];
        try
        {
            await Ready(model.Entered.Task);
            var admissions = Enumerable.Range(0, 12).Select(_ => Signal()).ToArray();
            duplicates = admissions.Select(admitted => Task.Run(async () =>
            {
                var pending = agent.InvokeAsync("ignored", new() { IdempotencyToken = "race" });
                admitted.TrySetResult(true);
                return await pending;
            })).ToArray();
            await Ready(Task.WhenAll(admissions.Select(value => value.Task)));
            Assert.All(duplicates, task => Assert.False(task.IsCompleted));
        }
        finally { model.Release.TrySetResult(true); await Observe(first); await Observe(Task.WhenAll(duplicates)); }
        foreach (var duplicate in duplicates) Assert.Same(await first, await duplicate);
        Assert.Equal(1, model.Calls); Assert.Equal(1, before); Assert.Equal(1, finals); Assert.Equal(2, messages);
    }

    [Fact]
    public async Task DuplicateStreamContainsOnlyOneFinalEventAndDoesNotReplayModelOrMessages()
    {
        var model = new ControlledModel();
        await using var agent = new Agent(new() { Model = model });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "stream" });
        var events = new List<IAgentStreamEvent>();
        async Task Read() { await foreach (var value in agent.StreamAsync("ignored", new() { IdempotencyToken = "stream" })) events.Add(value); }
        Task? read = null;
        try { await Ready(model.Entered.Task); read = Read(); Assert.Empty(events); }
        finally { model.Release.TrySetResult(true); await Observe(first); if (read is not null) await Observe(read); }
        await read!; var final = Assert.IsType<AgentResultEvent>(Assert.Single(events)); Assert.Same(agent, final.Agent); Assert.Same(await first, final.Result);
    }

    [Fact]
    public async Task ReusingCompletedTokenStartsNewWorkAndNoTokenStillUsesOrdinaryConcurrency()
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true);
        await using var agent = new Agent(new() { Model = model });
        var first = await agent.InvokeAsync("one", new() { IdempotencyToken = "reuse" });
        var second = await agent.InvokeAsync("two", new() { IdempotencyToken = "reuse" });
        var third = await agent.InvokeAsync("three");
        Assert.NotSame(first, second); Assert.NotSame(second, third); Assert.Equal(3, model.Calls); Assert.Equal(6, agent.Messages.Count);
    }

    [Fact]
    public async Task PrimaryCallerCancellationProducesTheSameCanceledResultForAllWaiters()
    {
        var model = new ControlledModel { ObserveCancellation = true }; using var caller = new CancellationTokenSource();
        var hooks = new HookRegistry(); CancellationToken callbackToken = default;
        hooks.Register<BeforeInvocationEvent>((_, token) => { callbackToken = token; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "cancel-primary" }, caller.Token); Task<AgentResult>? duplicate = null;
        try { await Ready(model.Entered.Task); duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "cancel-primary" }); caller.Cancel(); }
        finally { caller.Cancel(); model.Release.TrySetResult(true); await Observe(first); if (duplicate is not null) await Observe(duplicate); }
        Assert.Equal(StopReasons.Cancelled, (await first).StopReason); Assert.Same(await first, await duplicate!); Assert.Equal(1, model.Calls);
        Assert.Equal(caller.Token, callbackToken); Assert.True(model.ObservedToken.IsCancellationRequested);
        using var stillOwned = caller.Token.Register(() => { });
    }

    [Fact]
    public async Task DirectToolRemainsIndependentAndItsAcceptedCallDoesNotJoinOrAlterTheReceipt()
    {
        var model = new ControlledModel(); var body = 0;
        var tool = Tool("direct", (_, _, _) => { body++; return new("direct result"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "direct" }); Task<AgentResult>? duplicate = null;
        try
        {
            await Ready(model.Entered.Task); var result = await agent.InvokeToolAsync(new("call", "direct", ParseJson("{}")));
            Assert.Equal("direct result", result.Content); duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "direct" }); Assert.False(duplicate.IsCompleted);
        }
        finally { model.Release.TrySetResult(true); await Observe(first); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(await first, await duplicate!); Assert.Equal(1, body); Assert.Equal(2, agent.Messages.Count);
    }

    [Fact]
    public async Task ChildHasAnIndependentSlotAndConversationEvenForTheSameToken()
    {
        var model = new ControlledModel(); await using var parent = new Agent(new() { Model = model }); await using var child = parent.CreateChild();
        var first = parent.InvokeAsync("parent", new() { IdempotencyToken = "child" }); Task<AgentResult>? second = null;
        try { await Ready(model.Entered.Task); second = child.InvokeAsync("child", new() { IdempotencyToken = "child" }); Assert.False(second.IsCompleted); }
        finally { model.Release.TrySetResult(true); await Observe(first); if (second is not null) await Observe(second); }
        Assert.NotSame(await first, await second!); Assert.Equal(2, model.Calls); Assert.Equal("parent", parent.Messages[0].Text); Assert.Equal("child", child.Messages[0].Text);
    }

    [Fact]
    public async Task SessionWritesAndOriginalCallerStateAreNotRepeatedForDuplicateInput()
    {
        var store = new CountingStore(); var model = new ControlledModel(); var state = new Dictionary<string, object?> { ["caller"] = new object() };
        await using var agent = new Agent(new() { Model = model, SessionStore = store, SessionId = "session" });
        var first = agent.InvokeAsync("primary", new() { IdempotencyToken = "session", InvocationState = state }); Task<AgentResult>? duplicate = null;
        try { await Ready(model.Entered.Task); duplicate = agent.InvokeAsync("never appended", new() { IdempotencyToken = "session", InvocationState = new Dictionary<string, object?> { ["ignored"] = true } }); }
        finally { model.Release.TrySetResult(true); await Observe(first); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(state, (await duplicate!).InvocationState); Assert.Single(state); Assert.Equal(1, store.Loads); Assert.Equal(2, store.Saves);
        Assert.Equal(new[] { "primary", "done" }, store.Last!.Messages.Select(value => value.Text));
    }

    [Fact]
    public async Task InterruptPauseIsSharedButACompletedTokenCanResumeWithoutReplayingTheModelOrCompletedBody()
    {
        var model = new ControlledModel { ToolName = "approve" }; var accepted = 0; var finalEntered = Signal(); var finalRelease = Signal(); var finalCount = 0;
        var hooks = new HookRegistry(); hooks.Register<AgentResultEvent>(async (_, _) => { if (Interlocked.Increment(ref finalCount) == 1) { finalEntered.TrySetResult(true); await finalRelease.Task; } });
        var tool = Tool("approve", (_, context, _) => { context.Interrupt("approval"); accepted++; return new("approved"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks }); model.Release.TrySetResult(true);
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "interrupt" }); Task<AgentResult>? duplicate = null;
        try { await Ready(finalEntered.Task); duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "interrupt" }); Assert.False(duplicate.IsCompleted); }
        finally { finalRelease.TrySetResult(true); await Observe(first); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(await first, await duplicate!); var question = Assert.Single((await first).Interrupts); Assert.Equal(0, accepted); Assert.Equal(1, model.Calls);
        var resumed = await agent.InvokeAsync(null, new() { IdempotencyToken = "interrupt", InterruptResponses = [new(question.Id, ParseJson("true"))] });
        Assert.Equal(StopReasons.EndTurn, resumed.StopReason); Assert.Equal(1, accepted); Assert.Equal(2, model.Calls);
    }

    [Fact]
    public void LegacyInvocationOptionsJsonRetainsItsDefaultShapeAndTokenRoundtripsExactly()
    {
        var original = JsonSerializer.Serialize(new InvocationOptions());
        Assert.DoesNotContain("IdempotencyToken", original);
        var restored = JsonSerializer.Deserialize<InvocationOptions>("{\"MaxTurns\":2,\"InvocationState\":{\"old\":1}}");
        Assert.NotNull(restored); Assert.Null(restored.IdempotencyToken); Assert.Equal(2, restored.MaxTurns); Assert.Single(restored.InvocationState);
        Assert.Equal("", JsonSerializer.Deserialize<InvocationOptions>(JsonSerializer.Serialize(new InvocationOptions { IdempotencyToken = "" }))!.IdempotencyToken);
    }

    [Fact]
    public async Task InvalidDuplicateOptionsStillFailValidationWithoutChangingTheAcceptedInvocation()
    {
        var model = new ControlledModel(); await using var agent = new Agent(new() { Model = model }); var first = agent.InvokeAsync("primary", new() { IdempotencyToken = "valid" });
        try { await Ready(model.Entered.Task); await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => agent.InvokeAsync("invalid", new() { IdempotencyToken = "valid", MaxTurns = 0 })); Assert.False(first.IsCompleted); }
        finally { model.Release.TrySetResult(true); await Observe(first); }
        Assert.Equal(StopReasons.EndTurn, (await first).StopReason); Assert.Equal(1, model.Calls);
    }

    internal static JsonElement ParseJson(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    internal static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> body) => new(new(name, name, ParseJson("{\"type\":\"object\"}")), body);
    internal sealed class CountingStore : ISessionStore
    {
        internal int Loads, Saves; internal SessionSnapshot? Last;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) { Loads++; return new((SessionSnapshot?)null); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Saves++; Last = snapshot; return default; }
    }
    internal sealed class ControlledModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        internal bool ObserveCancellation;
        internal string? ToolName;
        internal int Calls;
        internal CancellationToken ObservedToken;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref Calls); ObservedToken = cancellationToken; Entered.TrySetResult(true);
            if (ObserveCancellation) await Task.Delay(Timeout.Infinite, cancellationToken); else await Release.Task;
            yield return new ModelMessageStartEvent();
            if (ToolName is { } name && call == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("tool-call", name, ParseJson("{}")));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }
}
