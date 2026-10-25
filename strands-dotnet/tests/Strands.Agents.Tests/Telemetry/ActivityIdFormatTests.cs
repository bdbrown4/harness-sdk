using System.Collections.Concurrent;
using System.Diagnostics;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class ActivityIdFormatTests
{
    [Fact]
    public void HierarchicalCallerCannotEraseOperationIdentityOrChangeTheProcessDefaults()
    {
        var originalFormat = Activity.DefaultIdFormat;
        var originalForce = Activity.ForceDefaultIdFormat;
        using var caller = new Activity("hierarchical caller").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        using var invocation = tracer.StartAgentSpan("portable");
        using var cycle = tracer.StartAgentLoopSpan("cycle");
        using var model = tracer.StartModelInvokeSpan();
        model.End();
        cycle.End();
        invocation.End();

        var root = Assert.Single(stopped, activity => activity.OperationName == "invoke_agent portable");
        var child = Assert.Single(stopped, activity => activity.OperationName == "execute_agent_loop_cycle");
        var request = Assert.Single(stopped, activity => activity.OperationName == "chat");
        Assert.All(stopped, activity =>
        {
            Assert.Equal(ActivityIdFormat.W3C, activity.IdFormat);
            Assert.NotEqual(default, activity.TraceId);
            Assert.NotEqual(default, activity.SpanId);
        });
        Assert.Equal(root.TraceId, child.TraceId);
        Assert.Equal(root.SpanId, child.ParentSpanId);
        Assert.Equal(child.SpanId, request.ParentSpanId);
        Assert.Same(caller, Activity.Current);
        Assert.Equal(ActivityIdFormat.Hierarchical, caller.IdFormat);
        Assert.Equal(originalFormat, Activity.DefaultIdFormat);
        Assert.Equal(originalForce, Activity.ForceDefaultIdFormat);
    }

    [Fact]
    public void W3CParentAndExplicitChildRetainTheirTraceAndSpanCorrelation()
    {
        using var caller = new Activity("W3C caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        using var parent = tracer.StartSpan("parent");
        using var child = tracer.StartSpan("child", parent);
        child.End();
        parent.End();

        var parentActivity = Assert.Single(stopped, activity => activity.OperationName == "parent");
        var childActivity = Assert.Single(stopped, activity => activity.OperationName == "child");
        Assert.Equal(caller.TraceId, parentActivity.TraceId);
        Assert.Equal(caller.SpanId, parentActivity.ParentSpanId);
        Assert.Equal(parentActivity.TraceId, childActivity.TraceId);
        Assert.Equal(parentActivity.SpanId, childActivity.ParentSpanId);
        Assert.Same(caller, Activity.Current);
    }

    [Theory]
    [InlineData(ActivitySamplingResult.None)]
    [InlineData(ActivitySamplingResult.PropagationData)]
    public void SamplingPolicyRemainsAuthoritative(ActivitySamplingResult sampling)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped, sampling);
        using var span = new Tracer().StartSpan("sampled", forceRoot: true);
        span.End();

        if (sampling == ActivitySamplingResult.None)
        {
            Assert.Null(span.Context);
            Assert.Empty(stopped);
        }
        else
        {
            var activity = Assert.Single(stopped);
            Assert.Equal(ActivityIdFormat.W3C, activity.IdFormat);
            Assert.NotEqual(default, activity.TraceId);
            Assert.NotEqual(default, activity.SpanId);
            Assert.False(activity.IsAllDataRequested);
            Assert.False(activity.Recorded);
        }
    }

    [Fact]
    public void ThrowingStartedListenerFinishesItsCreatedOperationAndRestoresAmbient()
    {
        using var caller = new Activity("caller").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        Activity? started = null;
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        listener.ActivityStarted = activity => { started = activity; throw new IOException("listener failed"); };

        using var span = new Tracer().StartSpan("broken observer");
        span.End();

        var closed = Assert.Single(stopped);
        Assert.Same(started, closed);
        Assert.Equal(ActivityIdFormat.W3C, closed.IdFormat);
        Assert.NotEqual(default, closed.TraceId);
        Assert.Same(caller, Activity.Current);
    }

    [Fact]
    public void DetachedRootHasUsableW3CIdentityWithoutChangingAmbient()
    {
        var originalFormat = Activity.DefaultIdFormat;
        var originalForce = Activity.ForceDefaultIdFormat;
        using var caller = new Activity("caller").SetIdFormat(ActivityIdFormat.Hierarchical).Start();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var span = new Tracer().StartSpan("detached", forceRoot: true);
        span.End();

        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityIdFormat.W3C, activity.IdFormat);
        Assert.NotEqual(default, activity.TraceId);
        Assert.NotEqual(default, activity.SpanId);
        Assert.Equal(default, activity.ParentSpanId);
        Assert.Same(caller, Activity.Current);
        Assert.Equal(originalFormat, Activity.DefaultIdFormat);
        Assert.Equal(originalForce, Activity.ForceDefaultIdFormat);
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped, ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
