using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

[Collection("Telemetry listeners")]
public sealed class SwarmTelemetryTests
{
    [Fact]
    public async Task TypeScriptAutomaticallyTracesTheSwarmAndItsAgentVisit()
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "worker", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"message":"done"}""") });
        var swarm = new Swarm(new() { Id = "swarm-telemetry", Nodes = [new(agent)] });

        var result = await swarm.InvokeAsync("task");

        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_swarm swarm-telemetry");
        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node worker");
        var invocation = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_agent agent");
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal(span.SpanId, node.ParentSpanId);
        Assert.Equal(node.SpanId, invocation.ParentSpanId);
        Assert.Equal(result.Usage.TotalTokens, span.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Fact]
    public async Task PythonAgentInvocationsAreDirectChildrenOfTheClientOrchestrator()
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Model = new MultiAgentTelemetryCapture.TextModel() });
        var swarm = new Swarm(SwarmOptions.Python with { Nodes = [new(agent, "worker")] });

        await swarm.InvokeAsync("task");

        var span = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_swarm");
        var invocation = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_agent agent");
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal(span.SpanId, invocation.ParentSpanId);
        Assert.Null(span.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.DoesNotContain(capture.Stopped, value => value.OperationName.StartsWith("node ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HookCancelledSwarmVisitsHaveTheSourceZeroExecutionTimeAndNeverInvokeTheAgent()
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "worker", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"message":"unused"}""") });
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Hooks = hooks });

        Assert.Equal(Status.Cancelled, (await swarm.InvokeAsync("task")).Status);

        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node worker");
        Assert.Equal("cancelled", node.GetTagItem("gen_ai.agent.status"));
        Assert.Equal(0d, node.GetTagItem("gen_ai.agent.execution_time"));
        Assert.Null(node.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.DoesNotContain(capture.Stopped, value => value.OperationName.StartsWith("invoke_agent ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NestedSwarmAndAgentInvocationsRemainChildrenOfTheirActualVisit()
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "worker", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"message":"done"}""") });
        var swarm = new Swarm(new() { Id = "inner", Nodes = [new(agent)] });
        var graph = new Graph(new() { Id = "outer", Nodes = [new MultiAgentNode(swarm, "nested")] });

        var result = await graph.InvokeAsync("task");

        var outer = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_graph outer");
        var nested = Assert.Single(capture.Stopped, value => value.OperationName == "node nested");
        var inner = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_swarm inner");
        var visit = Assert.Single(capture.Stopped, value => value.OperationName == "node worker");
        var invocation = Assert.Single(capture.Stopped, value => value.OperationName == "invoke_agent agent");
        Assert.Equal(outer.SpanId, nested.ParentSpanId);
        Assert.Equal(nested.SpanId, inner.ParentSpanId);
        Assert.Equal(inner.SpanId, visit.ParentSpanId);
        Assert.Equal(visit.SpanId, invocation.ParentSpanId);
        Assert.Equal(5, result.Usage.TotalTokens);
        Assert.Equal(result.Usage.TotalTokens, outer.GetTagItem("gen_ai.usage.total_tokens"));
    }

    [Fact]
    public async Task ReturningToAnEarlierAgentCreatesANewVisitWithoutMultiplyingUsage()
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var first = new Agent(new() { Id = "first", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"agentId":"last","message":"next"}""", """{"message":"done"}""") });
        await using var last = new Agent(new() { Id = "last", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"agentId":"first","message":"return"}""") });
        var swarm = new Swarm(new() { Nodes = [new(first), new(last)] });

        var result = await swarm.InvokeAsync("task");

        var visits = capture.Stopped.Where(value => value.OperationName == "node first").ToArray();
        Assert.Equal(2, visits.Length);
        Assert.NotEqual(visits[0].SpanId, visits[1].SpanId);
        Assert.Equal(15, result.Usage.TotalTokens);
        Assert.Equal(result.Usage.TotalTokens, Assert.Single(capture.Stopped, value => value.OperationName == "invoke_swarm swarm").GetTagItem("gen_ai.usage.total_tokens"));
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript, ActivityStatusCode.Error)]
    [InlineData(MultiAgentTelemetryProfile.Python, ActivityStatusCode.Unset)]
    public async Task CallerCancellationDrainsTheRealAgentAndPreservesTheCallerToken(MultiAgentTelemetryProfile profile, ActivityStatusCode status)
    {
        using var capture = new MultiAgentTelemetryCapture();
        using var cancellation = new CancellationTokenSource();
        var model = new BlockingModel();
        await using var agent = new Agent(new() { Model = model });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Telemetry = new() { Profile = profile } });
        var running = swarm.InvokeAsync("task", cancellationToken: cancellation.Token);
        Assert.Same(model.Entered.Task, await Task.WhenAny(model.Entered.Task, Task.Delay(8000)));
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(model.Closed);
        Assert.Equal(status, Assert.Single(capture.Stopped, value => value.OperationName.StartsWith("invoke_swarm", StringComparison.Ordinal)).Status);
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript, ActivityStatusCode.Ok)]
    [InlineData(MultiAgentTelemetryProfile.Python, ActivityStatusCode.Unset)]
    public async Task EarlyCloseJoinsTheAgentAndEndsTheActualActivitiesWithoutInventingUsage(MultiAgentTelemetryProfile profile, ActivityStatusCode status)
    {
        using var capture = new MultiAgentTelemetryCapture();
        var model = new BlockingModel();
        await using var agent = new Agent(new() { Model = model });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Telemetry = new() { Profile = profile } });
        var reader = swarm.StreamAsync(new("task")).GetAsyncEnumerator();
        while (await reader.MoveNextAsync()) if (reader.Current is NodeStreamUpdateEvent { Inner: ModelStreamUpdateEvent }) break;
        await reader.DisposeAsync();

        Assert.True(model.Closed);
        var root = Assert.Single(capture.Stopped, value => value.OperationName.StartsWith("invoke_swarm", StringComparison.Ordinal));
        Assert.Equal(status, root.Status);
        Assert.Null(root.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.DoesNotContain(root.Events, value => value.Name == "exception");
        Assert.Equal(capture.Started.Count, capture.Stopped.Count);
        Assert.Same(capture.Ambient, Activity.Current);
    }

    [Theory]
    [InlineData(MultiAgentTelemetryProfile.TypeScript)]
    [InlineData(MultiAgentTelemetryProfile.Python)]
    public async Task AStoredHumanPauseStartsOneNewInvocationAndVisitOnResume(MultiAgentTelemetryProfile profile)
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "worker", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"message":"done"}""") });
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { Assert.True(value.Interrupt("approval").GetBoolean()); return default; });
        Swarm Create() => new(new() { Nodes = [new(agent)], Hooks = hooks, Telemetry = new() { Profile = profile } });
        var original = Create();
        var paused = await original.InvokeAsync("task");
        var restored = Create();
        await restored.LoadSnapshotAsync(await original.TakeSnapshotAsync());

        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status);
        var spans = capture.Stopped.Where(value => value.OperationName.StartsWith("invoke_swarm", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, spans.Length);
        Assert.NotEqual(spans[0].SpanId, spans[1].SpanId);
        Assert.Equal(profile == MultiAgentTelemetryProfile.TypeScript ? 2 : 0, capture.Stopped.Count(value => value.OperationName == "node worker"));
        if (profile == MultiAgentTelemetryProfile.TypeScript) Assert.Equal(result.Usage.TotalTokens, spans[1].GetTagItem("gen_ai.usage.total_tokens"));
        else Assert.All(spans, span => Assert.Null(span.GetTagItem("gen_ai.usage.total_tokens")));
    }

    [Fact]
    public async Task AHookGeneratedHumanPauseUsesTheSourceZeroVisitExecutionTime()
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "worker", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"message":"unused"}""") });
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { value.Interrupt("approval"); return default; });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Hooks = hooks });

        Assert.Equal(Status.Interrupted, (await swarm.InvokeAsync("task")).Status);

        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node worker");
        Assert.Equal(0d, node.GetTagItem("gen_ai.agent.execution_time"));
        Assert.Null(node.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal(ActivityStatusCode.Ok, node.Status);
        Assert.DoesNotContain(capture.Stopped, value => value.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HookGatedVisitSpansStayOpenThroughTheAfterCallbackAndItsPublicEvent(bool interrupt)
    {
        using var capture = new MultiAgentTelemetryCapture();
        await using var agent = new Agent(new() { Id = "worker", Model = new MultiAgentTelemetryCapture.HandoffModel("""{"message":"unused"}""") });
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { if (interrupt) value.Interrupt("approval"); else value.CancelReason = "denied"; return default; });
        hooks.Register<AfterNodeCallEvent>((_, _) => { Assert.DoesNotContain(capture.Stopped, value => value.OperationName == "node worker"); return default; });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Hooks = hooks });

        await foreach (var item in swarm.StreamAsync(new("task")))
            if (item is AfterNodeCallEvent) Assert.DoesNotContain(capture.Stopped, value => value.OperationName == "node worker");

        var node = Assert.Single(capture.Stopped, value => value.OperationName == "node worker");
        Assert.Equal(0d, node.GetTagItem("gen_ai.agent.execution_time"));
        Assert.Null(node.GetTagItem("gen_ai.usage.total_tokens"));
    }

    private sealed class BlockingModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return new ModelMessageStartEvent();
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { Closed = true; }
        }
    }
}
