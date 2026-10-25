using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[CollectionDefinition("Telemetry listeners", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry listeners")]
public sealed class TracerTests
{
    [Fact]
    public void LocalCyclesExistWithoutExportersAndParallelToolsEndTheirOwnTraces()
    {
        var tracer = new Tracer();
        using var root = tracer.StartAgentSpan("assistant");
        using var cycle = tracer.StartAgentLoopSpan("cycle-one");
        var model = tracer.StartModelInvokeSpan("model-one");
        var output = Message.Assistant("ready");
        model.End(output);
        var first = tracer.StartToolCallSpan(Call("first"));
        var second = tracer.StartToolCallSpan(Call("second"));
        second.End();

        Assert.NotNull(first.Trace);
        Assert.NotNull(second.Trace);
        Assert.Null(first.Trace.EndTime);
        Assert.NotNull(second.Trace.EndTime);
        first.End();
        cycle.End();
        var local = Assert.Single(tracer.Traces);
        Assert.Equal("Cycle 1", local.Name);
        Assert.Equal("cycle-one", local.Metadata["cycleId"]);
        Assert.Null(local.ParentId);
        Assert.Equal(new[] { "stream_messages", "Tool: calculate", "Tool: calculate" }, local.Children.Select(child => child.Name));
        Assert.Same(output, local.Children[0].Message);
        Assert.Equal(new[] { "first", "second" }, local.Children.Skip(1).Select(child => child.Metadata["toolUseId"]));
        Assert.All(local.Children, child => Assert.Equal(local.Id, child.ParentId));
        using var nextInvocation = tracer.StartAgentSpan("assistant");
        Assert.Empty(tracer.Traces);
        Assert.Equal(3, local.Children.Count);
    }

    [Fact]
    public void ActivitiesPreserveAmbientContextAndExplicitHierarchyUsageAndError()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer(new Dictionary<string, object?> { ["customer.key"] = "copied" });
        var root = tracer.StartAgentSpan("assistant", "agent-one", "model-one");
        Assert.Same(ambient, Activity.Current);
        var loop = tracer.StartAgentLoopSpan("cycle-one");
        var model = tracer.StartModelInvokeSpan("model-one");
        Assert.Same(ambient, Activity.Current);
        model.End(usage: new(5, 7, 2, 3, 17), error: new IOException("transport failed"));
        loop.End();
        root.End();

        Assert.Same(ambient, Activity.Current);
        var spans = stopped.ToArray();
        var rootActivity = Assert.Single(spans, item => item.OperationName == "invoke_agent assistant");
        var loopActivity = Assert.Single(spans, item => item.OperationName == "execute_agent_loop_cycle");
        var modelActivity = Assert.Single(spans, item => item.OperationName == "chat");
        Assert.Equal(ambient.SpanId, rootActivity.ParentSpanId);
        Assert.Equal(rootActivity.SpanId, loopActivity.ParentSpanId);
        Assert.Equal(loopActivity.SpanId, modelActivity.ParentSpanId);
        Assert.All(spans, item => Assert.Equal(ActivityKind.Internal, item.Kind));
        Assert.Equal("agent-one", rootActivity.GetTagItem("gen_ai.agent.id"));
        Assert.Equal("model-one", modelActivity.GetTagItem("gen_ai.request.model"));
        Assert.Equal("copied", modelActivity.GetTagItem("customer.key"));
        Assert.Equal(10L, modelActivity.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(7L, modelActivity.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(17L, modelActivity.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal(2L, modelActivity.GetTagItem("gen_ai.usage.cache_read.input_tokens"));
        Assert.Equal(ActivityStatusCode.Error, modelActivity.Status);
        Assert.Equal("transport failed", modelActivity.StatusDescription);
        Assert.Contains(modelActivity.Events, item => item.Name == "exception");
    }

    [Fact]
    public void DetachedSpanRetainsLinksWithoutInheritingAnEndedParent()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer();
        var parent = tracer.StartAgentSpan("assistant");
        var context = Assert.IsType<ActivityContext>(parent.Context);
        parent.End();
        var detached = tracer.StartSpan("memory.extract", parent, forceRoot: true, links: [new ActivityLink(context)]);
        detached.End();

        var span = Assert.Single(stopped, item => item.OperationName == "memory.extract");
        Assert.Equal(default, span.ParentSpanId);
        Assert.NotEqual(context.TraceId, span.TraceId);
        Assert.Equal(context, Assert.Single(span.Links).Context);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void FailingActivityListenerCannotBreakLocalTraceCollectionOrAmbientContext()
    {
        using var ambient = new Activity("request").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => throw new InvalidOperationException("broken exporter"),
        };
        ActivitySource.AddActivityListener(listener);
        var tracer = new Tracer();

        using var root = tracer.StartAgentSpan("assistant");
        using var cycle = tracer.StartAgentLoopSpan("cycle-one");
        using var model = tracer.StartModelInvokeSpan();
        model.End(Message.Assistant("answer"));

        Assert.Same(ambient, Activity.Current);
        Assert.Equal("answer", Assert.Single(Assert.Single(tracer.Traces).Children).Message!.Text);
    }

    [Fact]
    public void FailingAmbientChangeObserverCannotEscapeSpanStartOrCompletion()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("request").Start();
        EventHandler<ActivityChangedEventArgs> callback = (_, args) =>
        {
            if (args.Previous?.Source.Name == Tracer.ActivitySourceName && ReferenceEquals(args.Current, ambient))
                throw new InvalidOperationException("context observer failed");
        };
        Activity.CurrentChanged += callback;
        try
        {
            var tracer = new Tracer();
            TraceSpan? span = null;
            Assert.Null(Record.Exception(() => span = tracer.StartAgentSpan("assistant")));
            Assert.NotNull(span);
            Assert.Same(ambient, Activity.Current);
            Assert.Null(Record.Exception(() => span.End()));
            Assert.Same(ambient, Activity.Current);
        }
        finally { Activity.CurrentChanged -= callback; }
    }

    [Fact]
    public async Task ConcurrentToolsRetainIndependentCompletionAndCorrelation()
    {
        var tracer = new Tracer();
        using var invocation = tracer.StartAgentSpan("assistant");
        using var cycle = tracer.StartAgentLoopSpan("parallel-cycle");
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            using var span = tracer.StartToolCallSpan(Call(index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            span.End();
        })));

        var children = Assert.Single(tracer.Traces).Children;
        Assert.Equal(64, children.Count);
        Assert.Equal(64, children.Select(child => child.Metadata["toolUseId"]).Distinct().Count());
        Assert.All(children, child => Assert.NotNull(child.EndTime));
    }

    [Fact]
    public void StoppedListenerFailureStillEndsLocalCycleAndAllowsNextCycle()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _ => throw new InvalidOperationException("export failed"),
        };
        ActivitySource.AddActivityListener(listener);
        var tracer = new Tracer();
        using var invocation = tracer.StartAgentSpan("assistant");
        var first = tracer.StartAgentLoopSpan("first");
        Assert.Null(Record.Exception(() => first.End()));
        using var second = tracer.StartAgentLoopSpan("second");
        using var model = tracer.StartModelInvokeSpan();
        model.End(Message.Assistant("second output"));

        Assert.NotNull(tracer.Traces[0].EndTime);
        Assert.Empty(tracer.Traces[0].Children);
        Assert.Equal("second output", Assert.Single(tracer.Traces[1].Children).Message!.Text);
    }

    [Fact]
    public void CustomAttributeArraysAreCopiedAndInclusiveCacheCountsAreNotDoubleCounted()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var values = new[] { "original" };
        var attributes = new Dictionary<string, object?> { ["custom.values"] = values };
        var tracer = new Tracer(attributes);
        values[0] = "mutated";
        attributes["custom.values"] = new[] { "replaced" };
        using var model = tracer.StartModelInvokeSpan();
        model.End(usage: new(10, 7, 2, 3, 17));

        var activity = Assert.Single(stopped);
        Assert.Equal(new[] { "original" }, Assert.IsType<string[]>(activity.GetTagItem("custom.values")));
        Assert.Equal(10L, activity.GetTagItem("gen_ai.usage.input_tokens"));
    }

    [Fact]
    public void InvalidUsageCannotHideTheApplicationErrorOrPreventSpanCompletion()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        var span = tracer.StartModelInvokeSpan();
        var error = new IOException("original model error");

        Assert.Null(Record.Exception(() => span.End(usage: new(long.MaxValue, 1, ReportedTotalTokens: 1), error: error)));

        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("original model error", activity.StatusDescription);
        Assert.Contains(activity.Events, item => item.Name == "exception");
        Assert.NotNull(span.Trace!.EndTime);
    }

    [Fact]
    public void ExplicitSpanContextParentsApplicationWorkAndPreservesOriginalFailure()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var span = new Tracer().StartModelInvokeSpan();
        var expected = Assert.IsType<ActivityContext>(span.Context);
        var error = new IOException("original failure");

        var actual = Record.Exception(() => span.WithContext<int>(() =>
        {
            using var work = new Activity("provider request").Start();
            Assert.Equal(expected.SpanId, work.ParentSpanId);
            throw error;
        }));

        Assert.Same(error, actual);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task ExplicitAsyncSpanContextFlowsThroughAwaitAndRestoresTheCaller()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var span = new Tracer().StartModelInvokeSpan();
        var expected = Assert.IsType<ActivityContext>(span.Context);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = span.WithContextAsync(async () =>
        {
            entered.SetResult(true);
            await release.Task.ConfigureAwait(false);
            Assert.Equal(expected.SpanId, Activity.Current!.SpanId);
            using var work = new Activity("provider request").Start();
            Assert.Equal(expected.SpanId, work.ParentSpanId);
            return 7;
        });
        await entered.Task;
        Assert.Same(ambient, Activity.Current);
        release.SetResult(true);

        Assert.Equal(7, await running);
        Assert.Same(ambient, Activity.Current);
    }

    private static ToolUseBlock Call(string id) => new(id, "calculate", JsonSerializer.SerializeToElement(new { value = 7 }));
    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
