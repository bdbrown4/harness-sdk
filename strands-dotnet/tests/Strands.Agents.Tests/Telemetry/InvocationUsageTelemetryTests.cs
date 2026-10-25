using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class InvocationUsageTelemetryTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 0)]
    public async Task AgentSpansSelectLifetimeOrLatestInvocationUsageWithoutDependingOnRetainedHistory(bool latestInvocation, int historyLimit)
    {
        using var environment = new ConventionEnvironment(latestInvocation);
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await using var agent = new Agent(new() { Model = new UsageModel(), MaximumInvocationHistory = historyLimit == 0 ? null : historyLimit });
        await agent.InvokeAsync("first");
        var result = await agent.InvokeAsync("second");
        var invocations = spans.Where(value => value.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, invocations.Length);
        Assert.Equal(2L, invocations[0].GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(latestInvocation ? 2L : 4L, invocations[1].GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(latestInvocation ? 3L : 6L, invocations[1].GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(new Usage(4, 6), result.Metrics!.AccumulatedUsage);
        Assert.Equal(historyLimit == 0 ? 2 : 1, result.Metrics.AgentInvocations.Count);
    }

    [Fact]
    public async Task DefaultAgentSpanRetainsLifetimeUsageWhenALaterInvocationFails()
    {
        using var environment = new ConventionEnvironment(false);
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        var failure = new IOException("second provider call failed");
        await using var agent = new Agent(new() { Model = new UsageModel(failure) });
        await agent.InvokeAsync("first");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("second")));
        var invocation = spans.Last(value => value.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
        Assert.Equal(ActivityStatusCode.Error, invocation.Status);
        Assert.Equal(2L, invocation.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(3L, invocation.GetTagItem("gen_ai.usage.output_tokens"));
    }

    [Fact]
    public async Task LatestInvocationTokenOptInIsCapturedWithTheOtherConventionsAtConstruction()
    {
        using var environment = new ConventionEnvironment(true);
        var spans = new ConcurrentQueue<Activity>();
        using var listener = Listen(spans);
        await using var agent = new Agent(new() { Model = new UsageModel() });
        Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", null);
        await agent.InvokeAsync("first");
        await agent.InvokeAsync("second");
        var invocation = spans.Last(value => value.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal));
        Assert.Equal(2L, invocation.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(3L, invocation.GetTagItem("gen_ai.usage.output_tokens"));
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

    private sealed class ConventionEnvironment : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN");
        internal ConventionEnvironment(bool latestInvocation) => Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", latestInvocation ? "gen_ai_use_latest_invocation_tokens" : null);
        public void Dispose() => Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", _previous);
    }

    private sealed class UsageModel(Exception? secondFailure = null) : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (++_calls == 2 && secondFailure is { } failure) throw failure;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMetadataEvent(new(2, 3));
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
