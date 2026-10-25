using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class PublicMultiAgentResultProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAgentResultsReturnsOriginalTypedLeafWithoutStatusFiltering(bool failed)
    {
        var caller = new Dictionary<string, object?> { ["caller"] = "unchanged" };
        var value = Result(new TextBlock("visible")) with { InvocationState = caller };
        var error = failed ? new InvalidOperationException("independent native failure") : null;
        var node = Leaf("leaf", value, failed ? Status.Failed : Status.Completed, error);

        var actual = Assert.Single(node.GetAgentResults());

        Assert.Same(value, actual);
        Assert.Same(caller, actual.InvocationState);
        Assert.Same(error, node.Error);
        Assert.Equal(failed ? Status.Failed : Status.Completed, node.Status);
        Assert.Equal("visible\n", node.RenderPython());
        Assert.Equal("unchanged", caller["caller"]);
    }

    [Fact]
    public void NestedGetAgentResultsKeepsPerBoundaryLatestOrderAndDuplicateBranchReferences()
    {
        var oldValue = Result(new TextBlock("old"));
        var latestValue = Result(new TextBlock("latest"));
        var sharedValue = Result(new TextBlock("shared"));
        var oldA = Leaf("a", oldValue, usage: new(1, 2));
        var b = Leaf("b", sharedValue, usage: new(3, 4));
        var latestA = Leaf("a", latestValue, usage: new(5, 6));
        var left = new MultiAgentResult([oldA, b, latestA]);
        var rightB = Leaf("b", sharedValue, usage: new(7, 8));
        var right = new MultiAgentResult([rightB]);
        var leftNode = Nested("left", left);
        var rightNode = Nested("right", right);
        var outer = new MultiAgentResult([leftNode, rightNode]);
        var root = Nested("root", outer);
        var usage = outer.Usage;

        var actual = root.GetAgentResults();

        Assert.Collection(actual,
            value => Assert.Same(latestValue, value),
            value => Assert.Same(sharedValue, value),
            value => Assert.Same(sharedValue, value));
        Assert.Collection(left.Results,
            value => Assert.Same(oldA, value),
            value => Assert.Same(b, value),
            value => Assert.Same(latestA, value));
        Assert.Same(rightB, Assert.Single(right.Results));
        Assert.Collection(outer.Results,
            value => Assert.Same(leftNode, value),
            value => Assert.Same(rightNode, value));
        Assert.Equal(new Usage(16, 20), usage);
        Assert.Equal(usage, outer.Usage);
    }

    [Fact]
    public void ErrorAndUntypedNativeRowsDoNotInventAgentResults()
    {
        var failure = new InvalidOperationException("broken");
        var error = new NodeResult("error", Status.Failed, [new TextBlock("hidden")], error: failure);
        var custom = new NodeResult("custom", Status.Completed,
            [new TextBlock("one"), new ReasoningBlock("private"), new TextBlock("two")]);
        var errorText = error.ToString();
        var customText = custom.ToString();

        Assert.Empty(error.GetAgentResults());
        Assert.Empty(custom.GetAgentResults());
        Assert.Equal("broken", error.RenderPython());
        Assert.Equal("onetwo", custom.RenderPython());
        Assert.Same(failure, error.Error);
        Assert.Null(error.AgentResult);
        Assert.Null(custom.AgentResult);
        Assert.Equal(errorText, error.ToString());
        Assert.Equal(customText, custom.ToString());
    }

    [Theory]
    [InlineData("leaf")]
    [InlineData("nested")]
    [InlineData("error")]
    public void NodeRenderPythonDelegatesToItsSelectedInnerArm(string arm)
    {
        NodeResult node;
        string expected;
        switch (arm)
        {
            case "leaf":
                node = Leaf("leaf", Result(new TextBlock("first"), new ReasoningBlock("hidden"),
                    new CitationsBlock([], [new("citation")]), new TextBlock("last")));
                expected = "first\ncitation\nlast\n";
                break;
            case "nested":
                node = Nested("outer", new MultiAgentResult([Leaf("child", Result(new TextBlock("inside")))]));
                expected = "child: inside";
                break;
            case "error":
                node = new("failed", Status.Failed, error: new InvalidOperationException("broken"));
                expected = "broken";
                break;
            default:
                throw new InvalidOperationException("Unknown fixed result arm.");
        }
        var nativeText = node.ToString();

        Assert.Equal(expected, node.RenderPython());

        Assert.Equal(nativeText, node.ToString());
    }

    [Fact]
    public void AggregateRenderPythonPreservesWhitespaceLabelsAndSkipsOnlyEmptyRows()
    {
        var old = Leaf("repeat", Result(new TextBlock("old")));
        var empty = Leaf("empty", Result(new TextBlock("")));
        var spaces = Leaf("space", Result(new TextBlock("  ")));
        var latest = Leaf("repeat", Result(new TextBlock("  indented  \nnext  ")));
        var end = Leaf("end", Result(new TextBlock("done\n")));
        var aggregate = new MultiAgentResult([old, empty, spaces, latest, end], [new TextBlock("native terminus")]);

        Assert.Equal("repeat:   indented  \nnext  \nspace:   \nend: done\n", aggregate.RenderPython());

        Assert.Equal("native terminus", aggregate.ToString());
        Assert.Collection(aggregate.Results,
            value => Assert.Same(old, value),
            value => Assert.Same(empty, value),
            value => Assert.Same(spaces, value),
            value => Assert.Same(latest, value),
            value => Assert.Same(end, value));
    }

    [Fact]
    public void AggregateRenderPythonRemovesOneLfAtEachNestedBoundary()
    {
        var leaf = Result(new TextBlock("x\n"));
        var inner = new MultiAgentResult([Leaf("leaf", leaf)]);
        var outer = new MultiAgentResult([Nested("nested", inner)]);

        Assert.Equal("x\n\n", AgentResultRendering.Render(leaf, AgentResultOutputProfile.Python));
        Assert.Equal("leaf: x\n", inner.RenderPython());
        Assert.Equal("nested: leaf: x", outer.RenderPython());

        Assert.Empty(inner.Content);
        Assert.Empty(outer.Content);
        Assert.Equal("", inner.ToString());
        Assert.Equal("", outer.ToString());
    }

    [Fact]
    public void AggregateOwnInterruptsOverrideNodeRowsUsingExistingPythonPendingProjection()
    {
        var pending = new Interrupt("id", "approval", "native-call", Parse("{\"ok\":true,\"value\":null}"));
        var leaf = Result(new TextBlock("hidden"));
        var aggregate = new MultiAgentResult([Leaf("row", leaf)], [new TextBlock("native terminus")],
            status: Status.Interrupted, error: new InvalidOperationException("global metadata"), interrupts: [pending]);
        const string expected = "[{'id': 'id', 'name': 'approval', 'reason': {'ok': True, 'value': None}, 'response': None}]";

        Assert.Equal(expected, aggregate.RenderPython());

        Assert.Equal(expected, AgentResultRendering.Render(Result() with { Interrupts = [pending] }, AgentResultOutputProfile.Python));
        Assert.Same(leaf, Assert.Single(aggregate.Results).AgentResult);
        Assert.Equal("native-call", Assert.Single(aggregate.Interrupts).ToolUseId);
        Assert.Equal(Status.Interrupted, aggregate.Status);
        Assert.Equal("native terminus", aggregate.ToString());
        Assert.Equal("global metadata", aggregate.Error!.Message);
    }

    [Fact]
    public void PublicProjectionPreservesLegacyJsonConstructorsAllVisitsAndUsage()
    {
        var caller = new Dictionary<string, object?> { ["caller"] = "untouched" };
        var message = Message.Assistant("leaf");
        var marker = new AgentCheckpoint(CheckpointPosition.AfterModel);
        var agent = new AgentResult("end_turn", message, new(2, 3), 1, caller) { Checkpoint = marker };
        var typed = Leaf("typed", agent, usage: new(2, 3));
        var legacy = new NodeResult("custom", Status.Completed, [new TextBlock("native row")], new(7, 11),
            TimeSpan.FromTicks(2), null, null, []);
        var aggregate = new MultiAgentResult([typed, legacy], [new TextBlock("terminus")],
            TimeSpan.FromTicks(3), Status.Completed, null, []);
        var nested = Nested("wrapper", aggregate);
        var beforeNode = JsonSerializer.Serialize(typed);
        var beforeLegacy = JsonSerializer.Serialize(legacy);
        var beforeAggregate = JsonSerializer.Serialize(aggregate);
        var beforeAgent = JsonSerializer.Serialize(agent);
        var nativeNodeText = typed.ToString();
        var nativeAggregateText = aggregate.ToString();

        var projection = nested.GetAgentResults();
        typed.GetAgentResults();
        legacy.GetAgentResults();
        typed.RenderPython();
        legacy.RenderPython();
        nested.RenderPython();
        aggregate.RenderPython();

        if (projection is IList<AgentResult> list)
        {
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Add(Result(new TextBlock("not admitted"))));
        }
        Assert.Equal(beforeNode, JsonSerializer.Serialize(typed));
        Assert.Equal(beforeLegacy, JsonSerializer.Serialize(legacy));
        Assert.Equal(beforeAggregate, JsonSerializer.Serialize(aggregate));
        Assert.Equal(beforeAgent, JsonSerializer.Serialize(agent));
        using var serializedNode = JsonDocument.Parse(beforeNode);
        Assert.False(serializedNode.RootElement.TryGetProperty("AgentResult", out _));
        Assert.False(serializedNode.RootElement.TryGetProperty("MultiAgentResult", out _));
        Assert.Equal(nativeNodeText, typed.ToString());
        Assert.Equal(nativeAggregateText, aggregate.ToString());
        Assert.Equal("terminus", aggregate.ToString());
        Assert.Collection(aggregate.Results,
            value => Assert.Same(typed, value),
            value => Assert.Same(legacy, value));
        Assert.Equal(new Usage(9, 14), aggregate.Usage);
        Assert.Same(caller, agent.InvocationState);
        Assert.Same(message, agent.Message);
        Assert.Same(marker, agent.Checkpoint);
        Assert.Equal("untouched", caller["caller"]);
    }

    private static AgentResult Result(params ContentBlock[] content)
        => new("end_turn", new Message(Role.Assistant, content), new(), 1, new Dictionary<string, object?>());

    private static NodeResult Leaf(string id, AgentResult value, Status status = Status.Completed, Exception? error = null, Usage? usage = null)
        => new(id, status, value.Message?.Content, usage ?? value.Usage, default, error, value.StructuredOutput, [], value, null);

    private static NodeResult Nested(string id, MultiAgentResult value)
        => new(id, value.Status, value.Content, value.Usage, value.Duration, value.Error, null, value.Interrupts, null, value);

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
