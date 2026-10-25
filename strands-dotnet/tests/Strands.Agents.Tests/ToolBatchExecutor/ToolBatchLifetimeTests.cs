using System.Runtime.CompilerServices;
using static Strands.Agents.Tests.ToolBatchExecutor.BatchTestSupport;

namespace Strands.Agents.Tests.ToolBatchExecutor;

public sealed class ToolBatchLifetimeTests
{
    [Fact]
    public async Task CallerCancellationKeepsAcceptedBodiesJoinedAndDoesNotDisposeCallerSource()
    {
        var entered = Signal(); var release = Signal(); var schedulerReturned = Signal(); var exited = false; CancellationToken scoped = default;
        using var caller = new CancellationTokenSource();
        var scheduler = new Scheduler((batch, token) => { scoped = token; _ = batch.ExecuteAsync(0); schedulerReturned.TrySetResult(true); return default; });
        await using var agent = Agent(Tool(async (_, _, token) => { Assert.Equal(scoped, token); entered.TrySetResult(true); try { await release.Task; return "accepted"; } finally { exited = true; } }), scheduler, calls: 1);
        var invocation = agent.InvokeAsync("run", cancellationToken: caller.Token);
        try
        {
            await Ready(entered.Task); await Ready(schedulerReturned.Task); Assert.NotEqual(caller.Token, scoped);
            caller.Cancel(); Assert.True(scoped.IsCancellationRequested); Assert.False(invocation.IsCompleted); Assert.False(exited);
        }
        finally { release.TrySetResult(true); await invocation; }
        Assert.True(exited); using var stillOwned = caller.Token.Register(() => { }); caller.Cancel();
    }

    [Fact]
    public async Task SchedulerFailureCancelsScopeButWaitsForIgnoredBody()
    {
        var entered = Signal(); var release = Signal(); var faulted = Signal(); var expected = new InvalidOperationException("scheduler"); var exited = false; CancellationToken scoped = default;
        var scheduler = new Scheduler(async (batch, token) => { scoped = token; _ = batch.ExecuteAsync(0); await entered.Task; faulted.TrySetResult(true); throw expected; });
        await using var agent = Agent(Tool(async (_, _, _) => { entered.TrySetResult(true); try { await release.Task; return "done"; } finally { exited = true; } }), scheduler, calls: 1);
        var invocation = agent.InvokeAsync("run");
        try { await Ready(faulted.Task); await ReadyCancellation(scoped); Assert.False(invocation.IsCompleted); Assert.False(exited); }
        finally { release.TrySetResult(true); }
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => invocation)); Assert.True(exited);
    }

    [Fact]
    public async Task IssuedPipelineFailureRemainsPrimaryOverEarlierCompletedSchedulerFailure()
    {
        var pipeline = new IOException("pipeline"); var schedulerError = new InvalidOperationException("scheduler");
        var scheduler = new Scheduler(async (batch, _) => { await batch.ExecuteAsync(0); throw schedulerError; });
        await using var agent = Agent(Tool((_, _, _) => new("ok")), scheduler, calls: 1);
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => throw pipeline);
        var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("run")); Assert.Same(pipeline, actual);
        Assert.Same(schedulerError, Assert.Single(Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task IssuedOrderDeterminesPipelinePrimaryRegardlessOfSchedulingOrder()
    {
        var first = new IOException("issued first"); var second = new InvalidOperationException("issued second");
        var scheduler = new Scheduler(async (batch, _) => { await batch.ExecuteAsync(1); await batch.ExecuteAsync(0); });
        await using var agent = Agent(Tool((_, _, _) => new("ok")), scheduler, calls: 2);
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => throw (value.ToolUse.ToolUseId == "0" ? first : second));
        var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("run")); Assert.Same(first, actual);
        Assert.Same(second, Assert.Single(Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task SamePipelineAndSchedulerExceptionIsNotSelfAttached()
    {
        var expected = new InvalidOperationException("shared");
        var scheduler = new Scheduler(async (batch, _) => { await batch.ExecuteAsync(0); throw expected; });
        await using var agent = Agent(Tool((_, _, _) => new("ok")), scheduler, calls: 1);
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => throw expected);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("run")));
        Assert.Null(expected.Data["Strands.CleanupErrors"]);
    }

    [Fact]
    public async Task HardCallerCancellationReachesQueuedAcceptedCallsWithoutRunningTheirBodies()
    {
        var entered = Signal(); var release = Signal(); var bodies = 0;
        using var caller = new CancellationTokenSource();
        var scheduler = new Scheduler((batch, token) => { for (var i = 0; i < batch.Calls.Count; i++) _ = batch.ExecuteAsync(i); return default; });
        await using var agent = Agent(Tool(async (_, _, _) => { Interlocked.Increment(ref bodies); entered.TrySetResult(true); await release.Task; return "first"; }), scheduler, calls: 3, parallel: 1);
        var invocation = agent.InvokeAsync("run", cancellationToken: caller.Token);
        try { await Ready(entered.Task); caller.Cancel(); Assert.False(invocation.IsCompleted); }
        finally { release.TrySetResult(true); await invocation; }
        Assert.Equal(1, bodies);
        Assert.Equal(3, agent.Messages[^1].Content.OfType<ToolResultBlock>().Count());
    }

    [Fact]
    public async Task CancellationCallbackFailureRemainsSecondaryToSchedulerFailure()
    {
        var expected = new InvalidOperationException("scheduler"); var cleanup = new IOException("cancel callback"); CancellationTokenRegistration registration = default;
        var scheduler = new Scheduler((batch, token) => { registration = token.Register(() => throw cleanup); throw expected; });
        await using var agent = Agent(Tool((_, _, _) => new("not admitted")), scheduler, calls: 1);
        try
        {
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("run")); Assert.Same(expected, actual);
            var secondary = Assert.Single(Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]));
            Assert.Same(cleanup, Assert.Single(Assert.IsType<AggregateException>(secondary).InnerExceptions));
        }
        finally { registration.Dispose(); }
    }

    private static async Task ReadyCancellation(CancellationToken token)
    {
        var signal = Signal(); using var registration = token.Register(() => signal.TrySetResult(true)); await Ready(signal.Task);
    }
}
