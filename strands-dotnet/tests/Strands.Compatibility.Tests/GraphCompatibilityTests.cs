using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.MultiAgent;

namespace Strands.Compatibility.Tests;

public sealed class GraphCompatibilityTests
{
    [Theory]
    [InlineData(AgentContextPolicy.RestoreAfterExecution, 0, 0, 1)]
    [InlineData(AgentContextPolicy.Retain, 4, 2, 3)]
    [InlineData(AgentContextPolicy.ResetOnRevisit, 2, 1, 1)]
    public async Task NativeAgentNodesPreserveTypedInputAndApplyTheSelectedSnapshotPolicy(AgentContextPolicy policy, int messages, int stateCount, int secondInputCount)
    {
        var model = new Model();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { value.Agent.State.Set("count", value.Agent.State.Get<int>("count") + 1); return default; });
        await using var agent = new Agent(new() { Id = "agent", Model = model, Hooks = hooks });
        var graph = new Graph(new()
        {
            Nodes = [new AgentNode(agent)],
            Sources = ["agent"],
            AgentContext = policy,
            Edges = [new("agent", "agent", (state, _, _) => new(state.Steps < 2))]
        });
        MultiAgentInput input;
        using (var document = JsonDocument.Parse("{\"value\":42}")) input = new([new TextBlock("task"), new JsonBlock(document.RootElement)]);

        var result = await graph.InvokeAsync(input);

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(messages, agent.Messages.Count);
        Assert.Equal(stateCount, agent.State.Get<int>("count"));
        Assert.Equal(secondInputCount, model.Requests[1].Messages.Count);
        Assert.Equal(42, Assert.IsType<JsonBlock>(model.Requests[0].Messages[0].Content[1]).Json.GetProperty("value").GetInt32());
        Assert.Equal(new Usage(4, 6), result.Usage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ExplicitAndOrPoliciesControlConditionalFanIn(bool python, bool executes)
    {
        var caller = new Dictionary<string, object?> { ["caller"] = 7 };
        var options = python ? GraphOptions.Python : new GraphOptions();
        var graph = new Graph(options with
        {
            Nodes = [new FunctionNode("a", (_, context, _) => { Assert.Same(caller, context.InvocationState); return Output("a"); }), new FunctionNode("b", (_, _, _) => Output("b")), new FunctionNode("c", (_, _, _) => Output("c"))],
            Edges = [new("a", "c", (_, state, _) => { Assert.Same(caller, state); return new(false); }), new("b", "c")]
        });

        var result = await graph.InvokeAsync("task", new() { InvocationState = caller });

        Assert.Equal(executes, result.Results.Any(value => value.NodeId == "c"));
        Assert.Single(caller);
    }

    [Fact]
    public async Task CyclicLimitsReturnPartialResultsWithoutRepeatingCompletedVisits()
    {
        var builder = new GraphBuilder(GraphOptions.Python);
        builder.AddNode(new FunctionNode("node", (_, _, _) => Output("visit")));
        builder.SetEntryPoint("node").AddEdge("node", "node").Configure(options => options with { MaxSteps = 2 });

        var result = await builder.Build().InvokeAsync("task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal("steps", Assert.IsType<GraphLimitException>(result.Error).Budget);
    }

    [Fact]
    public async Task ParentDeadlineDrainsEveryNestedBodyThatWasStarted()
    {
        var started = false;
        var disposed = false;
        var inner = new Graph(new() { Nodes = [new FunctionNode("inner", (_, _, token) => Body(token))] });
        var outer = new Graph(new() { Nodes = [new MultiAgentNode(inner)], Timeout = TimeSpan.FromMilliseconds(50) });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<GraphLimitException>(() => outer.InvokeAsync("task", cancellationToken: safety.Token));

        Assert.Equal("timeout", error.Budget);
        Assert.Equal(started, disposed);
        if (started) Assert.Equal(Status.Cancelled, Assert.Single(inner.State!.Results).Status);
        Assert.All(outer.State!.Results, result => Assert.Equal(Status.Cancelled, result.Status));

        async IAsyncEnumerable<INodeStreamEvent> Body([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                started = true;
                yield return new NodeStreamEvent("waiting");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public async Task ParentCancellationDrainsAnActiveNestedGraphAfterItsBodySignalsAdmission()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var inner = new Graph(new() { Nodes = [new FunctionNode("inner", (_, _, token) => Body(token))] });
        var outer = new Graph(new() { Nodes = [new MultiAgentNode(inner)] });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var invocation = outer.InvokeAsync("task", cancellationToken: cancellation.Token);

        Assert.Same(started.Task, await Task.WhenAny(started.Task, invocation));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.True(disposed);
        Assert.Equal(Status.Cancelled, Assert.Single(inner.State!.Results).Status);
        Assert.Equal(Status.Cancelled, Assert.Single(outer.State!.Results).Status);

        async IAsyncEnumerable<INodeStreamEvent> Body([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                started.TrySetResult(true);
                yield return new NodeStreamEvent("waiting");
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally { disposed = true; }
        }
    }

    [Fact]
    public async Task EarlyDisposalPreservesIteratorAndLifecycleCleanupFailures()
    {
        var primary = new IOException("iterator cleanup");
        var secondary = new InvalidOperationException("after graph");
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<AfterMultiAgentInvocationEvent>((_, _) => throw secondary);
        var stream = new BrokenStream(primary);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => stream)], Hooks = hooks });
        var iterator = graph.StreamAsync(new("task")).GetAsyncEnumerator();
        while (await iterator.MoveNextAsync() && iterator.Current is not NodeStreamUpdateEvent) { }

        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => iterator.DisposeAsync().AsTask()));

        Assert.Equal(1, stream.Disposals);
        Assert.Same(secondary, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(string value)
    { yield return new NodeResultUpdateEvent([new TextBlock(value)]); await Task.CompletedTask; }
    private static async IAsyncEnumerable<INodeStreamEvent> Waiting(Action onDispose, [EnumeratorCancellation] CancellationToken cancellationToken)
    { try { yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, cancellationToken); } finally { onDispose(); } }
    private sealed class BrokenStream(Exception cleanup) : IAsyncEnumerable<INodeStreamEvent>, IAsyncEnumerator<INodeStreamEvent>
    {
        public int Disposals { get; private set; }
        public INodeStreamEvent Current => new NodeStreamEvent("working");
        public IAsyncEnumerator<INodeStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(true);
        public ValueTask DisposeAsync() { Disposals++; throw cleanup; }
    }
    private sealed class Model : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            yield return new ModelMetadataEvent(new(2, 3));
            await Task.CompletedTask;
        }
    }
}
