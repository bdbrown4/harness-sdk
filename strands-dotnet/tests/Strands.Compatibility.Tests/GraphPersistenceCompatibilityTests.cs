using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.MultiAgent;
using Strands.Agents.Storage;

namespace Strands.Compatibility.Tests;

public sealed class GraphPersistenceCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedChildAnswersAreDurableWhenConcurrencyChangesAndAnotherChildPausesAgain(bool nested)
    {
        var effects = 0;
        FunctionTool Tool(bool secondary) => new(new("approve", "Approval", Json("{\"type\":\"object\"}")), (_, context, _) =>
        {
            Assert.True(context.Interrupt("approval").GetBoolean());
            if (secondary) Assert.True(context.Interrupt("secondary").GetBoolean());
            effects++;
            return new("accepted");
        });
        await using var left = new Agent(new() { Id = "left", Model = new Model(true), Tools = [Tool(true)] });
        await using var right = new Agent(new() { Id = "right", Model = new Model(true), Tools = [Tool(false)] });
        Node Wrap(Agent agent) => nested ? new MultiAgentNode(new Graph(new() { Id = "inner-" + agent.Id, Nodes = [new AgentNode(agent)] }), agent.Id) : new AgentNode(agent);
        Graph Create(int concurrency) => new(new() { Id = "graph", Nodes = [Wrap(left), Wrap(right)], MaxConcurrency = concurrency });
        var first = Create(2);
        var paused = await first.InvokeAsync("task");
        Assert.Equal(2, paused.Interrupts.Count);
        var serial = Create(1);
        await serial.LoadSnapshotAsync(await first.TakeSnapshotAsync());

        var partial = await serial.InvokeAsync(MultiAgentInput.Resume(paused.Interrupts.Select(question => new InterruptResponse(question.Id, Json("true"))).ToArray()));

        Assert.Equal("secondary", Assert.Single(partial.Interrupts).Name);
        var restored = Create(1);
        await restored.LoadSnapshotAsync(JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(await serial.TakeSnapshotAsync()))!);
        var complete = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(partial.Interrupts).Id, Json("true"))]));
        Assert.Equal(Status.Completed, complete.Status);
        Assert.Equal(2, effects);
        Assert.Equal(new Usage(8, 12), complete.Usage);
        Assert.Empty(left.Messages);
        Assert.Empty(right.Messages);
    }

    [Fact]
    public async Task GraphHookQuestionsAggregateAndAcceptedAnswersSurviveCallbackFailureAndRestart()
    {
        var callbacks = 0;
        var effects = 0;
        var repository = new StorageMultiAgentSessionStore(new InMemoryStorage());
        var failure = new IOException("graph callback");
        BeforeNodeCallEvent? stale = null;
        Graph Create(bool fail)
        {
            var hooks = new MultiAgentHookRegistry();
            hooks.Register<BeforeNodeCallEvent>((value, _) => { stale = value; Assert.True(value.Interrupt("approval").GetBoolean()); return default; });
            hooks.Register<BeforeNodeCallEvent>((value, _) => { Assert.Equal("allowed", value.Interrupt("compliance").GetString()); return default; });
            if (fail) hooks.Register<BeforeMultiAgentInvocationEvent>((_, _) => { if (++callbacks == 2) throw failure; return default; });
            return new(new()
            {
                Id = "graph",
                SessionId = "session",
                SessionStore = repository,
                Hooks = hooks,
                Nodes = [new FunctionNode("node", (input, _, _) => { Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single(input.Content)).Json.GetInt32()); effects++; return Output("done"); })]
            });
        }
        var first = Create(true);
        var paused = await first.InvokeAsync(new MultiAgentInput([new JsonBlock(Json("42"))]));
        Assert.Equal(2, paused.Interrupts.Count);
        Assert.All(paused.Interrupts, question => Assert.Equal(InterruptSource.OrchestratorHook, question.Source));
        Assert.Throws<InvalidOperationException>(() => stale!.Interrupt("stale"));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first.InvokeAsync(MultiAgentInput.Resume(paused.Interrupts.Select(question => new InterruptResponse(question.Id, question.Name == "approval" ? Json("true") : Json("\"allowed\""))).ToArray()))));

        Assert.True(first.CanResume);
        Assert.Empty(first.PendingInterrupts);
        var result = await Create(false).InvokeAsync(MultiAgentInput.Resume([]));
        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task FailedSessionLoadDoesNotRunCallbacksOrOverwriteTheUnreadSession()
    {
        var failure = new IOException("session unreadable");
        var repository = new FailingStore(failure, null);
        var hooks = new MultiAgentHookRegistry();
        var callbacks = 0;
        hooks.Register<MultiAgentInitializedEvent>((_, _) => { callbacks++; return default; });
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => Output("done"))], Hooks = hooks, SessionId = "session", SessionStore = repository });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync("task")));

        Assert.Null(graph.State);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, repository.Saves);
    }

    [Fact]
    public async Task SessionCleanupFailureCannotReplaceTheOriginalFailFastNodeError()
    {
        var primary = new IOException("node failed");
        var secondary = new InvalidOperationException("session save failed");
        var repository = new FailingStore(null, secondary);
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => throw primary)], NodeFailure = NodeFailurePolicy.FailFast, SessionId = "session", SessionStore = repository });

        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync("task")));

        Assert.Same(secondary, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, repository.Saves);
    }

    [Fact]
    public async Task FreshNestedGraphsRestoreAcceptedAnswersAfterCallbackFailureWithoutRepeatingEffects()
    {
        var calls = 0;
        var effects = 0;
        var failure = new IOException("resume callback");
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((_, _) => { if (++calls == 2) throw failure; return default; });
        var tool = Approval(() => effects++);
        await using var firstAgent = new Agent(new() { Id = "agent", Model = new Model(true), Hooks = hooks, Tools = [tool] });
        await using var nextAgent = new Agent(new() { Id = "agent", Model = new Model(false), Tools = [tool] });
        Graph Create(Agent agent) => new(new() { Id = "outer", Nodes = [new MultiAgentNode(new Graph(new() { Id = "inner", Nodes = [new AgentNode(agent)] }), "nested")] });
        var first = Create(firstAgent);
        var paused = await first.InvokeAsync(new MultiAgentInput([new JsonBlock(Json("{\"value\":42}"))]));
        var failed = await first.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, Json("true"))]));
        Assert.Equal(Status.Failed, failed.Status);
        Assert.Same(failure, Assert.Single(failed.Results).Error);
        Assert.True(first.CanResume);
        Assert.Empty(first.PendingInterrupts);
        var snapshot = JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(await first.TakeSnapshotAsync()))!;
        var next = Create(nextAgent);

        await next.LoadSnapshotAsync(snapshot);
        var result = await next.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("done", result.ToString());
        Assert.Equal(1, effects);
        Assert.Equal(new Usage(4, 6), result.Usage);
        Assert.False(next.CanResume);
        Assert.Empty(nextAgent.Messages);
    }

    [Fact]
    public async Task PartialResponsesRouteOnlyToTheirPausedNodeAndCannotReplaceAcceptedAnswers()
    {
        var effects = 0;
        var tool = Approval(() => Interlocked.Increment(ref effects));
        await using var left = new Agent(new() { Id = "left", Model = new Model(true), Tools = [tool] });
        await using var right = new Agent(new() { Id = "right", Model = new Model(true), Tools = [tool] });
        var graph = new Graph(new() { Nodes = [new AgentNode(left), new AgentNode(right)] });
        var paused = await graph.InvokeAsync("task");
        Assert.Equal(2, paused.Interrupts.Count);
        var first = paused.Interrupts[0];

        var partial = await graph.InvokeAsync(MultiAgentInput.Resume([new(first.Id, Json("true"))]));

        Assert.Equal(Status.Interrupted, partial.Status);
        Assert.Equal(1, effects);
        Assert.Single(graph.PendingInterrupts);
        var unchanged = graph.State;
        await Assert.ThrowsAsync<ArgumentException>(() => graph.InvokeAsync(MultiAgentInput.Resume([new(first.Id, Json("false"))])));
        Assert.Same(unchanged, graph.State);
        var complete = await graph.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(graph.PendingInterrupts).Id, Json("true"))]));
        Assert.Equal(Status.Completed, complete.Status);
        Assert.Equal(2, effects);
        Assert.Equal(new Usage(8, 12), complete.Usage);
        Assert.Empty(left.Messages);
        Assert.Empty(right.Messages);
    }

    [Fact]
    public async Task CancellationAfterAcceptanceRetainsTheAnswerAndRestoresTheBorrowedAgent()
    {
        var callbacks = 0;
        var effects = 0;
        using var cancellation = new CancellationTokenSource();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((_, _) => { if (++callbacks == 2) cancellation.Cancel(); return default; });
        await using var agent = new Agent(new() { Id = "agent", Model = new Model(true), Hooks = hooks, Tools = [Approval(() => effects++)] });
        var graph = new Graph(new() { Nodes = [new AgentNode(agent)] });
        var paused = await graph.InvokeAsync("task");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => graph.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, Json("true"))]), cancellationToken: cancellation.Token));

        Assert.True(graph.CanResume);
        Assert.Empty(graph.PendingInterrupts);
        Assert.Empty(agent.Messages);
        var result = await graph.InvokeAsync(MultiAgentInput.Resume([]));
        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, effects);
        Assert.Equal(new Usage(4, 6), result.Usage);
    }

    [Fact]
    public async Task SessionRepositoryRestoresOriginalTypedInputAndSkipsCompletedSiblings()
    {
        var effects = 0;
        var storage = new InMemoryStorage();
        var repository = new StorageMultiAgentSessionStore(storage);
        var tool = Approval(() => { });
        await using var firstAgent = new Agent(new() { Id = "agent", Model = new Model(true), Tools = [tool] });
        await using var nextAgent = new Agent(new() { Id = "agent", Model = new Model(false), Tools = [tool] });
        Graph Create(Agent agent) => new(new()
        {
            Id = "graph",
            SessionId = "opaque/../session",
            SessionStore = repository,
            Nodes = [new FunctionNode("sibling", (_, _, _) => { effects++; return Output("saved"); }), new AgentNode(agent)]
        });
        var first = Create(firstAgent);
        var paused = await first.InvokeAsync(new MultiAgentInput([new JsonBlock(Json("{\"value\":42}"))]));

        var result = await Create(nextAgent).InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, Json("true"))]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, effects);
        Assert.Equal(2, result.Results.Count);
        Assert.Single(await storage.ListAsync());
        Assert.Empty(nextAgent.Messages);
    }

    [Fact]
    public async Task InvalidAndCancelledSnapshotLoadsAreAtomicAndNeverLoadBorrowedAgents()
    {
        await using var agent = new Agent(new() { Id = "agent", Model = new Model(true), Tools = [Approval(() => { })] });
        var graph = new Graph(new() { Id = "graph", Nodes = [new AgentNode(agent)] });
        await graph.InvokeAsync("task");
        var snapshot = await graph.TakeSnapshotAsync();
        var original = graph.State;
        var data = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        data["topology"] = Json("{}");

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(new(data)).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => graph.LoadSnapshotAsync(snapshot, cancellation.Token).AsTask());

        Assert.Same(original, graph.State);
        Assert.True(graph.CanResume);
        Assert.Empty(agent.Messages);
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static FunctionTool Approval(Action effect) => new(new("approve", "Approval", Json("{\"type\":\"object\"}")), (_, context, _) =>
    { Assert.True(context.Interrupt("approval").GetBoolean()); effect(); return new("accepted"); });
    private static async IAsyncEnumerable<INodeStreamEvent> Output(string value)
    { yield return new NodeResultUpdateEvent([new TextBlock(value)]); await Task.CompletedTask; }
    private sealed class FailingStore(Exception? loadError, Exception? saveError) : IMultiAgentSessionStore
    {
        public int Saves { get; private set; }
        public ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { if (loadError is not null) throw loadError; return new((MultiAgentSnapshot?)null); }
        public ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
        { Saves++; if (saveError is not null) throw saveError; return default; }
    }
    private sealed class Model(bool initial) : IModel
    {
        private bool _initial = initial;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (_initial)
            {
                _initial = false;
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("call", "approve", Json("{}")));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            yield return new ModelMetadataEvent(new(2, 3));
            await Task.CompletedTask;
        }
    }
}
