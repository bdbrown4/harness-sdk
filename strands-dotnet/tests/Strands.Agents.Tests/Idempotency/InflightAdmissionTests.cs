using System.Text.Json;
using static Strands.Agents.Tests.Idempotency.InflightConformanceTests;
using static Strands.Agents.Tests.Idempotency.InflightIdempotencyTests;

namespace Strands.Agents.Tests.Idempotency;

public sealed class InflightAdmissionTests
{
    [Fact]
    public async Task AnOrdinaryPrimaryCannotBeRetroactivelyJoinedBySupplyingAToken()
    {
        var model = new ControlledModel(); await using var agent = new Agent(new() { Model = model }); var primary = agent.InvokeAsync("ordinary");
        try
        {
            await Ready(model.Entered.Task);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("cannot attach", new() { IdempotencyToken = "late" }));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("ordinary duplicate"));
        }
        finally { model.Release.TrySetResult(true); await Observe(primary); }
        Assert.Equal(StopReasons.EndTurn, (await primary).StopReason);
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("now fresh", new() { IdempotencyToken = "late" })).StopReason); Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task AnAcceptedDirectBodyStillPreventsNewModelAdmissionUntilItPhysicallyReturns()
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true); var entered = Signal(); var release = Signal();
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("direct", async (_, _, _) => { entered.TrySetResult(true); await release.Task; return "direct"; })] });
        var direct = agent.InvokeToolAsync(new("direct-call", "direct", ParseJson("{}"))).AsTask();
        try
        {
            await Ready(entered.Task);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("blocked", new() { IdempotencyToken = "direct-first" }));
            Assert.Equal(0, model.Calls);
        }
        finally { release.TrySetResult(true); await Observe(direct); }
        Assert.Equal("direct", (await direct).Content);
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("accepted", new() { IdempotencyToken = "direct-first" })).StopReason); Assert.Equal(1, model.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SimultaneousFirstAdmissionsEitherJoinExactTokenOrRejectTheDifferentToken(bool different)
    {
        var model = new ControlledModel(); var start = Signal(); var admitted = new[] { Signal(), Signal() };
        await using var agent = new Agent(new() { Model = model });
        var calls = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var pending = agent.InvokeAsync("prompt-" + index, new() { IdempotencyToken = different ? "token-" + index : "same" });
            admitted[index].TrySetResult(true); return await pending;
        })).ToArray();
        try { start.TrySetResult(true); await Ready(Task.WhenAll(admitted.Select(value => value.Task))); await Ready(model.Entered.Task); Assert.Equal(1, model.Calls); }
        finally { start.TrySetResult(true); model.Release.TrySetResult(true); await Observe(Task.WhenAll(calls)); }
        if (different)
        {
            _ = Assert.Single(calls, task => task.Status == TaskStatus.RanToCompletion);
            var denied = Assert.Single(calls, task => task.IsFaulted); await Assert.ThrowsAsync<ConcurrentInvocationException>(() => denied);
        }
        else Assert.Same(await calls[0], await calls[1]);
        Assert.Equal(2, agent.Messages.Count);
    }

    [Fact]
    public async Task AFailedExecutionClearsOnlyItsTransientTokenAndLaterReuseRunsAgain()
    {
        var model = new ControlledModel(); model.Release.TrySetResult(true); var hooks = new HookRegistry(); var expected = new IOException("one failed boundary"); var count = 0;
        hooks.Register<BeforeInvocationEvent>((_, _) => { if (Interlocked.Increment(ref count) == 1) throw expected; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("failed", new() { IdempotencyToken = "retry" })));
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("success", new() { IdempotencyToken = "retry" })).StopReason); Assert.Equal(1, model.Calls); Assert.Equal("success", agent.Messages[0].Text);
    }

    [Fact]
    public async Task ModelPrimaryAndAfterHookCleanupKeepTheSameCausalExceptionForEveryWaiter()
    {
        var entered = Signal(); var release = Signal(); var primaryError = new IOException("model primary"); var cleanup = new InvalidOperationException("after cleanup"); var hooks = new HookRegistry();
        hooks.Register<AfterInvocationEvent>((_, _) => throw cleanup);
        await using var agent = new Agent(new() { Model = new FailingModel(entered, release, primaryError), Hooks = hooks });
        var primary = agent.InvokeAsync("first", new() { IdempotencyToken = "causes" }); Task<AgentResult>? duplicate = null;
        try { await Ready(entered.Task); duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "causes" }); }
        finally { release.TrySetResult(true); await Observe(primary); if (duplicate is not null) await Observe(duplicate); }
        Assert.Same(primaryError, await Assert.ThrowsAsync<IOException>(() => primary)); Assert.Same(primaryError, await Assert.ThrowsAsync<IOException>(() => duplicate!));
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primaryError.Data["Strands.CleanupErrors"])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedInterfaceNullMatchesNullableValueTypeButNotNonnullableValueType(bool nullable)
    {
        var model = new ControlledModel { ToolName = StructuredOutputTool<int>.ToolName }; model.Release.TrySetResult(true);
        var hooks = new HookRegistry(); var entered = Signal(); var release = Signal(); var decodes = 0; var tool = new NullTool();
        hooks.Register<AgentResultEvent>(async (_, _) => { entered.TrySetResult(true); await release.Task; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var primary = agent.InvokeAsync("first", new() { IdempotencyToken = "null-value", StructuredOutputTool = tool });
        Task<StructuredOutputResult<int?>>? accepted = null; Task<StructuredOutputResult<int>>? rejected = null;
        try
        {
            await Ready(entered.Task);
            if (nullable) accepted = agent.InvokeStructuredAsync("ignored", new StructuredOutputOptions<int?> { Schema = tool.Spec.InputSchema, Deserializer = _ => { decodes++; return 12; } }, new() { IdempotencyToken = "null-value" });
            else rejected = agent.InvokeStructuredAsync("ignored", new StructuredOutputOptions<int> { Schema = tool.Spec.InputSchema, Deserializer = _ => { decodes++; return 12; } }, new() { IdempotencyToken = "null-value" });
        }
        finally { release.TrySetResult(true); await Observe(primary); if (accepted is not null) await Observe(accepted); if (rejected is not null) await Observe(rejected); }
        if (nullable) { Assert.Null((await accepted!).Output); Assert.Same(await primary, (await accepted!).Result); }
        else await Assert.ThrowsAsync<StructuredOutputException>(() => rejected!);
        Assert.Equal(0, decodes); Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task DuplicateAlreadyCancelledAtAdmissionDoesNotOwnOrCancelAcceptedPrimaryWork()
    {
        var model = new ControlledModel(); using var caller = new CancellationTokenSource(); caller.Cancel();
        await using var agent = new Agent(new() { Model = model }); var primary = agent.InvokeAsync("first", new() { IdempotencyToken = "already-cancelled" });
        try
        {
            await Ready(model.Entered.Task); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeAsync("ignored", new() { IdempotencyToken = "already-cancelled" }, caller.Token));
            Assert.Equal(caller.Token, error.CancellationToken); Assert.False(primary.IsCompleted); Assert.False(model.ObservedToken.IsCancellationRequested);
        }
        finally { model.Release.TrySetResult(true); await Observe(primary); }
        Assert.Equal(StopReasons.EndTurn, (await primary).StopReason); Assert.Equal(1, model.Calls);
    }

    private sealed class NullTool : IStructuredOutputTool
    {
        internal int Calls;
        public ToolSpec Spec { get; } = new(StructuredOutputTool<int>.ToolName, "Accepted interface null", ParseJson("{\"type\":\"object\"}"));
        public bool HasResult => Calls != 0;
        public object? Result => null;
        public bool TryGetResult(string toolUseId, out object? result) { result = null; return HasResult && toolUseId == "tool-call"; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) { Calls++; return new("accepted"); }
    }
    private sealed class FailingModel(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release, Exception error) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { entered.TrySetResult(true); await release.Task; if (error is not null) throw error; yield break; }
    }
}
