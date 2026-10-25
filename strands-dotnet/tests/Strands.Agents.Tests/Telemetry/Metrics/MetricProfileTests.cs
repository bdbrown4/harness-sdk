using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry.Metrics;

public sealed class MetricProfileTests
{
    [Theory]
    [InlineData(ContextSizePolicy.LastReportedUsage)]
    [InlineData(ContextSizePolicy.LatestInvocationCycle)]
    public async Task AgentBoundaryUsesTheSelectedContextPolicy(ContextSizePolicy policy)
    {
        await using var agent = new Agent(new() { Model = new CountingModel(), MetricsContextSizePolicy = policy });
        await agent.InvokeAsync("first");
        long? context = -1;
        using var registration = agent.Hooks.Register<BeforeInvocationEvent>((_, _) => { context = agent.Metrics.LatestContextSize; return default; });
        await agent.InvokeAsync("second");
        Assert.Equal(policy == ContextSizePolicy.LastReportedUsage ? (long?)1 : null, context);
        Assert.Equal(new Usage(2, 4), agent.Metrics.AccumulatedUsage);
        Assert.Equal(new Usage(1, 2), agent.Metrics.LatestAgentInvocation!.Usage);
    }

    [Theory]
    [InlineData(ContextSizePolicy.LastReportedUsage)]
    [InlineData(ContextSizePolicy.LatestInvocationCycle)]
    public void InvocationAndCycleBoundariesSelectTheRequestedContextSource(ContextSizePolicy policy)
    {
        var meter = new AgentMeter(null, null, policy);
        meter.StartNewInvocation();
        using (var first = meter.StartCycle()) first.Update(new(5, 7));
        meter.StartNewInvocation();
        var expected = policy == ContextSizePolicy.LastReportedUsage ? (long?)5 : null;
        Assert.Equal(expected, meter.Metrics.LatestContextSize);
        Assert.Equal(expected is null ? null : (long?)12, meter.Metrics.ProjectedContextSize);
        using var pending = meter.StartCycle();
        Assert.Equal(policy == ContextSizePolicy.LastReportedUsage ? 5 : 0, meter.Metrics.LatestContextSize);
        pending.Update(new(11, 13));
        Assert.Equal(11, meter.Metrics.LatestContextSize);
        Assert.Equal(24, meter.Metrics.ProjectedContextSize);
        Assert.Equal(new Usage(16, 20), meter.Metrics.AccumulatedUsage);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(null)]
    public async Task AgentHistoryRetentionIsConfigurableWithoutResettingTotals(int? maximum)
    {
        await using var agent = new Agent(new()
        {
            Model = new CountingModel(),
            MaximumInvocationHistory = maximum,
            MetricsContextSizePolicy = ContextSizePolicy.LatestInvocationCycle
        });
        for (var index = 0; index < 51; index++) await agent.InvokeAsync("next");
        Assert.Equal(maximum ?? 51, agent.Metrics.AgentInvocations.Count);
        Assert.Equal(51, agent.Metrics.CycleCount);
        Assert.Equal(new Usage(51, 102), agent.Metrics.AccumulatedUsage);
        Assert.Equal(new Usage(1, 2), agent.Metrics.LatestAgentInvocation!.Usage);
    }

    [Fact]
    public void InvalidProfilesFailBeforeAnyConstructionCallbackRuns()
    {
        var callbacks = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentMeter(null, null, (ContextSizePolicy)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new()
        {
            Model = new CountingModel(),
            MaximumInvocationHistory = 0,
            Tools = [new FunctionTool(new("work", "Work", System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new("result"))],
            ToolFilter = _ => { callbacks++; return true; }
        }));
        Assert.Equal(0, callbacks);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Model = new CountingModel(), MetricsContextSizePolicy = (ContextSizePolicy)99 }));
    }

    private sealed class CountingModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            yield return new ModelMetadataEvent(new(1, 2));
        }
    }
}
