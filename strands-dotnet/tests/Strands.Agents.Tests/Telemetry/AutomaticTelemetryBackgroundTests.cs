using System.Collections.Concurrent;
using System.Diagnostics;

namespace Strands.Agents.Tests.Telemetry;

public sealed partial class AutomaticTelemetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundToolBodiesReportTheirActualOutcome(bool fail)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = BoundaryTool("calculate", async (_, _, _) =>
        {
            await release.Task;
            if (fail) throw new IOException("background body failed");
            return "background body succeeded";
        });
        await using var agent = new Agent(new() { Model = new TimingModel(true), Tools = [tool], BackgroundTasks = new() { Always = ["calculate"] } });
        agent.Hooks.Register<AfterInvocationEvent>((_, _) => { release.TrySetResult(true); return default; }, -1);
        try
        {
            var result = await agent.InvokeAsync("background");
            var metric = Assert.Single(result.Metrics!.ToolMetrics).Value;
            Assert.Equal(1, metric.CallCount);
            Assert.Equal(fail ? 0 : 1, metric.SuccessCount);
            Assert.Equal(fail ? 1 : 0, metric.ErrorCount);
            var span = Assert.Single(stopped, value => value.DisplayName == "execute_tool calculate");
            Assert.Equal(fail ? ActivityStatusCode.Error : ActivityStatusCode.Ok, span.Status);
            Assert.DoesNotContain(result.Traces.SelectMany(cycle => cycle.Children), child => child.Name.StartsWith("Tool:", StringComparison.Ordinal));
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task LateBackgroundCompletionPreservesResultSnapshotsAndForegroundParentage()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActivitySpanId? bodySpan = null;
        var tool = BoundaryTool("calculate", async (_, _, _) =>
        {
            bodySpan = Activity.Current?.SpanId;
            started.TrySetResult(true);
            await release.Task;
            Assert.Equal(bodySpan, Activity.Current?.SpanId);
            return "late success";
        });
        await using var agent = new Agent(new()
        {
            Model = new TimingModel(true),
            Tools = [tool],
            BackgroundTasks = new() { Always = ["calculate"], WaitForCompletion = false }
        });
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => { completed.TrySetResult(true); return default; });
        try
        {
            var first = await agent.InvokeAsync("background");
            Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            var second = await agent.InvokeAsync("later foreground");
            release.TrySetResult(true);
            Assert.Same(completed.Task, await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            var span = Assert.Single(stopped, value => value.DisplayName == "execute_tool calculate");
            Assert.Equal(span.SpanId, bodySpan);
            Assert.DoesNotContain(stopped, cycle => cycle.DisplayName == "execute_agent_loop_cycle" && cycle.SpanId == span.ParentSpanId);
            Assert.Empty(first.Metrics!.ToolMetrics);
            Assert.Empty(second.Metrics!.ToolMetrics);
            Assert.Equal(1, Assert.Single(agent.Metrics.ToolMetrics).Value.CallCount);
            Assert.DoesNotContain(first.Traces.Concat(second.Traces).SelectMany(cycle => cycle.Children), child => child.Name.StartsWith("Tool:", StringComparison.Ordinal));
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task RefusingAHookContinuationAtTheTurnLimitRecordsTheTerminalReason()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        await using var agent = new Agent(new() { Model = new TimingModel(false), MaxTurns = 1 });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("again"); return default; });
        var result = await agent.InvokeAsync("limited");
        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        var span = Assert.Single(stopped, value => value.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
        Assert.Equal(new[] { StopReasons.MaxTurns }, Assert.IsType<string[]>(span.GetTagItem("gen_ai.response.finish_reasons")));
    }
}
