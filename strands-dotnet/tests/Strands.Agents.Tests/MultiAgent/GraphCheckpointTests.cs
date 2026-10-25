using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphCheckpointTests
{
    [Theory]
    [InlineData(GraphScheduling.ReadyNodes)]
    [InlineData(GraphScheduling.CompletedBatches)]
    public async Task FailedRoutingResumesTheConditionWithoutReplayingItsCompletedSource(GraphScheduling scheduling)
    {
        var sourceEffects = 0;
        var targetEffects = 0;
        var primary = new IOException("route unavailable");
        var store = new Store();
        Graph Create(bool fail) => new(new()
        {
            Id = "routing",
            SessionId = "session",
            SessionStore = store,
            Scheduling = scheduling,
            Nodes = [new FunctionNode("source", (_, _, _) => { sourceEffects++; return Output("source"); }), new FunctionNode("target", (_, _, _) => { targetEffects++; return Output("target"); })],
            Edges = [new("source", "target", (_, _, _) => fail ? throw primary : new(true))]
        });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => Create(true).InvokeAsync("task")));
        var restored = Create(false);
        await restored.LoadSnapshotAsync(store.Latest!);

        var result = await restored.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, sourceEffects);
        Assert.Equal(1, targetEffects);
        Assert.Equal(2, result.Results.Count);
    }

    [Fact]
    public async Task RestoredRoutingDoesNotRequeueAnEarlierSuccessorOrReevaluateItsCondition()
    {
        var sourceEffects = 0;
        var conditions = 0;
        var firstEffects = 0;
        var secondEffects = 0;
        var store = new Store();
        Graph Create(bool fail) => new(new()
        {
            Id = "partial-routing",
            SessionId = "session",
            SessionStore = store,
            Nodes = [new FunctionNode("source", (_, _, _) => { sourceEffects++; return Output("source"); }),
                new FunctionNode("first", (_, _, _) => { firstEffects++; return Output("first"); }), new FunctionNode("second", (_, _, _) => { secondEffects++; return Output("second"); })],
            Edges = [new("source", "first", (_, _, _) => { conditions++; return new(true); }), new("source", "second", (_, _, _) => fail ? throw new IOException("second route") : new(true))]
        });
        await Assert.ThrowsAsync<IOException>(() => Create(true).InvokeAsync("task"));
        var restored = Create(false);
        await restored.LoadSnapshotAsync(store.Latest!);

        var result = await restored.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, sourceEffects);
        Assert.Equal(1, conditions);
        Assert.Equal(1, firstEffects);
        Assert.Equal(1, secondEffects);
        Assert.Equal(3, result.Results.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unfinished")]
    public async Task InvalidPendingTransitionsAreRejectedWithoutReplacingLiveState(string corruption)
    {
        var snapshot = await CaptureUnfinishedAsync();
        var state = JsonNode.Parse(snapshot.Data["state"].GetRawText())!;
        state["pendingTransitions"] = corruption switch
        {
            "missing" => new JsonArray("missing"),
            "duplicate" => new JsonArray("source", "source"),
            _ => new JsonArray("target")
        };
        var data = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        data["state"] = JsonSerializer.SerializeToElement(state);
        var graph = new Graph(new()
        {
            Id = "recovery",
            Nodes = [new FunctionNode("source", (_, _, _) => Output("source")), new FunctionNode("target", (_, _, _) => Output("target"))],
            Edges = [new("source", "target")]
        });
        await graph.InvokeAsync("already complete");
        var previous = graph.State;

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(new(data)).AsTask());

        Assert.Same(previous, graph.State);
        Assert.False(graph.CanResume);
    }

    [Fact]
    public async Task NodeCheckpointWriteHonorsInvocationCancellationAndStillDrainsItsSibling()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saving = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var store = new CallbackStore(async (write, token) =>
        {
            if (write != 1) return;
            Assert.True(token.CanBeCanceled);
            saving.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
        });
        var graph = new Graph(new()
        {
            Id = "cancel-save",
            SessionId = "session",
            SessionStore = store,
            Nodes = [new FunctionNode("source", (_, _, _) => Source()), new FunctionNode("sibling", (_, _, token) => Blocked(token))]
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = graph.InvokeAsync("task", cancellationToken: cancellation.Token);
        try { Assert.Same(saving.Task, await Task.WhenAny(saving.Task, invocation)); }
        finally { cancellation.Cancel(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(disposed);
        Assert.Equal(2, store.Writes);
        Assert.True(graph.CanResume);

        async IAsyncEnumerable<INodeStreamEvent> Source()
        { await entered.Task; yield return new NodeResultUpdateEvent([new TextBlock("source")]); }
        async IAsyncEnumerable<INodeStreamEvent> Blocked([EnumeratorCancellation] CancellationToken token)
        { try { entered.TrySetResult(true); yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, token); } finally { disposed = true; } }
    }

    [Fact]
    public async Task NodeCheckpointFailureRetainsItsIdentityAndAttachesAFailedFinalSaveAfterDraining()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new IOException("node save");
        var secondary = new IOException("final save");
        var disposed = false;
        var store = new CallbackStore((write, _) => Task.FromException(write == 1 ? primary : secondary));
        var graph = new Graph(new()
        {
            Id = "failed-save",
            SessionId = "session",
            SessionStore = store,
            Nodes = [new FunctionNode("source", (_, _, _) => Source()), new FunctionNode("sibling", (_, _, token) => Blocked(token))]
        });

        var actual = await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync("task"));

        Assert.Same(primary, actual);
        Assert.True(disposed);
        Assert.Equal(2, store.Writes);
        Assert.Contains(secondary, Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]));

        async IAsyncEnumerable<INodeStreamEvent> Source()
        { await entered.Task; yield return new NodeResultUpdateEvent([new TextBlock("source")]); }
        async IAsyncEnumerable<INodeStreamEvent> Blocked([EnumeratorCancellation] CancellationToken token)
        { try { entered.TrySetResult(true); yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, token); } finally { disposed = true; } }
    }

    [Fact]
    public async Task InvocationSavePolicyDefersWritesUntilAllAdmittedBodiesAreDrained()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var store = new Store();
        var graph = new Graph(new()
        {
            Id = "invocation",
            SessionId = "session",
            SessionStore = store,
            SaveLatestOn = MultiAgentSaveLatestStrategy.Invocation,
            Nodes = [new FunctionNode("source", (_, _, _) => Output("source")), new FunctionNode("target", (_, _, token) => Blocked(token))],
            Edges = [new("source", "target")]
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = graph.InvokeAsync("task", cancellationToken: cancellation.Token);
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, invocation));
            Assert.False(store.FirstSave.Task.IsCompleted);
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation); }
        Assert.True(disposed);
        Assert.Equal(1, store.Writes);
        Assert.NotNull(store.Latest);
        Assert.True(graph.CanResume);

        async IAsyncEnumerable<INodeStreamEvent> Blocked([EnumeratorCancellation] CancellationToken token)
        { try { entered.TrySetResult(true); yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, token); } finally { disposed = true; } }
    }

    [Fact]
    public async Task NodeCheckpointCannotCommitASiblingWhoseAfterCallbackIsStillRunning()
    {
        var siblingAfter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSibling = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store();
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<AfterNodeCallEvent>(async (value, _) => { if (value.Result.NodeId == "sibling") { siblingAfter.TrySetResult(true); await releaseSibling.Task; } });
        var graph = new Graph(new()
        {
            Id = "parallel",
            SessionId = "session",
            SessionStore = store,
            Hooks = hooks,
            Nodes = [new FunctionNode("source", (_, _, _) => Source()), new FunctionNode("sibling", (_, _, _) => Output("uncommitted"))]
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = graph.InvokeAsync("task", cancellationToken: cancellation.Token);
        MultiAgentSnapshot snapshot;
        try { Assert.Same(store.FirstSave.Task, await Task.WhenAny(store.FirstSave.Task, invocation)); snapshot = await store.FirstSave.Task; }
        finally { cancellation.Cancel(); releaseSibling.TrySetResult(true); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation); }
        var resumedCalls = 0;
        var restored = new Graph(new()
        {
            Id = "parallel",
            Nodes = [new FunctionNode("source", (_, _, _) => throw new InvalidOperationException("Committed source replayed.")), new FunctionNode("sibling", (_, _, _) => { resumedCalls++; return Output("committed"); })]
        });
        await restored.LoadSnapshotAsync(snapshot);

        var result = await restored.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(1, resumedCalls);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal("committed", Assert.IsType<TextBlock>(result.Results.Single(value => value.NodeId == "sibling").Content.Single()).Text);

        async IAsyncEnumerable<INodeStreamEvent> Source()
        { await siblingAfter.Task; yield return new NodeResultUpdateEvent([new TextBlock("source")]); }
    }

    [Fact]
    public async Task JoinCannotBecomeReadyBeforeEveryPredecessorHasFinishedItsAfterCallbacks()
    {
        var siblingAfter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSibling = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store();
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<AfterNodeCallEvent>(async (value, _) => { if (value.Result.NodeId == "sibling") { siblingAfter.TrySetResult(true); await releaseSibling.Task; } });
        var graph = new Graph(new()
        {
            Id = "join",
            SessionId = "session",
            SessionStore = store,
            Hooks = hooks,
            Nodes = [new FunctionNode("source", (_, _, _) => Source()), new FunctionNode("sibling", (_, _, _) => Output("sibling")), new FunctionNode("join", (_, _, _) => Output("join"))],
            Edges = [new("source", "join"), new("sibling", "join")]
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = graph.InvokeAsync("task", cancellationToken: cancellation.Token);
        MultiAgentSnapshot snapshot;
        try { Assert.Same(store.FirstSave.Task, await Task.WhenAny(store.FirstSave.Task, invocation)); snapshot = await store.FirstSave.Task; }
        finally { cancellation.Cancel(); releaseSibling.TrySetResult(true); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation); }

        Assert.DoesNotContain(snapshot.Data["state"].GetProperty("pendingTargets").EnumerateArray(), value => value.GetString() == "join");

        async IAsyncEnumerable<INodeStreamEvent> Source()
        { await siblingAfter.Task; yield return new NodeResultUpdateEvent([new TextBlock("source")]); }
    }

    [Theory]
    [InlineData(GraphRestoredInputPolicy.Incoming, "new task")]
    [InlineData(GraphRestoredInputPolicy.Stored, "stored task")]
    public async Task RestoredUnfinishedExecutionUsesItsExplicitTaskPolicy(GraphRestoredInputPolicy policy, string expected)
    {
        var snapshot = await CaptureUnfinishedAsync();
        MultiAgentInput? received = null;
        var graph = new Graph(new()
        {
            Id = "recovery",
            RestoredInput = policy,
            Nodes = [new FunctionNode("source", (_, _, _) => Output("source")), new FunctionNode("target", (input, _, _) => { received = input; return Output("target"); })],
            Edges = [new("source", "target")]
        });
        await graph.LoadSnapshotAsync(snapshot);

        var result = await graph.InvokeAsync("new task");

        Assert.Equal(Status.Completed, result.Status);
        Assert.StartsWith(expected, Assert.IsType<TextBlock>(received!.Content[0]).Text, StringComparison.Ordinal);
        Assert.Equal(2, result.Results.Count);
    }

    [Fact]
    public async Task CancellationDoesNotOverwriteTheRecoverableNodeCheckpointWithAnUnresumableFinalState()
    {
        var saved = await CaptureUnfinishedAsync(finalSave: true);
        var targetCalls = 0;
        var graph = new Graph(new()
        {
            Id = "recovery",
            Nodes = [new FunctionNode("source", (_, _, _) => throw new InvalidOperationException("Completed source was replayed.")), new FunctionNode("target", (_, _, _) => { targetCalls++; return Output("target"); })],
            Edges = [new("source", "target")]
        });
        await graph.LoadSnapshotAsync(saved);
        Assert.True(graph.CanResume);

        var result = await graph.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, targetCalls);
        Assert.Equal(2, result.Results.Count);
    }

    [Fact]
    public async Task NodeCheckpointResumesAnUnfinishedSiblingWithoutReplayingTheCompletedSource()
    {
        var completedEffects = 0;
        var siblingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store();
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<AfterNodeCallEvent>((value, _) => { if (value.Result.NodeId == "source") sourceCompleted.TrySetResult(true); return default; });
        var source = new FunctionNode("source", (_, _, _) => Source());
        var graph = new Graph(new() { Id = "graph", Nodes = [source, new FunctionNode("sibling", (_, _, token) => Blocked(token))], SessionId = "session", SessionStore = store, Hooks = hooks });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = graph.InvokeAsync(new MultiAgentInput([new JsonBlock(JsonSerializer.SerializeToElement(42))]), cancellationToken: cancellation.Token);
        MultiAgentSnapshot? saved = null;
        Task winner;
        try
        {
            Assert.Same(sourceCompleted.Task, await Task.WhenAny(sourceCompleted.Task, invocation));
            winner = await Task.WhenAny(store.FirstSave.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            if (winner == store.FirstSave.Task) saved = await store.FirstSave.Task;
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        }
        Assert.Same(store.FirstSave.Task, winner);
        var restored = new Graph(new()
        {
            Id = "graph",
            Nodes = [source, new FunctionNode("sibling", (input, _, _) =>
        { Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single(input.Content)).Json.GetInt32()); return Output("finished"); })]
        });

        await restored.LoadSnapshotAsync(saved!);
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, completedEffects);
        Assert.Equal(2, result.Results.Count);

        async IAsyncEnumerable<INodeStreamEvent> Source()
        { await siblingStarted.Task; completedEffects++; yield return new NodeResultUpdateEvent([new TextBlock("saved")]); }
        async IAsyncEnumerable<INodeStreamEvent> Blocked([EnumeratorCancellation] CancellationToken token)
        { siblingStarted.TrySetResult(true); yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, token); }
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(string text)
    { yield return new NodeResultUpdateEvent([new TextBlock(text)]); await Task.CompletedTask; }
    private static async Task<MultiAgentSnapshot> CaptureUnfinishedAsync(bool finalSave = false)
    {
        var store = new Store();
        var graph = new Graph(new()
        {
            Id = "recovery",
            SessionId = "session",
            SessionStore = store,
            Nodes = [new FunctionNode("source", (_, _, _) => Output("source")), new FunctionNode("target", (_, _, token) => Blocked(token))],
            Edges = [new("source", "target")]
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var invocation = graph.InvokeAsync("stored task", cancellationToken: cancellation.Token);
        MultiAgentSnapshot? snapshot = null;
        try
        {
            Assert.Same(store.FirstSave.Task, await Task.WhenAny(store.FirstSave.Task, invocation));
            snapshot = await store.FirstSave.Task;
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation); }
        return finalSave ? store.Latest! : snapshot;

        static async IAsyncEnumerable<INodeStreamEvent> Blocked([EnumeratorCancellation] CancellationToken token)
        { yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, token); }
    }
    private sealed class Store : IMultiAgentSessionStore
    {
        internal TaskCompletionSource<MultiAgentSnapshot> FirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MultiAgentSnapshot? Latest { get; private set; }
        internal int Writes { get; private set; }
        public ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new((MultiAgentSnapshot?)null);
        public ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default) { Writes++; Latest = snapshot; FirstSave.TrySetResult(snapshot); return default; }
    }
    private sealed class CallbackStore(Func<int, CancellationToken, Task> save) : IMultiAgentSessionStore
    {
        internal int Writes { get; private set; }
        public ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new((MultiAgentSnapshot?)null);
        public async ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default) => await save(++Writes, cancellationToken);
    }
}
