using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class MetadataUsageReplacementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task AModelDecoratorCanReplaceUnknownUsageWithAnAuthoritativeMeasurement(long tokens)
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var unknown = new ModelMetadataEvent(new()) { UsageReported = false, PerformanceMetrics = new(TimeSpan.FromMilliseconds(7)) };
        var source = new MetadataModel(new(new(3, 5)), unknown);
        await using var agent = new Agent(new() { Model = new UsageDecorator(source, new(tokens, tokens)) });

        await agent.InvokeAsync("first measurement");
        var result = await agent.InvokeAsync("replacement measurement");

        Assert.False(unknown.UsageReported);
        Assert.Equal(new Usage(), unknown.Usage);
        Assert.Equal(tokens, result.Metrics!.LatestContextSize);
        Assert.Equal(new Usage(3 + tokens, 5 + tokens), result.Metrics.AccumulatedUsage);
        Assert.Equal(TimeSpan.FromMilliseconds(7), result.Metrics.AccumulatedMetrics.Latency);
        var model = spans.Last(value => value.DisplayName == "chat");
        Assert.Equal(tokens, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(tokens, model.GetTagItem("gen_ai.usage.output_tokens"));
    }

    [Fact]
    public async Task CopyingOnlyPerformanceMetadataPreservesUnknownUsageAndThePreviousContextEstimate()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var unknown = new ModelMetadataEvent(new()) { UsageReported = false };
        var timed = unknown with { PerformanceMetrics = new(TimeSpan.FromMilliseconds(11)) };
        await using var agent = new Agent(new() { Model = new MetadataModel(new(new(3, 5)), timed) });

        await agent.InvokeAsync("known");
        var result = await agent.InvokeAsync("unknown");

        Assert.False(timed.UsageReported);
        Assert.Equal(3, result.Metrics!.LatestContextSize);
        Assert.Equal(new Usage(3, 5), result.Metrics.AccumulatedUsage);
        Assert.Equal(TimeSpan.FromMilliseconds(11), result.Metrics.AccumulatedMetrics.Latency);
        Assert.Null(spans.Last(value => value.DisplayName == "chat").GetTagItem("gen_ai.usage.input_tokens"));
    }

    [Fact]
    public async Task ExplicitlyHidingNonzeroReplacementUsageStillRejectsTheTurnBeforeAssistantCommit()
    {
        var unknown = new ModelMetadataEvent(new()) { UsageReported = false };
        var invalid = unknown with { Usage = new(9, 11), UsageReported = false };
        await using var agent = new Agent(new() { Model = new MetadataModel(invalid) });

        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("invalid usage"));

        Assert.Equal(new Usage(), agent.Metrics.AccumulatedUsage);
        Assert.Single(agent.Messages);
        Assert.Equal(Role.User, agent.Messages[0].Role);
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

    private sealed class UsageDecorator(IModel source, Usage replacement) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in source.StreamAsync(request, cancellationToken))
                yield return item is ModelMetadataEvent { UsageReported: false } metadata ? metadata with { Usage = replacement } : item;
        }
    }

    private sealed class MetadataModel(params ModelMetadataEvent[] metadata) : IModel
    {
        private int _index;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            yield return metadata[_index++];
        }
    }
}
