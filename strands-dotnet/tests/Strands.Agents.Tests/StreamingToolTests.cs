using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class StreamingToolTests
{
    [Fact]
    public async Task EarlyStreamDisposalPreservesTheToolCleanupFailureWhenInvocationCleanupAlsoFails()
    {
        var primary = new InvalidOperationException("after-tool cleanup");
        var secondary = new IOException("after-invocation cleanup");
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((_, _) => throw primary);
        hooks.Register<AfterInvocationEvent>((_, _) => throw secondary);
        var tool = new StreamingFunctionTool(new("stream", "Stream progress", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, token) => UntilCancelled(context.ToolUse.ToolUseId, token));
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}"))), Tools = [tool], Hooks = hooks });
        var iterator = agent.StreamAsync("go").GetAsyncEnumerator();
        while (await iterator.MoveNextAsync() && iterator.Current is not ToolStreamUpdateEvent) { }

        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => iterator.DisposeAsync().AsTask()));

        Assert.Same(secondary, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("denied")]
    [InlineData("missing")]
    [InlineData("schema")]
    public async Task EveryToolOutcomeEmitsOneNormalizedRichTerminalAfterItsResultHook(string mode)
    {
        var order = new List<string>();
        var state = new Dictionary<string, object?>();
        var hooks = new HookRegistry();
        var failure = new IOException("tool body failed");
        hooks.Register<BeforeToolCallEvent>((value, _) => { if (mode == "denied") value.CancelReason = "denied"; return default; });
        hooks.Register<AfterToolCallEvent>((value, _) => { order.Add("after"); value.Result = value.Result with { ToolUseId = "forged" }; return default; });
        hooks.Register<ToolResultEvent>((value, _) => { order.Add("result"); Assert.Equal(2, value.Agent.Messages.Count); Assert.Same(state, value.InvocationState); return default; });
        var schema = JsonSerializer.SerializeToElement(new { type = "object", required = mode == "schema" ? new[] { "value" } : Array.Empty<string>() });
        var tool = new StreamingFunctionTool(new("stream", "Stream progress", schema), (_, context, _) => mode == "error" ? throw failure : ImmediateResult(context.ToolUse.ToolUseId));
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("call", mode == "missing" ? "missing" : "stream", "{}")), AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });
        var events = new List<ToolResultEvent>();

        await foreach (var update in agent.StreamAsync("go", new() { InvocationState = state }))
            if (update is ToolResultEvent result) events.Add(result);

        var terminal = Assert.Single(events);
        Assert.Equal("call", terminal.Result.ToolUseId);
        Assert.Equal(mode == "success" ? ToolResultStatus.Success : ToolResultStatus.Error, terminal.Result.Status);
        if (mode == "error") Assert.Same(failure, terminal.Result.Error);
        Assert.Equal(["after", "result"], order);
        Assert.Equal(terminal.Result, Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]));
    }

    [Fact]
    public async Task ProgressHooksCompleteBeforeTheToolAdvancesAndItsAfterHookRuns()
    {
        var order = new List<string>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((_, _) => { order.Add("before"); return default; });
        hooks.Register<ToolStreamUpdateEvent>(async (_, _) => { await Task.Yield(); order.Add("progress"); });
        hooks.Register<AfterToolCallEvent>((_, _) => { order.Add("after"); return default; });
        var tool = new StreamingFunctionTool(new("stream", "Stream progress", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => ImmediateResult(context.ToolUse.ToolUseId));
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}")), AgentTests.Text("done")), Tools = [tool], Hooks = hooks });

        await agent.InvokeAsync("go");

        Assert.Equal(["before", "progress", "after"], order);
    }

    [Fact]
    public async Task CancellationRetainsTheStreamFailureAndItsCleanupErrorOnTheCommittedResult()
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException("body cancelled", cancellation.Token);
        var cleanup = new IOException("iterator cleanup");
        var hooks = new HookRegistry();
        ToolResultBlock? observed = null;
        hooks.Register<AfterToolCallEvent>((value, _) => { observed = value.Result; return default; });
        var stream = new CancelledStream(cancellation, primary, cleanup);
        var tool = new StreamingFunctionTool(new("stream", "Stream progress", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => stream);
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}"))), Tools = [tool], Hooks = hooks });

        var result = await agent.InvokeAsync("go", cancellationToken: cancellation.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.NotNull(observed);
        Assert.Same(primary, observed.Error);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Same(primary, Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Error);
        Assert.Equal(1, stream.Disposals);
    }

    [Fact]
    public async Task EarlyStreamDisposalRetainsAPipelineCleanupFailure()
    {
        var failure = new InvalidOperationException("after-tool cleanup");
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((_, _) => throw failure);
        var tool = new StreamingFunctionTool(new("stream", "Stream progress", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, token) => UntilCancelled(context.ToolUse.ToolUseId, token));
        await using var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}"))), Tools = [tool], Hooks = hooks });
        var iterator = agent.StreamAsync("go").GetAsyncEnumerator();
        while (await iterator.MoveNextAsync() && iterator.Current is not ToolStreamUpdateEvent) { }
        Assert.IsType<ToolStreamUpdateEvent>(iterator.Current);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => iterator.DisposeAsync().AsTask());

        Assert.Same(failure, error);
        Assert.Equal("call", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).ToolUseId);
    }

    [Fact]
    public async Task ProgressIsObservedBeforeTheToolCompletesAndBeforeTheNextModelTurn()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "stream", "{}")), AgentTests.Text("done"));
        var tool = new StreamingFunctionTool(new("stream", "Stream progress", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, token) => ProgressThenResult(context.ToolUse.ToolUseId, release.Task, token));
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var observed = false;
        try
        {
            await foreach (var item in agent.StreamAsync("go", cancellationToken: deadline.Token))
            {
                if (item is not ToolStreamUpdateEvent update) continue;
                Assert.False(release.Task.IsCompleted);
                Assert.Single(model.Requests);
                Assert.Equal("call", update.ToolUse.ToolUseId);
                Assert.Equal("working", update.Event.Data);
                observed = true;
                release.TrySetResult(true);
            }
        }
        finally { release.TrySetResult(true); }

        Assert.True(observed, "Tool progress must reach the agent stream before the tool returns.");
        Assert.Equal("complete", Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]).Content);
    }

    private static async IAsyncEnumerable<IToolStreamEvent> ProgressThenResult(string id, Task release, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ToolStreamEvent("working");
        await release.WaitAsync(cancellationToken);
        yield return new ToolStreamResultEvent(new(id, "complete"));
    }

    private static async IAsyncEnumerable<IToolStreamEvent> ImmediateResult(string id)
    {
        yield return new ToolStreamEvent("working");
        yield return new ToolStreamResultEvent(new(id, "complete"));
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IToolStreamEvent> UntilCancelled(string id, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ToolStreamEvent("working");
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield return new ToolStreamResultEvent(new(id, "unreachable"));
    }

    private sealed class CancelledStream(CancellationTokenSource cancellation, OperationCanceledException primary, Exception cleanup) : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        private bool _started;
        public int Disposals { get; private set; }
        public IToolStreamEvent Current => new ToolStreamEvent("working");
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (!_started) { _started = true; return new(true); }
            cancellation.Cancel();
            throw primary;
        }
        public ValueTask DisposeAsync() { Disposals++; throw cleanup; }
    }
}
