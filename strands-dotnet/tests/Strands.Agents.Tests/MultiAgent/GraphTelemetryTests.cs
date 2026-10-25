using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.MultiAgent;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.MultiAgent;

[Collection("Telemetry listeners")]
public sealed class GraphTelemetryTests
{
    [Fact]
    public async Task TypeScriptAutomaticallyParentsAFunctionVisitAndReportsExactAggregateUsage()
    {
        using var capture = new MultiAgentTelemetryCapture();
        ActivitySpanId bodyParent = default;
        var graph = new Graph(new()
        {
            Id = "graph-telemetry",
            Nodes = [new FunctionNode("function", (_, _, _) => { bodyParent = Activity.Current?.SpanId ?? default; return MultiAgentTelemetryCapture.Output(new(4, 2)); })]
        });

        var result = await graph.InvokeAsync("task");

        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph graph-telemetry");
        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node function");
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal(capture.Ambient.SpanId, span.ParentSpanId);
        Assert.Equal(span.SpanId, node.ParentSpanId);
        Assert.Equal(node.SpanId, bodyParent);
        Assert.Equal("\"task\"", span.GetTagItem("gen_ai.agent.input"));
        Assert.Equal(result.Usage.TotalTokens, span.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal("completed", node.GetTagItem("gen_ai.agent.status"));
        Assert.Equal(Assert.Single(result.Results).Duration.TotalMilliseconds, node.GetTagItem("gen_ai.agent.execution_time"));
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Fact]
    public async Task PythonUsesOneActiveClientSpanWithoutInventingAggregateEndAttributes()
    {
        using var capture = new MultiAgentTelemetryCapture();
        ActivitySpanId bodyParent = default;
        var graph = new Graph(GraphOptions.Python with
        {
            Nodes = [new FunctionNode("function", (_, _, _) => { bodyParent = Activity.Current?.SpanId ?? default; return MultiAgentTelemetryCapture.Output(new(4, 2)); })]
        });

        await graph.InvokeAsync("task");

        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph");
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal(span.SpanId, bodyParent);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal("task", Assert.Single(span.Events).Tags.Single(value => value.Key == "content").Value);
        Assert.Null(span.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Null(span.GetTagItem("gen_ai.event.end_time"));
        Assert.DoesNotContain(capture.Stopped, value => value.OperationName.StartsWith("node ", StringComparison.Ordinal));
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Fact]
    public async Task ParallelVisitsKeepSeparateParentsAcrossFactoryAwaitYieldAndDisposal()
    {
        using var capture = new MultiAgentTelemetryCapture();
        var observed = new ConcurrentDictionary<string, List<ActivitySpanId>>(StringComparer.Ordinal);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        FunctionNode Node(string id) => new(id, (_, _, token) =>
        {
            observed[id] = [Activity.Current?.SpanId ?? default];
            return Body(id, token);
        });
        var graph = new Graph(new() { Nodes = [Node("left"), Node("right")] });
        var running = graph.InvokeAsync("task");
        try { Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000))); }
        finally { release.TrySetResult(true); }
        await running;

        var root = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph graph");
        foreach (var id in new[] { "left", "right" })
        {
            var node = Assert.Single(capture.Stopped, value => value.OperationName == "node " + id);
            Assert.Equal(root.SpanId, node.ParentSpanId);
            Assert.All(observed[id], parent => Assert.Equal(node.SpanId, parent));
            Assert.Equal(node.SpanId, Assert.Single(capture.Stopped, value => value.OperationName == "child " + id).ParentSpanId);
        }
        Assert.Same(capture.Ambient, Activity.Current);

        async IAsyncEnumerable<INodeStreamEvent> Body(string id, [EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                observed[id].Add(Activity.Current?.SpanId ?? default);
                if (Interlocked.Increment(ref count) == 2) entered.TrySetResult(true);
                await release.Task;
                token.ThrowIfCancellationRequested();
                observed[id].Add(Activity.Current?.SpanId ?? default);
                using (new Tracer().StartSpan("child " + id)) { }
                yield return new NodeStreamEvent("progress");
                observed[id].Add(Activity.Current?.SpanId ?? default);
                yield return new NodeResultUpdateEvent();
            }
            finally { observed[id].Add(Activity.Current?.SpanId ?? default); }
        }
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript)]
    [InlineData(MultiAgentTelemetryProfile.Python)]
    public async Task BodyAndIteratorCleanupErrorsKeepTheirIdentityUnderTracing(MultiAgentTelemetryProfile profile)
    {
        using var capture = new MultiAgentTelemetryCapture();
        var primary = new IOException("body");
        var cleanup = new IOException("cleanup");
        var graph = new Graph(new() { Nodes = [new FaultingNode(primary, cleanup)], NodeFailure = NodeFailurePolicy.FailFast, Telemetry = new() { Profile = profile } });

        var error = await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync("task"));

        Assert.Same(primary, error);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        var span = Assert.Single(capture.Stopped, value => value.OperationName.StartsWith("invoke_graph", StringComparison.Ordinal));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Single(span.Events, value => value.Name == "exception");
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Fact]
    public async Task FreshCheckpointResumeStartsNewSpansAndReportsRetainedWallTimeWithoutChangingResultDuration()
    {
        using var capture = new MultiAgentTelemetryCapture();
        Graph Create()
        {
            var hooks = new MultiAgentHookRegistry();
            hooks.Register<BeforeNodeCallEvent>((value, _) => { Assert.True(value.Interrupt("approval").GetBoolean()); return default; });
            return new(new() { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output(new(4, 2)))], Hooks = hooks });
        }
        var original = Create();
        var paused = await original.InvokeAsync("task");
        var snapshot = await original.TakeSnapshotAsync();
        var data = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var state = JsonNode.Parse(data["state"].GetRawText())!;
        state["startTime"] = DateTimeOffset.UtcNow.AddMinutes(-2);
        data["state"] = JsonSerializer.SerializeToElement(state);
        var restored = Create();
        await restored.LoadSnapshotAsync(new(data));

        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        var spans = capture.Stopped.Where(value => value.OperationName == "invoke_graph graph").ToArray();
        Assert.Equal(2, spans.Length);
        Assert.NotEqual(spans[0].SpanId, spans[1].SpanId);
        Assert.Equal(result.Usage.TotalTokens, spans[1].GetTagItem("gen_ai.usage.total_tokens"));
        Assert.True(Assert.IsType<double>(spans[1].GetTagItem("gen_ai.agent.execution_time")) >= 110000);
        Assert.True(result.Duration.TotalMilliseconds < 110000);
        using var input = JsonDocument.Parse(Assert.IsType<string>(spans[1].GetTagItem("gen_ai.agent.input")));
        Assert.Equal(Assert.Single(paused.Interrupts).Id, input.RootElement[0].GetProperty("interruptResponse").GetProperty("interruptId").GetString());
        Assert.Equal(2, capture.Stopped.Count(value => value.OperationName == "node node"));
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript, ActivityStatusCode.Error)]
    [InlineData(MultiAgentTelemetryProfile.Python, ActivityStatusCode.Unset)]
    public async Task CallerCancellationKeepsItsTokenAndUsesTheSourceStatusPolicy(MultiAgentTelemetryProfile profile, ActivityStatusCode status)
    {
        using var capture = new MultiAgentTelemetryCapture();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, token) => Body(token))], Telemetry = new() { Profile = profile } });
        var running = graph.InvokeAsync("task", cancellationToken: cancellation.Token);
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var span = Assert.Single(capture.Stopped, value => value.OperationName.StartsWith("invoke_graph", StringComparison.Ordinal));
        Assert.Equal(status, span.Status);
        Assert.Equal(profile == MultiAgentTelemetryProfile.TypeScript ? 1 : 0, span.Events.Count(value => value.Name == "exception"));

        async IAsyncEnumerable<INodeStreamEvent> Body([EnumeratorCancellation] CancellationToken token)
        { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); yield break; }
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript)]
    [InlineData(MultiAgentTelemetryProfile.Python)]
    public async Task InvocationHookBookendsFollowTheSelectedSourceLifetime(MultiAgentTelemetryProfile profile)
    {
        using var capture = new MultiAgentTelemetryCapture();
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeMultiAgentInvocationEvent>((_, _) =>
        {
            Assert.Equal(profile == MultiAgentTelemetryProfile.TypeScript, capture.Started.Any(value => value.OperationName.StartsWith("invoke_graph", StringComparison.Ordinal)));
            Assert.Same(capture.Ambient, Activity.Current);
            return default;
        });
        hooks.Register<AfterMultiAgentInvocationEvent>((_, _) =>
        {
            var span = Assert.Single(capture.Started, value => value.OperationName.StartsWith("invoke_graph", StringComparison.Ordinal));
            Assert.Equal(profile == MultiAgentTelemetryProfile.TypeScript, capture.Stopped.Contains(span));
            Assert.Equal(profile == MultiAgentTelemetryProfile.Python ? span.SpanId : capture.Ambient.SpanId, Activity.Current?.SpanId);
            return default;
        });
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => MultiAgentTelemetryCapture.Output())], Hooks = hooks, Telemetry = new() { Profile = profile } });
        await graph.InvokeAsync("task");
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript, ActivityStatusCode.Ok)]
    [InlineData(MultiAgentTelemetryProfile.Python, ActivityStatusCode.Unset)]
    public async Task EarlyConsumerDisposalDrainsTheBodyAndEndsEveryVisitWithoutInventingAnErrorOrUsage(MultiAgentTelemetryProfile profile, ActivityStatusCode status)
    {
        using var capture = new MultiAgentTelemetryCapture();
        var closed = false;
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, token) => Body(token))], Telemetry = new() { Profile = profile } });
        var reader = graph.StreamAsync(new MultiAgentInput("task")).GetAsyncEnumerator();
        while (await reader.MoveNextAsync())
        {
            Assert.Same(capture.Ambient, Activity.Current);
            if (reader.Current is NodeStreamUpdateEvent) break;
        }
        await reader.DisposeAsync();

        Assert.True(closed);
        var root = Assert.Single(capture.Stopped, value => value.OperationName.StartsWith("invoke_graph", StringComparison.Ordinal));
        Assert.Equal(status, root.Status);
        Assert.DoesNotContain(root.Events, value => value.Name == "exception");
        Assert.Null(root.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal(capture.Started.Count, capture.Stopped.Count);
        Assert.Same(capture.Ambient, Activity.Current);

        async IAsyncEnumerable<INodeStreamEvent> Body([EnumeratorCancellation] CancellationToken token)
        {
            try { yield return new NodeStreamEvent("progress"); await Task.Delay(Timeout.Infinite, token); }
            finally { closed = true; }
        }
    }

    [Fact]
    public async Task RepeatedVisitsGetDistinctNodeSpansAndOneExactAggregate()
    {
        using var capture = new MultiAgentTelemetryCapture();
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("again", (_, _, _) => MultiAgentTelemetryCapture.Output(new(2, 3)))],
            Edges = [new("again", "again", (state, _, _) => new(state.Steps < 2))],
            Sources = ["again"]
        });
        var result = await graph.InvokeAsync("task");
        var nodes = capture.Stopped.Where(value => value.OperationName == "node again").ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.NotEqual(nodes[0].SpanId, nodes[1].SpanId);
        Assert.Equal(10, result.Usage.TotalTokens);
        Assert.Equal(result.Usage.TotalTokens, Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph graph").GetTagItem("gen_ai.usage.total_tokens"));
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript, false)]
    [InlineData(MultiAgentTelemetryProfile.TypeScript, true)]
    [InlineData(MultiAgentTelemetryProfile.Python, false)]
    [InlineData(MultiAgentTelemetryProfile.Python, true)]
    public async Task FailingInvocationHooksPreserveTheErrorAndCloseOnlyTheSourceLifetime(MultiAgentTelemetryProfile profile, bool after)
    {
        using var capture = new MultiAgentTelemetryCapture();
        var failure = new IOException("callback");
        var bodies = 0;
        var hooks = new MultiAgentHookRegistry();
        if (after) hooks.Register<AfterMultiAgentInvocationEvent>((_, _) => throw failure);
        else hooks.Register<BeforeMultiAgentInvocationEvent>((_, _) => throw failure);
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("node", (_, _, _) => { bodies++; return MultiAgentTelemetryCapture.Output(); })],
            Hooks = hooks,
            Telemetry = new() { Profile = profile }
        });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync("task")));

        Assert.Equal(after ? 1 : 0, bodies);
        var roots = capture.Stopped.Where(value => value.OperationName.StartsWith("invoke_graph", StringComparison.Ordinal)).ToArray();
        if (profile == MultiAgentTelemetryProfile.Python && !after) Assert.Empty(roots);
        else
        {
            var root = Assert.Single(roots);
            Assert.Equal(profile == MultiAgentTelemetryProfile.TypeScript && after ? ActivityStatusCode.Ok : ActivityStatusCode.Error, root.Status);
        }
        Assert.Equal(capture.Started.Count, capture.Stopped.Count);
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Fact]
    public async Task RejectedConcurrentInvocationsDoNotCreatePhantomActivities()
    {
        using var capture = new MultiAgentTelemetryCapture();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => Body())] });
        var running = graph.InvokeAsync("first");
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => graph.InvokeAsync("rejected"));
            Assert.Single(capture.Started, value => value.OperationName == "invoke_graph graph");
        }
        finally { release.TrySetResult(true); await running; }

        async IAsyncEnumerable<INodeStreamEvent> Body()
        { entered.TrySetResult(true); await release.Task; yield return new NodeResultUpdateEvent(); }
    }

    [Fact]
    public async Task AnIndependentBodyFailureMarksOnlyItsActualNodeSpanAsAnError()
    {
        using var capture = new MultiAgentTelemetryCapture();
        var failure = new IOException("independent body");
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => throw failure)] });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Same(failure, Assert.Single(result.Results).Error);
        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node node");
        Assert.Equal(ActivityStatusCode.Error, node.Status);
        Assert.Equal(failure.Message, node.StatusDescription);
        Assert.Equal(failure.Message, Assert.Single(node.Events, value => value.Name == "exception").Tags.Single(value => value.Key == "exception.message").Value);
        Assert.Equal(ActivityStatusCode.Ok, Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph graph").Status);
    }

    [Fact]
    public async Task AHookGeneratedHumanPauseUsesTheSourceZeroVisitExecutionTime()
    {
        using var capture = new MultiAgentTelemetryCapture();
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { value.Interrupt("approval"); return default; });
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => throw new InvalidOperationException("Not admitted."))], Hooks = hooks });

        Assert.Equal(Status.Interrupted, (await graph.InvokeAsync("task")).Status);

        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node node");
        Assert.Equal(0d, node.GetTagItem("gen_ai.agent.execution_time"));
        Assert.Null(node.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal(ActivityStatusCode.Ok, node.Status);
        Assert.Empty(node.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HookGatedVisitSpansStayOpenThroughTheAfterCallbackAndItsPublicEvent(bool interrupt)
    {
        using var capture = new MultiAgentTelemetryCapture();
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { if (interrupt) value.Interrupt("approval"); else value.CancelReason = "denied"; return default; });
        hooks.Register<AfterNodeCallEvent>((_, _) => { Assert.DoesNotContain(capture.Stopped, value => value.OperationName == "node node"); return default; });
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => throw new InvalidOperationException("Not admitted."))], Hooks = hooks });

        await foreach (var item in graph.StreamAsync(new("task")))
            if (item is AfterNodeCallEvent) Assert.DoesNotContain(capture.Stopped, value => value.OperationName == "node node");

        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node node");
        Assert.Equal(0d, node.GetTagItem("gen_ai.agent.execution_time"));
        Assert.Null(node.GetTagItem("gen_ai.usage.total_tokens"));
    }

    [Fact]
    public async Task GatedAfterCleanupCannotReplaceTheRecordedCancellationError()
    {
        using var capture = new MultiAgentTelemetryCapture();
        using var cancellation = new CancellationTokenSource();
        var cleanup = new IOException("after cleanup");
        Exception? bodyError = null;
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        hooks.Register<AfterNodeCallEvent>((value, _) => { bodyError = value.Result.Error; throw cleanup; });
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => throw new InvalidOperationException("Not admitted."))], Hooks = hooks });
        var reader = graph.StreamAsync(new("task"), cancellationToken: cancellation.Token).GetAsyncEnumerator();
        try
        {
            while (await reader.MoveNextAsync()) if (reader.Current is NodeCancelEvent) break;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { while (await reader.MoveNextAsync()) { } });
        }
        finally { await reader.DisposeAsync(); }

        Assert.IsAssignableFrom<OperationCanceledException>(bodyError);
        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node node");
        Assert.Equal(bodyError!.Message, node.StatusDescription);
        Assert.NotEqual(cleanup.Message, node.StatusDescription);
    }

    private sealed class FaultingNode(Exception primary, Exception cleanup) : Node("fault")
    {
        public override IAsyncEnumerable<INodeStreamEvent> HandleAsync(MultiAgentInput input, NodeContext context, CancellationToken cancellationToken = default) => new Sequence(primary, cleanup);
        private sealed class Sequence(Exception primary, Exception cleanup) : IAsyncEnumerable<INodeStreamEvent>, IAsyncEnumerator<INodeStreamEvent>
        {
            public IAsyncEnumerator<INodeStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public INodeStreamEvent Current => throw new InvalidOperationException();
            public ValueTask<bool> MoveNextAsync() => throw primary;
            public ValueTask DisposeAsync() => new(Task.FromException(cleanup));
        }
    }
}
