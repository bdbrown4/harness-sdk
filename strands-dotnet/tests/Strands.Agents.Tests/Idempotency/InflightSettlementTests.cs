using System.Runtime.CompilerServices;
using System.Text.Json;
using static Strands.Agents.Tests.Idempotency.InflightConformanceTests;
using static Strands.Agents.Tests.Idempotency.InflightIdempotencyTests;

namespace Strands.Agents.Tests.Idempotency;

public sealed class InflightSettlementTests
{
    [Fact]
    public async Task FinalHookAndEveryDuplicateShareTheAlreadyBoundStructuredResult()
    {
        var model = new ControlledModel { ToolName = StructuredOutputTool<Answer>.ToolName }; var hooks = new HookRegistry();
        var entered = Signal(); var release = Signal(); AgentResult? observed = null; var value = new Answer(9); var decodes = 0;
        hooks.Register<AgentResultEvent>(async (evt, _) => { observed = evt.Result; entered.TrySetResult(true); await release.Task; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks }); model.Release.TrySetResult(true);
        var options = new StructuredOutputOptions<Answer> { Schema = ParseJson("{\"type\":\"object\"}"), Deserializer = _ => { decodes++; return value; } };
        var primary = agent.InvokeStructuredAsync("first", options, new() { IdempotencyToken = "bound" }); Task<StructuredOutputResult<Answer>>? duplicate = null;
        try { await Ready(entered.Task); duplicate = agent.InvokeStructuredAsync("ignored", options, new() { IdempotencyToken = "bound" }); Assert.False(duplicate.IsCompleted); }
        finally { release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(observed, (await primary).Result); Assert.Same(observed, (await duplicate!).Result); Assert.Same(value, (await duplicate!).Output); Assert.Equal(1, decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleStructuredDuplicateFailsWithoutDecodingOrChangingPrimary(bool valueType)
    {
        var model = new ControlledModel { ToolName = StructuredOutputTool<Answer>.ToolName }; var entered = Signal(); var release = Signal(); var hooks = new HookRegistry(); var decodes = 0;
        hooks.Register<AgentResultEvent>(async (_, _) => { entered.TrySetResult(true); await release.Task; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks }); model.Release.TrySetResult(true);
        var primary = agent.InvokeStructuredAsync("first", new StructuredOutputOptions<Answer> { Schema = ParseJson("{\"type\":\"object\"}"), Deserializer = _ => new(4) }, new() { IdempotencyToken = "mismatch" });
        Task? duplicate = null;
        try
        {
            await Ready(entered.Task);
            duplicate = valueType
                ? agent.InvokeStructuredAsync("ignored", new StructuredOutputOptions<int> { Schema = ParseJson("{\"type\":\"object\"}"), Deserializer = _ => { decodes++; return 42; } }, new() { IdempotencyToken = "mismatch" })
                : agent.InvokeStructuredAsync("ignored", new StructuredOutputOptions<string> { Schema = ParseJson("{\"type\":\"object\"}"), Deserializer = _ => { decodes++; return "wrong"; } }, new() { IdempotencyToken = "mismatch" });
        }
        finally { release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        await primary; await Assert.ThrowsAsync<StructuredOutputException>(() => duplicate!); Assert.Equal(0, decodes); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task OrdinaryResultCannotBeForgedIntoStructuredOutputByADuplicateDecoder()
    {
        var model = new ControlledModel(); await using var agent = new Agent(new() { Model = model });
        var primary = agent.InvokeAsync("first", new() { IdempotencyToken = "ordinary" }); Task<StructuredOutputResult<Answer>>? duplicate = null; var decodes = 0;
        try
        {
            await Ready(model.Entered.Task);
            duplicate = agent.InvokeStructuredAsync("ignored", new StructuredOutputOptions<Answer> { Schema = ParseJson("{\"type\":\"object\"}"), Deserializer = _ => { decodes++; return new(8); } }, new() { IdempotencyToken = "ordinary" });
        }
        finally { model.Release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        await primary; await Assert.ThrowsAsync<StructuredOutputException>(() => duplicate!); Assert.Equal(0, decodes);
    }

    [Fact]
    public async Task FinalHookFailureIsTheExactSharedPrimaryEvenAfterTheModelSucceeded()
    {
        var model = new ControlledModel(); var hooks = new HookRegistry(); var expected = new IOException("final hook"); var entered = Signal(); var release = Signal();
        hooks.Register<AgentResultEvent>(async (_, _) => { entered.TrySetResult(true); await release.Task; throw expected; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks }); model.Release.TrySetResult(true);
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "hook" }); Task<AgentResult>? duplicate = null;
        try { await Ready(entered.Task); duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "hook" }); Assert.False(duplicate.IsCompleted); }
        finally { release.TrySetResult(true); await Observe(first); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => first)); Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => duplicate!));
    }

    [Fact]
    public async Task DisposeFailureFromAnEarlyClosedPrimaryIsSharedAfterPhysicalCleanup()
    {
        var model = new CleanupModel(); var expected = new IOException("model iterator close"); model.Error = expected;
        await using var agent = new Agent(new() { Model = model }); var iterator = agent.StreamAsync("first", new() { IdempotencyToken = "dispose" }).GetAsyncEnumerator(); Task? close = null; Task<AgentResult>? duplicate = null;
        try
        {
            while (await iterator.MoveNextAsync()) if (iterator.Current is ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent }) break;
            duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "dispose" }); close = iterator.DisposeAsync().AsTask(); await Ready(model.Closing.Task);
            Assert.False(close.IsCompleted); Assert.False(duplicate.IsCompleted);
        }
        finally { model.Release.TrySetResult(true); if (close is not null) await Observe(close); else await Observe(iterator.DisposeAsync().AsTask()); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => close!)); Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => duplicate!));
    }

    [Fact]
    public async Task SeeingThePrimaryTerminalEventDoesNotSettleADuplicateUntilIteratorClose()
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true); await using var agent = new Agent(new() { Model = model });
        var iterator = agent.StreamAsync("first", new() { IdempotencyToken = "terminal" }).GetAsyncEnumerator(); Task<AgentResult>? duplicate = null; AgentResult? result = null;
        try
        {
            while (await iterator.MoveNextAsync()) if (iterator.Current is AgentResultEvent terminal) { result = terminal.Result; break; }
            Assert.NotNull(result); duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "terminal" }); Assert.False(duplicate.IsCompleted);
        }
        finally { await iterator.DisposeAsync(); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(result, await duplicate!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnCallbacksCannotJoinTheirReceiptWithNormalFlowOrSynchronousSuppression(bool suppressFlow)
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true); var hooks = new HookRegistry(); Task<AgentResult>? own = null;
        hooks.Register<BeforeInvocationEvent>((evt, _) =>
        {
            if (suppressFlow) { using var suppression = ExecutionContext.SuppressFlow(); own = evt.Agent.InvokeAsync("self", new() { IdempotencyToken = "own" }); }
            else own = evt.Agent.InvokeAsync("self", new() { IdempotencyToken = "own" });
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        await agent.InvokeAsync("first", new() { IdempotencyToken = "own" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => own!); Assert.Contains("cannot join its own", error.Message); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task FlowingCallbackAfterAwaitStillRefusesItsOwnToken()
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true); var hooks = new HookRegistry(); Task<AgentResult>? own = null;
        hooks.Register<BeforeInvocationEvent>(async (evt, _) => { await Task.Yield(); own = evt.Agent.InvokeAsync("self", new() { IdempotencyToken = "async-own" }); });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks }); await agent.InvokeAsync("first", new() { IdempotencyToken = "async-own" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => own!); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task ExpiredCapturedExecutionContextCanStartANewInvocationAfterSettlement()
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true); var hooks = new HookRegistry(); var escapedRelease = Signal(); Task<AgentResult>? escaped = null; var before = 0;
        hooks.Register<BeforeInvocationEvent>((evt, _) =>
        {
            if (Interlocked.Increment(ref before) == 1) escaped = Task.Run(async () => { await escapedRelease.Task; return await evt.Agent.InvokeAsync("later", new() { IdempotencyToken = "escaped" }); });
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        try { await agent.InvokeAsync("first", new() { IdempotencyToken = "escaped" }); }
        finally { escapedRelease.TrySetResult(true); if (escaped is not null) await Observe(escaped); }
        Assert.Equal(StopReasons.EndTurn, (await escaped!).StopReason); Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task ReentryIntoADifferentAgentRemainsUsable()
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true); await using var other = new Agent(new() { Model = model });
        var hooks = new HookRegistry(); AgentResult? nested = null; hooks.Register<BeforeInvocationEvent>(async (_, _) => nested = await other.InvokeAsync("other", new() { IdempotencyToken = "shared-name" }));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks }); var first = await agent.InvokeAsync("first", new() { IdempotencyToken = "shared-name" });
        Assert.NotSame(first, nested); Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task FailedInitializationIsSharedButTokenReuseDoesNotResetExistingInitializationPolicy()
    {
        var store = new FailingLoadStore(); var model = new ControlledModel(); await using var agent = new Agent(new() { Model = model, SessionStore = store, SessionId = "init" });
        var first = agent.InvokeAsync("first", new() { IdempotencyToken = "init" }); Task<AgentResult>? duplicate = null;
        try { await Ready(store.Entered.Task); duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "init" }); }
        finally { store.Release.TrySetResult(true); await Observe(first); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(store.Error, await Assert.ThrowsAsync<IOException>(() => first)); Assert.Same(store.Error, await Assert.ThrowsAsync<IOException>(() => duplicate!));
        Assert.Same(store.Error, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("retry token", new() { IdempotencyToken = "init" }))); Assert.Equal(1, store.Loads); Assert.Equal(0, model.Calls);
    }

    private sealed class FailingLoadStore : ISessionStore
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal(); internal TaskCompletionSource<bool> Release { get; } = Signal(); internal IOException Error { get; } = new("session initialization"); internal int Loads;
        public async ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) { Loads++; Entered.TrySetResult(true); await Release.Task; throw Error; }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }
    private sealed class CleanupModel : IModel
    {
        internal TaskCompletionSource<bool> Closing { get; } = Signal(); internal TaskCompletionSource<bool> Release { get; } = Signal(); internal Exception? Error;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "partial"); }
            finally { Closing.TrySetResult(true); await Release.Task; if (Error is not null) throw Error; }
        }
    }
}
