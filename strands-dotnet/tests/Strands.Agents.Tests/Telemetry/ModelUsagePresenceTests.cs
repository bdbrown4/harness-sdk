using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents.Middleware;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class ModelUsagePresenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task AssigningAuthoritativeMiddlewareUsageMarksItAsReported(long tokens)
    {
        var registry = new MiddlewareRegistry();
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => new(result with { Usage = new(tokens, tokens) }));
        await using var agent = new Agent(new() { Model = new PresenceModel(null), Middleware = registry });
        var result = await agent.InvokeAsync("authoritative usage");
        Assert.Equal(new Usage(tokens, tokens), result.Usage);
        Assert.Equal(tokens, result.Metrics!.LatestContextSize);
        Assert.Equal(new Usage(tokens, tokens), result.Metrics.AccumulatedUsage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportedZeroAndTimingOnlyMetadataRemainDistinct(bool usageReported)
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await using var agent = new Agent(new() { Model = new PresenceModel(usageReported ? new Usage() : null) });
        var result = await agent.InvokeAsync("metadata");
        Assert.Equal(usageReported ? (long?)0 : null, result.Metrics!.LatestContextSize);
        Assert.Equal(TimeSpan.FromMilliseconds(7), result.Metrics.AccumulatedMetrics.Latency);
        var model = Assert.Single(spans, value => value.DisplayName == "chat");
        Assert.Equal(usageReported ? (object)0L : null, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(usageReported ? (object)0L : null, model.GetTagItem("gen_ai.usage.output_tokens"));
    }

    [Fact]
    public async Task MissingMetadataDoesNotInventAProviderUsageMeasurement()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await using var agent = new Agent(new() { Model = new PresenceModel(null, emitMetadata: false) });
        var result = await agent.InvokeAsync("no metadata");
        Assert.Null(result.Metrics!.LatestContextSize);
        Assert.Null(result.Metrics.ProjectedContextSize);
        Assert.Null(Assert.Single(spans, value => value.DisplayName == "chat").GetTagItem("gen_ai.usage.total_tokens"));
    }

    [Fact]
    public async Task MiddlewareAndAfterHooksPreserveTimingOnlyUsagePresence()
    {
        var registry = new MiddlewareRegistry();
        var reported = new List<bool>();
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) =>
        {
            reported.Add(result.UsageReported);
            return new(result with { Message = Message.Assistant("transformed") });
        });
        await using var agent = new Agent(new()
        {
            Model = new SequenceModel(new PresenceModel(new(5, 7)), new PresenceModel(null)),
            Middleware = registry
        });
        agent.Hooks.Register<AfterModelCallEvent>((value, _) => { value.Message = Message.Assistant("after hook"); return default; });
        await agent.InvokeAsync("reported");
        var result = await agent.InvokeAsync("timing only");
        Assert.Equal(new[] { true, false }, reported);
        Assert.Equal("after hook", result.ToString());
        Assert.Equal(new Usage(5, 7), result.Metrics!.AccumulatedUsage);
        Assert.Equal(5, result.Metrics.LatestContextSize);
        Assert.Equal(TimeSpan.FromMilliseconds(14), result.Metrics.AccumulatedMetrics.Latency);
    }

    [Fact]
    public async Task MiddlewareCannotHideNonzeroUsageBehindAnUnreportedFlag()
    {
        var registry = new MiddlewareRegistry();
        registry.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => new(result with { Usage = new(9, 11), UsageReported = false }));
        await using var agent = new Agent(new() { Model = new PresenceModel(new()), Middleware = registry });
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync("invalid middleware result"));
        Assert.Equal(0, agent.Metrics.AccumulatedUsage.TotalTokens);
        Assert.Single(agent.Messages);
    }

    [Fact]
    public async Task AnExplicitZeroReportReplacesThePreviousContextEstimate()
    {
        await using var agent = new Agent(new() { Model = new SequenceModel(new PresenceModel(new(3, 5)), new PresenceModel(new())) });
        await agent.InvokeAsync("positive");
        var result = await agent.InvokeAsync("measured zero");
        Assert.Equal(0, result.Metrics!.LatestContextSize);
        Assert.Equal(0, result.Metrics.ProjectedContextSize);
        Assert.Equal(new Usage(3, 5), result.Metrics.AccumulatedUsage);
    }

    [Fact]
    public async Task TimingOnlyTrailingMetadataRetainsEarlierUsage()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await using var agent = new Agent(new() { Model = new PresenceModel(new(5, 7), trailingTimingOnly: true) });
        var result = await agent.InvokeAsync("trailing metrics");
        Assert.Equal(new Usage(5, 7), result.Metrics!.AccumulatedUsage);
        Assert.Equal(5, result.Metrics.LatestContextSize);
        Assert.Equal(TimeSpan.FromMilliseconds(9), result.Metrics.AccumulatedMetrics.Latency);
        Assert.Equal(5L, Assert.Single(spans, value => value.DisplayName == "chat").GetTagItem("gen_ai.usage.input_tokens"));
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class SequenceModel(params IModel[] models) : IModel
    {
        private int _index;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => models[_index++].StreamAsync(request, cancellationToken);
    }

    private sealed class PresenceModel(Usage? usage, bool emitMetadata = true, bool trailingTimingOnly = false) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            if (emitMetadata)
                yield return new ModelMetadataEvent(usage ?? new()) { UsageReported = usage is not null, PerformanceMetrics = new(TimeSpan.FromMilliseconds(7)) };
            if (trailingTimingOnly)
                yield return new ModelMetadataEvent(new()) { UsageReported = false, PerformanceMetrics = new(TimeSpan.FromMilliseconds(9)) };
        }
    }
}
