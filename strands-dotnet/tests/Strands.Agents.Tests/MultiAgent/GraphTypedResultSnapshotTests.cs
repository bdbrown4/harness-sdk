using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphTypedResultSnapshotTests
{
    [Fact]
    public async Task PythonAgentCheckpointResultSurvivesGraphSnapshotJsonRoundTrip()
    {
        var effects = 0;
        var model = new ScriptedModel(toolCall: true, new(2, 1));
        var freshModel = new ScriptedModel(toolCall: true, new(2, 1)) { RejectCalls = true };
        await using var agent = CheckpointAgent(model, () => effects++);
        await using var freshAgent = CheckpointAgent(freshModel, () => effects++);
        var graph = DirectGraph(agent);
        var caller = new Dictionary<string, object?> { ["caller-only"] = new object() };
        AgentResult? original = null;
        MultiAgentResult? terminal = null;
        await foreach (var item in graph.StreamAsync(new MultiAgentInput("pause at the tool boundary"), new() { InvocationState = caller }))
        {
            if (item is NodeStreamUpdateEvent { Inner: AgentResultEvent update }) original = update.Result;
            if (item is MultiAgentResultEvent completed) terminal = completed.Result;
        }

        Assert.NotNull(original);
        AssertCheckpoint(original);
        Assert.Same(caller, original.InvocationState);
        Assert.Equal(Status.Completed, Assert.IsType<MultiAgentResult>(terminal).Status);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, model.IteratorDisposals);
        AssertNoHumanResume(graph);
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var restored = DirectGraph(freshAgent);

        await restored.LoadSnapshotAsync(snapshot);

        var row = Assert.Single(Assert.IsType<MultiAgentState>(restored.State).Results);
        Assert.Equal("paused", row.NodeId);
        Assert.Equal(Status.Completed, row.Status);
        Assert.Equal(new Usage(2, 1), row.Usage);
        Assert.Null(row.StructuredOutput);
        Assert.Null(row.Error);
        Assert.Equal("effect-call", Assert.IsType<ToolUseBlock>(Assert.Single(row.Content)).ToolUseId);
        Assert.Equal(0, freshModel.Calls);
        Assert.Equal(0, effects);
        AssertNoHumanResume(restored);

        var inner = ReadPublicResult<AgentResult>(row, "AgentResult");

        AssertCheckpoint(inner);
        Assert.Equal("effect", Assert.IsType<ToolUseBlock>(Assert.Single(inner.Message!.Content)).Name);
        Assert.Empty(inner.InvocationState);
        Assert.Equal(new Usage(), inner.Usage);
        Assert.Equal(0, inner.Turns);
        Assert.Empty(inner.Traces);
        Assert.NotSame(original, inner);
    }

    [Fact]
    public async Task PythonNestedGraphRetainsOrderedTypedLeavesAndCheckpointAcrossSnapshot()
    {
        var effects = 0;
        var pausedModel = new ScriptedModel(toolCall: true, new(2, 1));
        var textModel = new ScriptedModel(toolCall: false, new(3, 4));
        var freshPausedModel = new ScriptedModel(toolCall: true, new(2, 1)) { RejectCalls = true };
        var freshTextModel = new ScriptedModel(toolCall: false, new(3, 4)) { RejectCalls = true };
        await using var pausedAgent = CheckpointAgent(pausedModel, () => effects++);
        await using var textAgent = new Agent(new() { Id = "text-agent", Model = textModel });
        await using var freshPausedAgent = CheckpointAgent(freshPausedModel, () => effects++);
        await using var freshTextAgent = new Agent(new() { Id = "text-agent", Model = freshTextModel });
        var graph = OuterGraph(InnerGraph(pausedAgent, textAgent));
        MultiAgentResult? observedInner = null;
        MultiAgentResult? terminal = null;
        await foreach (var item in graph.StreamAsync(new MultiAgentInput("run the nested graph")))
        {
            if (item is NodeStreamUpdateEvent { Inner: MultiAgentResultEvent nested }) observedInner = nested.Result;
            if (item is MultiAgentResultEvent completed) terminal = completed.Result;
        }

        Assert.NotNull(observedInner);
        Assert.Equal(new[] { "paused", "done" }, observedInner.Results.Select(row => row.NodeId));
        Assert.Equal(Status.Completed, Assert.IsType<MultiAgentResult>(terminal).Status);
        Assert.Equal(new Usage(5, 5), terminal!.Usage);
        Assert.Equal("done", terminal.ToString());
        Assert.Equal(0, effects);
        Assert.Equal(1, pausedModel.Calls);
        Assert.Equal(1, textModel.Calls);
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var freshInner = InnerGraph(freshPausedAgent, freshTextAgent);
        var restored = OuterGraph(freshInner);

        await restored.LoadSnapshotAsync(snapshot);

        var row = Assert.Single(Assert.IsType<MultiAgentState>(restored.State).Results);
        Assert.Equal("nested", row.NodeId);
        Assert.Equal(Status.Completed, row.Status);
        Assert.Equal(new Usage(5, 5), row.Usage);
        Assert.Equal("done", Assert.IsType<TextBlock>(Assert.Single(row.Content)).Text);
        Assert.Equal(0, freshPausedModel.Calls);
        Assert.Equal(0, freshTextModel.Calls);
        Assert.Null(freshInner.State);
        Assert.Equal(0, effects);
        AssertNoHumanResume(restored);

        var nestedResult = ReadPublicResult<MultiAgentResult>(row, "MultiAgentResult");

        Assert.Equal(Status.Completed, nestedResult.Status);
        Assert.Equal(new[] { "paused", "done" }, nestedResult.Results.Select(leaf => leaf.NodeId));
        Assert.Equal(new Usage(5, 5), nestedResult.Usage);
        Assert.Equal("done", nestedResult.ToString());
        var paused = ReadPublicResult<AgentResult>(nestedResult.Results[0], "AgentResult");
        var done = ReadPublicResult<AgentResult>(nestedResult.Results[1], "AgentResult");
        AssertCheckpoint(paused);
        Assert.Equal(StopReasons.EndTurn, done.StopReason);
        Assert.Null(done.Checkpoint);
        Assert.Equal("done", done.Message!.Text);
        Assert.Equal(new Usage(2, 1), nestedResult.Results[0].Usage);
        Assert.Equal(new Usage(3, 4), nestedResult.Results[1].Usage);
        Assert.Equal(new Usage(), paused.Usage);
        Assert.Equal(new Usage(), done.Usage);
        Assert.Empty(paused.InvocationState);
        Assert.Empty(done.InvocationState);
    }

    [Fact]
    public async Task LegacySchema10CompletedCustomResultRemainsLoadableWithoutTypedPayload()
    {
        var originalCalls = 0;
        var restoredCalls = 0;
        var graph = CustomGraph(() => originalCalls++);
        var result = await graph.InvokeAsync("legacy input");
        Assert.Equal(Status.Completed, result.Status);
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        Assert.Equal("1.0", snapshot.SchemaVersion);
        var restored = CustomGraph(() => restoredCalls++);

        await restored.LoadSnapshotAsync(snapshot);

        var row = Assert.Single(Assert.IsType<MultiAgentState>(restored.State).Results);
        Assert.Equal("custom", row.NodeId);
        Assert.Equal(Status.Completed, row.Status);
        Assert.Equal("legacy", Assert.IsType<TextBlock>(Assert.Single(row.Content)).Text);
        Assert.Equal(new Usage(4, 2), row.Usage);
        Assert.Null(row.StructuredOutput);
        Assert.Null(row.Error);
        Assert.Empty(row.Interrupts);
        Assert.Equal(1, originalCalls);
        Assert.Equal(0, restoredCalls);
        AssertNoHumanResume(restored);
    }

    [Fact]
    public async Task UnsupportedSnapshotVersionRejectsBeforeReplacingLegacyLiveState()
    {
        var calls = 0;
        var graph = CustomGraph(() => calls++);
        await graph.InvokeAsync("existing live state");
        var originalState = graph.State;
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var unsupported = new MultiAgentSnapshot(snapshot.Data, snapshot.AppData,
            snapshot.Scope, schemaVersion: "999.0", snapshot.CreatedAt);

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(unsupported).AsTask());

        Assert.Same(originalState, graph.State);
        Assert.Equal(1, calls);
        Assert.Equal("legacy", Assert.IsType<TextBlock>(Assert.Single(Assert.Single(graph.State!.Results).Content)).Text);
        AssertNoHumanResume(graph);
    }

    private static T ReadPublicResult<T>(NodeResult row, string name) where T : class
    {
        var member = Assert.Single(typeof(NodeResult).GetProperties(BindingFlags.Public | BindingFlags.Instance), property => property.Name == name);
        Assert.Equal(typeof(T), member.PropertyType);
        Assert.NotNull(member.GetMethod);
        Assert.True(member.GetMethod!.IsPublic);
        Assert.Null(member.SetMethod);
        return Assert.IsType<T>(member.GetValue(row));
    }

    private static MultiAgentSnapshot RoundTrip(MultiAgentSnapshot snapshot)
    {
        using var owner = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        return owner.RootElement.Deserialize<MultiAgentSnapshot>()!;
    }

    private static void AssertCheckpoint(AgentResult result)
    {
        Assert.Equal(StopReasons.Checkpoint, result.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), result.Checkpoint);
        Assert.Empty(result.Interrupts);
    }

    private static void AssertNoHumanResume(Graph graph)
    {
        Assert.False(graph.CanResume);
        Assert.Empty(graph.PendingInterrupts);
    }

    private static Agent CheckpointAgent(IModel model, Action effect) => new(new()
    {
        Id = "paused-agent",
        Model = model,
        CheckpointBehavior = CheckpointBehavior.PythonReplay,
        Tools = [new FunctionTool(new("effect", "Record one effect", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                effect();
                return new ValueTask<string>("accepted");
            })]
    });

    private static Graph DirectGraph(Agent agent) => new(GraphOptions.Python with
    {
        Id = "direct",
        Nodes = [new AgentNode(agent, "paused")]
    });

    private static Graph InnerGraph(Agent paused, Agent done) => new(GraphOptions.Python with
    {
        Id = "inner",
        Nodes = [new AgentNode(paused, "paused"), new AgentNode(done, "done")],
        Edges = [new("paused", "done")]
    });

    private static Graph OuterGraph(Graph inner) => new(GraphOptions.Python with
    {
        Id = "outer",
        Nodes = [new MultiAgentNode(inner, "nested")]
    });

    private static Graph CustomGraph(Action called) => new(GraphOptions.Python with
    {
        Id = "legacy",
        Nodes = [new FunctionNode("custom", (_, _, cancellationToken) => CustomOutput(called, cancellationToken))]
    });

    private static async IAsyncEnumerable<INodeStreamEvent> CustomOutput(Action called,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        called();
        yield return new NodeResultUpdateEvent([new TextBlock("legacy")], new(4, 2));
        await Task.CompletedTask;
    }

    private sealed class ScriptedModel(bool toolCall, Usage usage) : IModel
    {
        internal bool RejectCalls { get; init; }
        internal int Calls { get; private set; }
        internal int IteratorDisposals { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (RejectCalls) throw new InvalidOperationException("Loading completed Graph results must not execute a model.");
            try
            {
                yield return new ModelMessageStartEvent();
                if (toolCall)
                {
                    Assert.Contains(request.Tools!, tool => tool.Name == "effect");
                    yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("effect-call", "effect", JsonSerializer.SerializeToElement(new { })));
                }
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                }
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMetadataEvent(usage);
                yield return new ModelMessageStopEvent(toolCall ? StopReasons.ToolUse : StopReasons.EndTurn);
                await Task.CompletedTask;
            }
            finally { IteratorDisposals++; }
        }
    }
}
