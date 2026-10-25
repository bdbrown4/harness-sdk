using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphTypedResultBoundaryTests
{
    [Fact]
    public async Task CaptureDepthBudgetStopsBeforeProjectingAnInvalidDepth80Leaf()
    {
        var invalidLeaf = AgentValue() with { StopReason = null! };
        var leaf = AgentRow("leaf", invalidLeaf, new(2, 1));
        var nested = new MultiAgentResult([leaf], leaf.Content);
        for (var level = 0; level < 80; level++)
        {
            var row = NestedRow("level-" + level, nested);
            nested = new([row], row.Content);
        }
        var calls = 0;
        var graph = GraphFor(null, nested, () => calls++);
        await graph.InvokeAsync("task");
        var original = graph.State;

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.TakeSnapshotAsync().AsTask());

        Assert.Same(original, graph.State);
        Assert.Equal(1, calls);
        Assert.False(graph.CanResume);
    }

    [Fact]
    public void OriginalConstructorsAndDefaultJsonKeepTheOldWireShape()
    {
        var content = new ContentBlock[] { new TextBlock("legacy") };
        var usage = new Usage(2, 1);
        var oldRow = new NodeResult("node", Status.Completed, content, usage, TimeSpan.Zero, null, null, null);
        var oldUpdate = new NodeResultUpdateEvent(content, usage, Status.Completed, null, null, null);
        using var rowJson = JsonDocument.Parse(JsonSerializer.Serialize(oldRow));
        using var updateJson = JsonDocument.Parse(JsonSerializer.Serialize(oldUpdate));

        Assert.Equal(new[] { "NodeId", "Status", "Content", "Usage", "Duration", "StructuredOutput", "Interrupts" },
            rowJson.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new[] { "Content", "Usage", "Status", "Error", "StructuredOutput", "Interrupts" },
            updateJson.RootElement.EnumerateObject().Select(property => property.Name));
        var restoredRow = JsonSerializer.Deserialize<NodeResult>(rowJson.RootElement.GetRawText())!;
        var restoredUpdate = JsonSerializer.Deserialize<NodeResultUpdateEvent>(updateJson.RootElement.GetRawText())!;
        Assert.Equal("legacy", Assert.IsType<TextBlock>(Assert.Single(restoredRow.Content)).Text);
        Assert.Equal("legacy", Assert.IsType<TextBlock>(Assert.Single(restoredUpdate.Content)).Text);
        Assert.Null(restoredRow.AgentResult);
        Assert.Null(restoredRow.MultiAgentResult);
        Assert.Null(restoredUpdate.AgentResult);
        Assert.Null(restoredUpdate.MultiAgentResult);

        var typedRow = AgentRow("node", AgentValue(), usage);
        var typedUpdate = new NodeResultUpdateEvent(content, usage, Status.Completed, null, null, null, typedRow.AgentResult, null);
        Assert.DoesNotContain("caller-secret", JsonSerializer.Serialize(typedRow));
        Assert.DoesNotContain("caller-secret", JsonSerializer.Serialize(typedUpdate));
        using var typedJson = JsonDocument.Parse(JsonSerializer.Serialize(typedRow));
        Assert.Equal(rowJson.RootElement.EnumerateObject().Select(property => property.Name), typedJson.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SafeInnerStructuredPresenceAndOpaqueNullSurviveGraphSnapshot(bool present)
    {
        var nullJson = Parse("null");
        var tool = new ToolResultBlock("call", "legacy display")
        {
            HasExplicitContent = true,
            ContentBlocks = [],
            StructuredContent = nullJson
        };
        var inner = AgentValue() with
        {
            Message = new(Role.Assistant, [tool]),
            StructuredOutput = present ? nullJson : null
        };
        var graph = GraphFor(inner);
        await graph.InvokeAsync("task");
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var restored = GraphFor(AgentValue());

        await restored.LoadSnapshotAsync(snapshot);

        var result = Assert.IsType<AgentResult>(Assert.Single(restored.State!.Results).AgentResult);
        if (present) Assert.Equal(JsonValueKind.Null, result.StructuredOutput!.Value.ValueKind);
        else Assert.Null(result.StructuredOutput);
        var block = Assert.IsType<ToolResultBlock>(Assert.Single(result.Message!.Content));
        Assert.True(block.HasExplicitContent);
        Assert.Empty(block.ContentBlocks);
        Assert.Equal(JsonValueKind.Null, block.StructuredContent!.Value.ValueKind);
        Assert.Empty(result.InvocationState);
        Assert.Equal(new Usage(), result.Usage);
        Assert.Equal(0, result.Turns);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), result.Checkpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version10RejectsRecognizableTypedArmEvenWhenItsValueIsNull(bool nullArm)
    {
        var graph = GraphFor(AgentValue());
        await graph.InvokeAsync("task");
        var state = graph.State;
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        Assert.Equal("1.1", snapshot.SchemaVersion);
        var downgraded = Rewrite(snapshot, data =>
        {
            if (nullArm) First(data)["agentResult"] = null;
        }, version: "1.0");

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(downgraded).AsTask());

        Assert.Same(state, graph.State);
        Assert.False(graph.CanResume);
    }

    [Theory]
    [InlineData("both-arms")]
    [InlineData("null-arm")]
    [InlineData("unknown-agent-type")]
    [InlineData("invalid-marker")]
    public async Task MalformedAgentArmRejectsBeforeReplacingLiveState(string mutation)
    {
        var calls = 0;
        var graph = GraphFor(AgentValue(), called: () => calls++);
        await graph.InvokeAsync("task");
        var state = graph.State;
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var malformed = Rewrite(snapshot, data =>
        {
            var row = First(data);
            switch (mutation)
            {
                case "both-arms": row["multiAgentResult"] = JsonNode.Parse("{}"); break;
                case "null-arm": row["agentResult"] = null; break;
                case "unknown-agent-type": row["agentResult"]!["type"] = "unknown.result"; break;
                case "invalid-marker": row["agentResult"]!["checkpoint"]!["schemaVersion"] = "999.0"; break;
                default: throw new InvalidOperationException("Unknown fixed mutation.");
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(malformed).AsTask());

        Assert.Same(state, graph.State);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("unknown-version")]
    [InlineData("missing-results")]
    public async Task MalformedNestedHeaderRejectsBeforeReplacingLiveState(string mutation)
    {
        var nested = Aggregate();
        var graph = GraphFor(null, nested);
        await graph.InvokeAsync("task");
        var state = graph.State;
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var malformed = Rewrite(snapshot, data =>
        {
            var value = First(data)["multiAgentResult"]!.AsObject();
            switch (mutation)
            {
                case "unknown-type": value["type"] = "unknown.aggregate"; break;
                case "unknown-version": value["schemaVersion"] = 2; break;
                case "missing-results": Assert.True(value.Remove("results")); break;
                default: throw new InvalidOperationException("Unknown fixed mutation.");
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(malformed).AsTask());

        Assert.Same(state, graph.State);
    }

    [Fact]
    public async Task NestedDuplicateReservedHeaderRejectsWebCaseAliasBeforeLiveReplacement()
    {
        var graph = GraphFor(null, Aggregate());
        await graph.InvokeAsync("task");
        var state = graph.State;
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        const string header = "\"type\":\"strands.multi_agent_result\",\"schemaVersion\":1";
        var raw = snapshot.Data["state"].GetRawText();
        Assert.Equal(1, raw.Split(new[] { header }, StringSplitOptions.None).Length - 1);
        var malformed = WithRawState(snapshot, raw.Replace(header, header + ",\"SCHEMAVERSION\":2"));

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(malformed).AsTask());

        Assert.Same(state, graph.State);
    }

    [Fact]
    public async Task OpaqueJsonDuplicateMembersRemainDataInTheSafeInnerMessage()
    {
        var inner = AgentValue() with { Message = new(Role.Assistant, [new JsonBlock(Parse("{\"same\":1,\"same\":2}"))]) };
        var graph = GraphFor(inner);
        await graph.InvokeAsync("task");
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var restored = GraphFor(AgentValue());

        await restored.LoadSnapshotAsync(snapshot);

        var result = Assert.IsType<AgentResult>(Assert.Single(restored.State!.Results).AgentResult);
        var json = Assert.IsType<JsonBlock>(Assert.Single(result.Message!.Content)).Json;
        Assert.Equal(new[] { 1, 2 }, json.EnumerateObject().Select(property => property.Value.GetInt32()));
    }

    [Fact]
    public async Task ShallowNestedSafeResultsKeepOrderedUsageOnceAndFreshInnerState()
    {
        var graph = GraphFor(null, Aggregate());
        await graph.InvokeAsync("task");
        var snapshot = RoundTrip(await graph.TakeSnapshotAsync());
        var restored = GraphFor(null, Aggregate());

        await restored.LoadSnapshotAsync(snapshot);

        var row = Assert.Single(restored.State!.Results);
        var nested = Assert.IsType<MultiAgentResult>(row.MultiAgentResult);
        Assert.Equal(new Usage(5, 5), row.Usage);
        Assert.Equal(new Usage(5, 5), nested.Usage);
        Assert.Equal(new[] { "a", "b" }, nested.Results.Select(result => result.NodeId));
        Assert.Equal(new Usage(2, 1), nested.Results[0].Usage);
        Assert.Equal(new Usage(3, 4), nested.Results[1].Usage);
        Assert.All(nested.Results, result =>
        {
            Assert.Equal(new Usage(), result.AgentResult!.Usage);
            Assert.Empty(result.AgentResult.InvocationState);
        });
    }

    private static AgentResult AgentValue() => new(StopReasons.Checkpoint, Message.Assistant("inner"), new(99, 88), 7,
        new Dictionary<string, object?> { ["caller-secret"] = (Action)(() => { }) })
    {
        Checkpoint = new(CheckpointPosition.AfterModel)
    };

    private static NodeResult AgentRow(string id, AgentResult result, Usage usage) =>
        new(id, Status.Completed, result.Message!.Content, usage, TimeSpan.Zero, null, null, [], result, null);

    private static NodeResult NestedRow(string id, MultiAgentResult nested) =>
        new(id, Status.Completed, nested.Content, nested.Usage, TimeSpan.Zero, null, null, [], null, nested);

    private static MultiAgentResult Aggregate()
    {
        var first = AgentRow("a", AgentValue(), new(2, 1));
        var second = AgentRow("b", AgentValue() with { Message = Message.Assistant("last") }, new(3, 4));
        return new([first, second], second.Content);
    }

    private static Graph GraphFor(AgentResult? agent, MultiAgentResult? nested = null, Action? called = null) => new(GraphOptions.Python with
    {
        Id = "typed-boundary",
        Nodes = [new FunctionNode("root", (_, _, token) => Output(agent, nested, called, token))]
    });

    private static async IAsyncEnumerable<INodeStreamEvent> Output(AgentResult? agent, MultiAgentResult? nested, Action? called,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        called?.Invoke();
        var content = agent?.Message?.Content ?? nested?.Content ?? [];
        var usage = nested?.Usage ?? new Usage(2, 1);
        yield return new NodeResultUpdateEvent(content, usage, Status.Completed, null, null, [], agent, nested);
        await Task.CompletedTask;
    }

    private static JsonObject First(JsonObject state) => state["results"]!.AsArray()[0]!.AsObject();

    private static MultiAgentSnapshot Rewrite(MultiAgentSnapshot snapshot, Action<JsonObject> mutation, string? version = null)
    {
        var state = JsonNode.Parse(snapshot.Data["state"].GetRawText())!.AsObject();
        mutation(state);
        return WithRawState(snapshot, state.ToJsonString(), version);
    }

    private static MultiAgentSnapshot WithRawState(MultiAgentSnapshot snapshot, string raw, string? version = null)
    {
        var data = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        data["state"] = Parse(raw);
        return new(data, snapshot.AppData, snapshot.Scope, version ?? snapshot.SchemaVersion, snapshot.CreatedAt);
    }

    private static MultiAgentSnapshot RoundTrip(MultiAgentSnapshot snapshot) =>
        JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(snapshot))!;

    private static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
