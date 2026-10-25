using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed partial class AutomaticTelemetryTests
{
    [Fact]
    public async Task CancellingAutomaticBackgroundWaitingRetainsStatisticsAndTerminalReason()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var tool = new FunctionTool(new("calculate", "Calculate", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return "7";
        });
        await using var agent = new Agent(new() { Model = new TimingModel(true), Tools = [tool], BackgroundTasks = new() { Always = ["calculate"] } });
        agent.Hooks.Register<AfterInvocationEvent>(async (_, _) =>
        {
            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        }, -1);
        try
        {
            var result = await agent.InvokeAsync("calculate", cancellationToken: cancellation.Token);
            Assert.Equal(StopReasons.Cancelled, result.StopReason);
            Assert.Equal(result.Usage, Assert.IsType<AgentMetrics>(result.Metrics).AccumulatedUsage);
            var invocation = Assert.Single(spans, span => span.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
            Assert.Equal(new[] { StopReasons.Cancelled }, Assert.IsType<string[]>(invocation.GetTagItem("gen_ai.response.finish_reasons")));
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task ModelFailureAndFailingListenersCannotReplaceTheOriginalError()
    {
        var failure = new IOException("original provider failure");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped, throwOnStop: true);
        using var caller = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        await using var agent = new Agent(new() { Model = new FailingModel(failure) });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("fail")));
        Assert.Same(caller, Activity.Current);
        Assert.Equal(1, agent.Metrics.CycleCount);
        Assert.NotNull(Assert.Single(agent.Traces).EndTime);
        var invocation = Assert.Single(stopped, span => span.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
        Assert.Equal(ActivityStatusCode.Error, invocation.Status);
    }

    [Fact]
    public async Task DisposingAnIncompleteStreamClosesSpansAndAllowsTheNextInvocation()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var caller = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        await using var agent = new Agent(new() { Model = new TimingModel(false) });
        await using (var iterator = agent.StreamAsync("partial").GetAsyncEnumerator())
        {
            var sawText = false;
            while (await iterator.MoveNextAsync())
                if (iterator.Current is ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent }) { sawText = true; break; }
            Assert.True(sawText);
        }
        Assert.Same(caller, Activity.Current);
        Assert.Equal(3, stopped.Count);
        Assert.NotNull(Assert.Single(agent.Traces).EndTime);
        Assert.Equal(0, agent.Metrics.AccumulatedUsage.TotalTokens);
        Assert.Equal("7", (await agent.InvokeAsync("complete")).ToString());
        Assert.Equal(2, agent.Metrics.CycleCount);
        Assert.Equal(6, stopped.Count);
    }

    [Fact]
    public async Task OrdinaryInvocationAutomaticallyCollectsMetricsAndLocalTraces()
    {
        await using var agent = CreateToolAgent();
        var result = await agent.InvokeAsync("calculate");
        var metrics = Assert.IsType<AgentMetrics>(result.Metrics);
        Assert.Equal(new Usage(4, 6), result.Usage);
        Assert.Equal(result.Usage, metrics.AccumulatedUsage);
        Assert.Equal(2, metrics.CycleCount);
        Assert.Equal(2, Assert.Single(metrics.AgentInvocations).Cycles.Count);
        Assert.Equal(1, metrics.ToolMetrics["calculate"].CallCount);
        Assert.Equal(1, metrics.ToolMetrics["calculate"].SuccessCount);
        Assert.Equal(TimeSpan.FromMilliseconds(10), metrics.AccumulatedMetrics.Latency);
        Assert.Equal(2, result.Traces.Count);
        Assert.All(result.Traces, cycle => Assert.NotNull(cycle.EndTime));
        Assert.Equal(result.Usage, agent.Metrics.AccumulatedUsage);
    }

    [Fact]
    public async Task AutomaticActivitiesRetainParentageAndRestoreTheCallerContext()
    {
        var exported = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = exported.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var caller = new Activity("application caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        await using var agent = CreateToolAgent();
        await agent.InvokeAsync("calculate");
        Assert.Same(caller, Activity.Current);
        var spans = exported.ToArray();
        Assert.Equal(6, spans.Length);
        var invocation = Assert.Single(spans, span => span.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
        Assert.Equal(caller.SpanId, invocation.ParentSpanId);
        var cycles = spans.Where(span => span.DisplayName == "execute_agent_loop_cycle").ToArray();
        Assert.Equal(2, cycles.Length);
        Assert.All(cycles, cycle => Assert.Equal(invocation.SpanId, cycle.ParentSpanId));
        var models = spans.Where(span => span.DisplayName == "chat").ToArray();
        Assert.Equal(2, models.Length);
        Assert.All(models, model => Assert.Contains(cycles, cycle => cycle.SpanId == model.ParentSpanId));
        var tool = Assert.Single(spans, span => span.DisplayName == "execute_tool calculate");
        Assert.Equal("calculation-one", tool.GetTagItem("gen_ai.tool.call.id"));
        Assert.Contains(cycles, cycle => cycle.SpanId == tool.ParentSpanId);
    }

    [Fact]
    public async Task PriorResultStatisticsRemainStableAcrossLaterInvocations()
    {
        await using var agent = new Agent(new() { Model = new TimingModel(false) });
        var first = await agent.InvokeAsync("first");
        var firstMetrics = Assert.IsType<AgentMetrics>(first.Metrics);
        var firstTrace = Assert.Single(first.Traces);
        var second = await agent.InvokeAsync("second");
        Assert.Equal(1, firstMetrics.CycleCount);
        Assert.Single(firstMetrics.AgentInvocations);
        Assert.Equal(new Usage(2, 3), firstMetrics.AccumulatedUsage);
        Assert.Same(firstTrace, Assert.Single(first.Traces));
        Assert.Equal(2, Assert.IsType<AgentMetrics>(second.Metrics).CycleCount);
        Assert.Equal(2, agent.Metrics.AgentInvocations.Count);
        Assert.NotSame(firstTrace, Assert.Single(second.Traces));
    }

    private static Agent CreateToolAgent()
    {
        var tool = new FunctionTool(new("calculate", "Calculate an answer", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => new ValueTask<string>("7"));
        return new Agent(new() { Model = new TimingModel(true), Tools = [tool] });
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped, bool throwOnStop = false)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                stopped.Enqueue(activity);
                if (throwOnStop) throw new InvalidOperationException("observer failure");
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class FailingModel(Exception failure) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            throw failure;
        }
    }

    private sealed class TimingModel(bool useTool) : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            var tool = useTool && Interlocked.Increment(ref _calls) == 1;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, tool ? new ToolUseBlock("calculation-one", "calculate", JsonSerializer.SerializeToElement(new { })) : null);
            if (!tool) yield return new ModelContentBlockDeltaEvent(0, Text: "7");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(tool ? StopReasons.ToolUse : StopReasons.EndTurn);
            yield return new ModelMetadataEvent(new(2, 3)) { PerformanceMetrics = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(1)) };
        }
    }
}
