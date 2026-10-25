using System.Runtime.CompilerServices;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphLifetimeTests
{
    [Fact]
    public async Task StepAdmissionLimitDrainsAlreadyStartedNodesBeforeReportingThePartialResult()
    {
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("a", (_, _, _) => Output("a")), new FunctionNode("b", (_, _, _) => Output("b")), new FunctionNode("c", (_, _, _) => Output("c"))],
            MaxConcurrency = 3,
            MaxSteps = 2
        });

        var error = await Assert.ThrowsAsync<GraphLimitException>(() => graph.InvokeAsync("task"));

        Assert.NotNull(error.Result);
        Assert.Equal(["a", "b"], error.Result.Results.Select(value => value.NodeId).OrderBy(value => value, StringComparer.Ordinal));
        Assert.All(error.Result.Results, value => Assert.Equal(Status.Completed, value.Status));
        Assert.Equal(Status.Pending, graph.State!.Nodes["c"].Status);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("nodeTimeout")]
    public async Task CooperativeDeadlinesDrainTheNodeAndProduceTheCorrectBudgetError(string budget)
    {
        var started = false;
        var disposed = false;
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("node", (_, _, token) => Waiting(() => disposed = true, token, () => started = true))],
            Timeout = budget == "timeout" ? TimeSpan.FromMilliseconds(50) : null,
            NodeTimeout = budget == "nodeTimeout" ? TimeSpan.FromMilliseconds(50) : null
        });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var error = await Assert.ThrowsAsync<GraphLimitException>(() => graph.InvokeAsync("task", cancellationToken: safety.Token));

        Assert.Equal(budget, error.Budget);
        Assert.Equal(started, disposed);
        Assert.NotNull(error.Result);
        Assert.Equal(Status.Failed, error.Result.Status);
    }

    [Fact]
    public async Task ExpiredInvocationDeadlineDoesNotEnterTheBodyAfterNodeCallbacks()
    {
        var started = false;
        var disposed = false;
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("node", (_, _, token) => Waiting(() => disposed = true, token, () => started = true))],
            Timeout = TimeSpan.FromMilliseconds(25),
            Hooks = hooks
        });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<GraphLimitException>(() => graph.InvokeAsync("task", cancellationToken: safety.Token));

        Assert.Equal("timeout", error.Budget);
        Assert.False(started);
        Assert.False(disposed);
        Assert.All(graph.State!.Results, value => Assert.Equal(Status.Cancelled, value.Status));
    }

    [Fact]
    public async Task CancellationAfterAdmissionDrainsAnActiveNodeBeforeReturning()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("node", (_, _, token) => Waiting(() => disposed = true, token, () => started.TrySetResult(true)))]
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var invocation = graph.InvokeAsync("task", cancellationToken: cancellation.Token);
        try { Assert.Same(started.Task, await Task.WhenAny(started.Task, invocation)); }
        finally { cancellation.Cancel(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);

        Assert.True(disposed);
        Assert.Equal(Status.Cancelled, Assert.Single(graph.State!.Results).Status);
    }

    [Fact]
    public async Task EarlyConsumerDisposalCancelsTheNodeAndReleasesTheInvocationGuard()
    {
        var disposed = false;
        var calls = 0;
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, token) => Interlocked.Increment(ref calls) == 1 ? Waiting(() => disposed = true, token) : Output("next"))] });
        await using (var iterator = graph.StreamAsync(new("task")).GetAsyncEnumerator())
            while (await iterator.MoveNextAsync()) if (iterator.Current is NodeStreamUpdateEvent) break;

        Assert.True(disposed);
        Assert.Equal(Status.Cancelled, Assert.Single(graph.State!.Results).Status);
        Assert.Equal("next", (await graph.InvokeAsync("again")).ToString());
    }

    [Fact]
    public async Task ProgressFailureRetainsAllIteratorAndLifecycleCleanupFailures()
    {
        var primary = new InvalidOperationException("progress");
        var dispose = new IOException("iterator disposal");
        var afterNode = new ApplicationException("after node");
        var afterGraph = new ArgumentException("after graph");
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<NodeStreamUpdateEvent>((_, _) => throw primary);
        hooks.Register<AfterNodeCallEvent>((_, _) => throw afterNode);
        hooks.Register<AfterMultiAgentInvocationEvent>((_, _) => throw afterGraph);
        var stream = new BrokenStream(dispose);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => stream)], Hooks = hooks });

        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => graph.InvokeAsync("task")));

        Assert.Equal(1, stream.Disposals);
        var errors = Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]);
        Assert.Equal(3, errors.Count);
        Assert.Contains(dispose, errors);
        Assert.Contains(afterNode, errors);
        Assert.Contains(afterGraph, errors);
    }

    [Fact]
    public async Task DeniedNodesEmitOneCancelledResultAndAllAfterCallbacksAreAttempted()
    {
        var bodyCalls = 0;
        var afterCalls = 0;
        var failure = new IOException("after callback");
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        hooks.Register<AfterNodeCallEvent>((_, _) => { afterCalls++; return default; });
        hooks.Register<AfterNodeCallEvent>((_, _) => throw failure);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => { bodyCalls++; return Output("unexpected"); })], Hooks = hooks });
        var events = new List<IMultiAgentStreamEvent>();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in graph.StreamAsync(new("task"))) events.Add(item); }));

        Assert.Equal(0, bodyCalls);
        Assert.Equal(1, afterCalls);
        Assert.Single(events.OfType<NodeCancelEvent>());
        Assert.Equal(Status.Cancelled, Assert.Single(events.OfType<NodeResultEvent>()).Result.Status);
    }

    [Fact]
    public async Task ReentrantInvocationIsRejectedWithoutPoisoningTheOuterRun()
    {
        Graph? graph = null;
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>(async (_, _) => await Assert.ThrowsAsync<InvalidOperationException>(() => graph!.InvokeAsync("reentrant")));
        graph = new(new() { Nodes = [new FunctionNode("node", (_, _, _) => Output("done"))], Hooks = hooks });

        Assert.Equal("done", (await graph.InvokeAsync("task")).ToString());
        Assert.Equal("done", (await graph.InvokeAsync("again")).ToString());
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(string value)
    { yield return new NodeResultUpdateEvent([new TextBlock(value)]); await Task.CompletedTask; }
    private static async IAsyncEnumerable<INodeStreamEvent> Waiting(Action onDispose, [EnumeratorCancellation] CancellationToken cancellationToken, Action? onStart = null)
    { try { onStart?.Invoke(); yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, cancellationToken); } finally { onDispose(); } }
    private sealed class BrokenStream(Exception cleanup) : IAsyncEnumerable<INodeStreamEvent>, IAsyncEnumerator<INodeStreamEvent>
    {
        public int Disposals { get; private set; }
        public INodeStreamEvent Current => new NodeStreamEvent("working");
        public IAsyncEnumerator<INodeStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(true);
        public ValueTask DisposeAsync() { Disposals++; throw cleanup; }
    }
}
