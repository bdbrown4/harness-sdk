using Strands.Agents.Middleware;
using static Strands.Agents.Tests.ToolBatchExecutor.BatchTestSupport;

namespace Strands.Agents.Tests.ToolBatchExecutor;

public sealed class ToolBatchSchedulingTests
{
    [Fact]
    public async Task ConcurrentDuplicateRequestsSharePublishedTaskAndOnePipeline()
    {
        var entered = Signal(); var release = Signal(); var bodies = 0; var before = 0; var after = 0; var middleware = 0;
        var scheduler = new Scheduler(async (batch, _) =>
        {
            var tasks = new Task<ToolResultBlock?>[12];
            try
            {
                await Task.WhenAll(Enumerable.Range(0, tasks.Length).Select(i => Task.Run(() => { tasks[i] = batch.ExecuteAsync(0); })));
                await Ready(entered.Task); Assert.All(tasks, task => Assert.Same(tasks[0], task));
            }
            finally { release.TrySetResult(true); }
            await Task.WhenAll(tasks);
        });
        await using var agent = Agent(Tool(async (_, _, _) => { Interlocked.Increment(ref bodies); entered.TrySetResult(true); await release.Task; return "ok"; }), scheduler, calls: 1);
        agent.Hooks.Register<BeforeToolCallEvent>((_, _) => { Interlocked.Increment(ref before); return default; });
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => { Interlocked.Increment(ref after); return default; });
        agent.Middleware.RegisterInput(MiddlewareStages.ExecuteTool, (value, _) => { Interlocked.Increment(ref middleware); return new ValueTask<ExecuteToolContext>(value); });
        try { await agent.InvokeAsync("run"); } finally { release.TrySetResult(true); }
        Assert.Equal(1, bodies); Assert.Equal(1, before); Assert.Equal(1, after); Assert.Equal(1, middleware);
        Assert.Equal(1, agent.Metrics.ToolMetrics["work"].CallCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IgnoredTasksAreBoundedAndPhysicallyJoined(int bound)
    {
        var entered = Signal(); var release = Signal(); var returned = Signal(); var active = 0; var maximum = 0; var completed = 0;
        var scheduler = new Scheduler((batch, token) => { for (var i = 0; i < batch.Calls.Count; i++) _ = batch.ExecuteAsync(i); returned.TrySetResult(true); return default; });
        await using var agent = Agent(Tool(async (_, _, _) =>
        {
            var count = Interlocked.Increment(ref active); int old;
            do { old = Volatile.Read(ref maximum); if (count <= old) break; } while (Interlocked.CompareExchange(ref maximum, count, old) != old);
            if (count == bound) entered.TrySetResult(true);
            try { await release.Task; return "ok"; } finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref completed); }
        }), scheduler, calls: 5, parallel: bound);
        var invocation = agent.InvokeAsync("run");
        try { await Ready(entered.Task); await Ready(returned.Task); Assert.False(invocation.IsCompleted); Assert.Equal(bound, maximum); }
        finally { release.TrySetResult(true); await invocation; }
        Assert.Equal(5, completed); Assert.Equal(0, active); Assert.Equal(bound, maximum);
    }

    [Fact]
    public async Task AdmissionClosesAtSchedulerReturnIncludingCachedIndex()
    {
        ToolBatchExecutionContext? captured = null; Task<ToolResultBlock?>? accepted = null;
        var scheduler = new Scheduler((batch, _) => { captured = batch; accepted = batch.ExecuteAsync(0); return default; });
        await using var agent = Agent(Tool((_, _, _) => new("ok")), scheduler);
        await agent.InvokeAsync("run"); Assert.NotNull(await accepted!);
        Assert.IsType<InvalidOperationException>(Record.Exception(() => { captured!.ExecuteAsync(0); }));
        Assert.IsType<InvalidOperationException>(Record.Exception(() => { captured!.ExecuteAsync(1); }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task InvalidIndexDoesNotConsumeAnyAdmission(int index)
    {
        var bodies = 0; var scheduler = new Scheduler(async (batch, _) => { Assert.IsType<ArgumentOutOfRangeException>(Record.Exception(() => { batch.ExecuteAsync(index); })); await batch.ExecuteAsync(0); });
        await using var agent = Agent(Tool((_, _, _) => { bodies++; return new("ok"); }), scheduler, calls: 2);
        await agent.InvokeAsync("run"); Assert.Equal(1, bodies);
    }

    [Theory]
    [InlineData(ToolExecutionStrategy.Sequential)]
    [InlineData(ToolExecutionStrategy.Concurrent)]
    public async Task NullOptionRetainsEnumExecution(ToolExecutionStrategy strategy)
    {
        var bodies = 0;
        await using var agent = new Agent(new() { Model = new Model(Call("a"), Call("b")), Tools = [Tool((_, _, _) => { Interlocked.Increment(ref bodies); return new("ok"); })], ToolExecution = strategy, MaxTurns = 1 });
        await agent.InvokeAsync("run"); Assert.Equal(2, bodies);
        Assert.Equal(new[] { "a", "b" }, agent.Messages[^1].Content.OfType<ToolResultBlock>().Select(result => result.ToolUseId));
    }
}
