using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry.Metrics;

public sealed class AgentMeterTests
{
    [Fact]
    public void EmptyMetricsAreSerializableAndHaveNoInventedContextOrInvocation()
    {
        var snapshot = new AgentMeter().Metrics;
        Assert.Equal(0, snapshot.CycleCount);
        Assert.Null(snapshot.LatestAgentInvocation);
        Assert.Null(snapshot.LatestContextSize);
        Assert.Null(snapshot.ProjectedContextSize);
        Assert.Empty(snapshot.AgentInvocations);
        Assert.Empty(snapshot.ToolMetrics);
        Assert.Equal(TimeSpan.Zero, snapshot.AverageCycleTime);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        Assert.Equal(0, json.RootElement.GetProperty("CycleCount").GetInt32());
    }

    [Fact]
    public void AccumulatesCyclesUsageLatencyAndExplicitProviderContextAccounting()
    {
        var meter = new AgentMeter();
        meter.StartNewInvocation();
        var cycle = meter.StartCycle();
        cycle.Update(new Usage(10, 2, 5, 3, 20), new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10)));
        cycle.End(TimeSpan.FromMilliseconds(80));

        var snapshot = meter.Metrics;
        Assert.Equal(1, snapshot.CycleCount);
        Assert.Equal("cycle-1", Assert.Single(Assert.Single(snapshot.AgentInvocations).Cycles).CycleId);
        Assert.Equal(new Usage(10, 2, 5, 3, 20), snapshot.AccumulatedUsage);
        Assert.Equal(snapshot.AccumulatedUsage, snapshot.LatestAgentInvocation!.Usage);
        Assert.Equal(TimeSpan.FromMilliseconds(50), snapshot.AccumulatedMetrics.Latency);
        Assert.Null(snapshot.AccumulatedMetrics.TimeToFirstToken);
        Assert.Equal(18, snapshot.LatestContextSize);
        Assert.Equal(20, snapshot.ProjectedContextSize);
        Assert.Equal(TimeSpan.FromMilliseconds(80), snapshot.TotalDuration);
        Assert.Equal(snapshot.TotalDuration, snapshot.AverageCycleTime);
    }

    [Fact]
    public void AnOlderCycleKeepsItsOriginalInvocationWhenCompletedOutOfOrder()
    {
        var meter = new AgentMeter();
        meter.StartNewInvocation();
        var first = meter.StartCycle();
        meter.StartNewInvocation();
        var second = meter.StartCycle();
        second.Update(new Usage(3, 4));
        second.End(TimeSpan.FromMilliseconds(20));
        first.Update(new Usage(8, 2));
        first.End(TimeSpan.FromMilliseconds(60));
        first.End(TimeSpan.FromHours(1));

        var snapshot = meter.Metrics;
        Assert.Equal(2, snapshot.AgentInvocations.Count);
        Assert.Equal(new Usage(8, 2), snapshot.AgentInvocations[0].Usage);
        Assert.Equal(TimeSpan.FromMilliseconds(60), Assert.Single(snapshot.AgentInvocations[0].Cycles).Duration);
        Assert.Equal(new Usage(3, 4), snapshot.AgentInvocations[1].Usage);
        Assert.Equal(TimeSpan.FromMilliseconds(20), Assert.Single(snapshot.AgentInvocations[1].Cycles).Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(80), snapshot.TotalDuration);
    }

    [Fact]
    public void HistoryEvictionRetainsLifetimeTotalsAndPreviouslyReturnedSnapshots()
    {
        var meter = new AgentMeter(maximumInvocationHistory: 1);
        meter.StartNewInvocation();
        var first = meter.StartCycle();
        first.Update(new Usage(2, 1));
        first.End(TimeSpan.FromMilliseconds(10));
        var prior = meter.Metrics;

        meter.StartNewInvocation();
        var second = meter.StartCycle();
        second.Update(new Usage(5, 3));
        second.End(TimeSpan.FromMilliseconds(30));

        var current = meter.Metrics;
        Assert.Single(current.AgentInvocations);
        Assert.Equal(new Usage(7, 4), current.AccumulatedUsage);
        Assert.Equal(TimeSpan.FromMilliseconds(40), current.TotalDuration);
        Assert.Equal(2, current.CycleCount);
        Assert.Equal(new Usage(2, 1), prior.AccumulatedUsage);
        Assert.Equal(new Usage(2, 1), Assert.Single(prior.AgentInvocations).Usage);
        Assert.Equal("cycle-1", Assert.Single(prior.AgentInvocations[0].Cycles).CycleId);
    }

    [Fact]
    public void CycleStartedWithoutInvocationNeverAttachesToAnUnrelatedLaterInvocation()
    {
        var meter = new AgentMeter();
        var orphan = meter.StartCycle();
        meter.StartNewInvocation();
        orphan.Update(new Usage(5, 2));
        orphan.End(TimeSpan.FromMilliseconds(20));

        var snapshot = meter.Metrics;
        Assert.Equal(new Usage(5, 2), snapshot.AccumulatedUsage);
        Assert.Equal(new Usage(), snapshot.LatestAgentInvocation!.Usage);
        Assert.Empty(snapshot.LatestAgentInvocation.Cycles);
        Assert.Equal(TimeSpan.FromMilliseconds(20), snapshot.TotalDuration);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(12L, 10)]
    [InlineData(20L, 18)]
    public void CachedPromptTokensFollowExplicitProviderTotalAccounting(long? total, long prompt)
    {
        var meter = new AgentMeter();
        meter.UpdateCycle(new Usage(10, 2, 5, 3, total));
        Assert.Equal(prompt, meter.Metrics.LatestContextSize);
        Assert.Equal(prompt + 2, meter.Metrics.ProjectedContextSize);
    }

    [Fact]
    public void DefaultHistoryRetainsFiftyAndUnboundedModeRetainsEveryInvocation()
    {
        var bounded = new AgentMeter();
        var unbounded = new AgentMeter(null);
        for (var index = 0; index < 53; index++)
        {
            bounded.StartNewInvocation();
            unbounded.StartNewInvocation();
            bounded.StartCycle().End(TimeSpan.Zero);
            unbounded.StartCycle().End(TimeSpan.Zero);
        }
        Assert.Equal(50, bounded.Metrics.AgentInvocations.Count);
        Assert.Equal("cycle-4", bounded.Metrics.AgentInvocations[0].Cycles[0].CycleId);
        Assert.Equal(53, bounded.Metrics.CycleCount);
        Assert.Equal(53, unbounded.Metrics.AgentInvocations.Count);
    }

    [Fact]
    public void EvictedInvocationCanFinishWithoutChangingTheRetainedInvocation()
    {
        var meter = new AgentMeter(1);
        meter.StartNewInvocation();
        var old = meter.StartCycle();
        meter.StartNewInvocation();
        old.Update(new Usage(7, 1));
        old.End(TimeSpan.FromSeconds(1));
        Assert.Equal(new Usage(7, 1), meter.Metrics.AccumulatedUsage);
        Assert.Equal(new Usage(), meter.Metrics.LatestAgentInvocation!.Usage);
        Assert.Empty(meter.Metrics.LatestAgentInvocation.Cycles);
    }

    [Fact]
    public void ReadOnlySnapshotsCannotMutateCollectorOrNestedHistory()
    {
        var meter = new AgentMeter();
        meter.StartNewInvocation();
        var cycle = meter.StartCycle();
        var before = meter.Metrics;
        cycle.Update(new Usage(3, 1));
        cycle.End(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.Zero, before.AgentInvocations[0].Cycles[0].Duration);
        Assert.Equal(new Usage(), before.AgentInvocations[0].Usage);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<AgentInvocationMetrics>>(before.AgentInvocations).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<AgentLoopMetrics>>(before.AgentInvocations[0].Cycles).Clear());
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IDictionary<string, ToolMetrics>>(before.ToolMetrics).Clear());
    }

    [Fact]
    public void ToolStatisticsRetainLatestDetachedJsonAndPriorSnapshots()
    {
        var meter = new AgentMeter();
        using (var input = JsonDocument.Parse("{\"value\":7}"))
        using (var metadata = JsonDocument.Parse("{\"opaque\":true}"))
        {
            meter.RecordToolCall(new("one", "calculate", input.RootElement) { ProviderMetadata = metadata.RootElement }, TimeSpan.FromMilliseconds(20), true);
        }
        var before = meter.Metrics;
        using (var input = JsonDocument.Parse("{\"value\":9}"))
            meter.RecordToolCall(new("two", "calculate", input.RootElement), TimeSpan.FromMilliseconds(60), false);
        var current = meter.Metrics.ToolMetrics["calculate"];
        Assert.Equal(2, current.CallCount);
        Assert.Equal(1, current.SuccessCount);
        Assert.Equal(1, current.ErrorCount);
        Assert.Equal(TimeSpan.FromMilliseconds(80), current.TotalTime);
        Assert.Equal(TimeSpan.FromMilliseconds(40), current.AverageTime);
        Assert.Equal(0.5, current.SuccessRate);
        Assert.Equal(9, current.LastToolCall.Input.GetProperty("value").GetInt32());
        Assert.Equal("two", current.LastToolCall.ToolUseId);
        Assert.Equal(7, before.ToolMetrics["calculate"].LastToolCall.Input.GetProperty("value").GetInt32());
        Assert.True(before.ToolMetrics["calculate"].LastToolCall.ProviderMetadata!.Value.GetProperty("opaque").GetBoolean());
        Assert.Equal(1, before.ToolMetrics["calculate"].CallCount);
    }

    [Fact]
    public async Task ConcurrentCyclesAndToolCallsHaveExactTotalsAndUniqueIdentities()
    {
        var meter = new AgentMeter();
        meter.StartNewInvocation();
        using var input = JsonDocument.Parse("{}");
        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(() =>
        {
            var cycle = meter.StartCycle();
            cycle.Update(new Usage(2, 1));
            meter.RecordToolCall(new(index.ToString(), "parallel", input.RootElement), TimeSpan.FromMilliseconds(1), index % 2 == 0);
            cycle.End(TimeSpan.FromMilliseconds(2));
        })));
        var snapshot = meter.Metrics;
        Assert.Equal(200, snapshot.CycleCount);
        Assert.Equal(200, snapshot.LatestAgentInvocation!.Cycles.Select(cycle => cycle.CycleId).Distinct().Count());
        Assert.Equal(new Usage(400, 200), snapshot.AccumulatedUsage);
        Assert.Equal(new Usage(400, 200), snapshot.LatestAgentInvocation.Usage);
        Assert.Equal(TimeSpan.FromMilliseconds(400), snapshot.TotalDuration);
        Assert.Equal(100, snapshot.ToolMetrics["parallel"].SuccessCount);
        Assert.Equal(100, snapshot.ToolMetrics["parallel"].ErrorCount);
    }

    [Fact]
    public void OverflowDoesNotPartiallyCommitUsageLatencyOrContext()
    {
        var meter = new AgentMeter();
        meter.StartNewInvocation();
        var cycle = meter.StartCycle();
        cycle.Update(new Usage(long.MaxValue - 1), new(TimeSpan.FromMilliseconds(5)));
        var before = meter.Metrics;
        Assert.Throws<OverflowException>(() => cycle.Update(new Usage(3), new(TimeSpan.FromMilliseconds(20))));
        Assert.Equal(before.AccumulatedUsage, meter.Metrics.AccumulatedUsage);
        Assert.Equal(before.LatestAgentInvocation!.Usage, meter.Metrics.LatestAgentInvocation!.Usage);
        Assert.Equal(before.AccumulatedMetrics, meter.Metrics.AccumulatedMetrics);
        Assert.Equal(before.LatestContextSize, meter.Metrics.LatestContextSize);
    }

    [Fact]
    public void TimingOnlyMetadataDoesNotInventTokenContext()
    {
        var meter = new AgentMeter();
        meter.UpdateCycle(metrics: new(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(TimeSpan.FromMilliseconds(20), meter.Metrics.AccumulatedMetrics.Latency);
        Assert.Null(meter.Metrics.LatestContextSize);
        Assert.Null(meter.Metrics.ProjectedContextSize);
    }

    [Fact]
    public void InvalidDataDoesNotMutateCollectedMetrics()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentMeter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentMeter(-1));
        var meter = new AgentMeter();
        var cycle = meter.StartCycle();
        Assert.Throws<ArgumentOutOfRangeException>(() => cycle.End(TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => cycle.Update(new Usage(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => cycle.Update(metrics: new(TimeSpan.FromTicks(-1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => cycle.Update(metrics: new(TimeSpan.Zero, TimeSpan.FromTicks(-1))));
        Assert.Equal(new Usage(), meter.Metrics.AccumulatedUsage);
        Assert.Equal(TimeSpan.Zero, meter.Metrics.TotalDuration);
        cycle.End(TimeSpan.FromMilliseconds(2));
        Assert.Equal(TimeSpan.FromMilliseconds(2), meter.Metrics.TotalDuration);
    }
}
