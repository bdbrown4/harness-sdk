using System.Runtime.CompilerServices;
using Strands.Agents.Middleware;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

public sealed class ModelPerformanceMetadataTests
{
    [Fact]
    public async Task ProviderTimingReachesAuthoritativeModelMiddlewareResult()
    {
        ModelPerformanceMetrics? observed = null;
        var middleware = new MiddlewareRegistry();
        middleware.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) =>
        {
            observed = result.PerformanceMetrics;
            return new(result);
        });
        await using var agent = new Agent(new() { Model = new TimingModel(), Middleware = middleware });
        Assert.Equal("ok", (await agent.InvokeAsync("timed")).ToString());
        Assert.Equal(Timing(7, 2), observed);
    }

    [Fact]
    public void AccumulatorRetainsLatestReportedTimingAlongsideAdditiveUsage()
    {
        var accumulator = new ModelStreamAccumulator();
        accumulator.Process(new ModelMetadataEvent(new(2)) { PerformanceMetrics = Timing(7, 2) });
        accumulator.Process(new ModelMetadataEvent(new(3)) { PerformanceMetrics = Timing(9, 4) });
        Assert.Equal(5, accumulator.Usage.InputTokens);
        Assert.Equal(Timing(9, 4), accumulator.PerformanceMetrics);
    }

    [Fact]
    public void UsageOnlyMetadataDoesNotEraseEarlierTiming()
    {
        var accumulator = new ModelStreamAccumulator();
        accumulator.Process(new ModelMetadataEvent(new(2)) { PerformanceMetrics = Timing(7, 2) });
        accumulator.Process(new ModelMetadataEvent(new(3)));
        Assert.Equal(5, accumulator.Usage.InputTokens);
        Assert.Equal(Timing(7, 2), accumulator.PerformanceMetrics);
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(1L, -1L)]
    public void InvalidProviderTimingCannotPartiallyCommitUsageOrTiming(long latency, long? firstToken)
    {
        var accumulator = new ModelStreamAccumulator();
        accumulator.Process(new ModelMetadataEvent(new(5)) { PerformanceMetrics = Timing(3, 1) });
        Assert.Throws<ModelException>(() => accumulator.Process(new ModelMetadataEvent(new(7)) { PerformanceMetrics = Timing(latency, firstToken) }));
        Assert.Equal(5, accumulator.Usage.InputTokens);
        Assert.Equal(Timing(3, 1), accumulator.PerformanceMetrics);
    }

    [Fact]
    public void ExplicitZeroTimingIsRetainedAsReportedData()
    {
        var accumulator = new ModelStreamAccumulator();
        accumulator.Process(new ModelMetadataEvent(new()) { PerformanceMetrics = Timing(0, 0) });
        Assert.Equal(Timing(0, 0), accumulator.PerformanceMetrics);
    }

    [Fact]
    public void ImplicitTotalOverflowCannotPartiallyCommitUsageOrTiming()
    {
        var accumulator = new ModelStreamAccumulator();
        accumulator.Process(new ModelMetadataEvent(new(long.MaxValue)) { PerformanceMetrics = Timing(3, null) });
        Assert.Throws<ModelException>(() => accumulator.Process(new ModelMetadataEvent(new(OutputTokens: 1)) { PerformanceMetrics = Timing(9, 2) }));
        Assert.Equal(long.MaxValue, accumulator.Usage.InputTokens);
        Assert.Equal(0, accumulator.Usage.OutputTokens);
        Assert.Equal(Timing(3, null), accumulator.PerformanceMetrics);
    }

    [Theory]
    [InlineData(-1L, null)]
    [InlineData(1L, -1L)]
    public void MiddlewareCannotSubstituteInvalidProviderTiming(long latency, long? firstToken)
    {
        var result = new InvokeModelResult(new(Role.Assistant, [new TextBlock("ok")]), StopReasons.EndTurn, new())
        { PerformanceMetrics = Timing(latency, firstToken) };
        Assert.Throws<ArgumentOutOfRangeException>(() => result.Snapshot());
    }

    private static ModelPerformanceMetrics Timing(long latency, long? firstToken) =>
        new(TimeSpan.FromMilliseconds(latency), firstToken is { } first ? TimeSpan.FromMilliseconds(first) : null);

    private sealed class TimingModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "ok");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            yield return new ModelMetadataEvent(new(2, 3)) { PerformanceMetrics = Timing(7, 2) };
        }
    }
}
