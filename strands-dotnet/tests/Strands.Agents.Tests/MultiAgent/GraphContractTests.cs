using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphContractTests
{
    [Fact]
    public async Task SchedulingUsesNodeIdentifiersRatherThanCustomObjectEquality()
    {
        var graph = new Graph(new()
        {
            Nodes = [new EqualNode("source"), new EqualNode("left"), new EqualNode("right")],
            Edges = [new("source", "left"), new("source", "right")]
        });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(["left", "right", "source"], result.Results.Select(value => value.NodeId).OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal("leftright", result.ToString());
    }

    [Fact]
    public async Task InvalidReportedUsageCannotProduceASuccessfulAggregate()
    {
        async IAsyncEnumerable<INodeStreamEvent> Body()
        { yield return new NodeResultUpdateEvent(usage: new Usage(-1, 3)); await Task.CompletedTask; }
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => Body())] });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.IsType<ArgumentOutOfRangeException>(Assert.Single(result.Results).Error);
    }

    [Fact]
    public async Task CustomProgressCannotReplaceTheExecutingNodeIdentityBySelectingNestedOrigin()
    {
        var node = new FunctionNode("actual", (_, context, _) => Spoof(context));
        var graph = new Graph(new() { Nodes = [node] });
        var progress = new List<NodeStreamUpdateEvent>();

        await foreach (var item in graph.StreamAsync(new("task"))) if (item is NodeStreamUpdateEvent update) progress.Add(update);

        Assert.Equal("actual", Assert.Single(progress).NodeId);
        Assert.Equal("spoofed", Assert.IsType<NodeStreamUpdateEvent>(progress[0].Inner).NodeId);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("after_result")]
    [InlineData("null_event")]
    [InlineData("unknown")]
    [InlineData("null_stream")]
    [InlineData("null_iterator")]
    public async Task MalformedNodesProduceExplicitFailuresAndDisposeAcquiredIterators(string kind)
    {
        var terminal = new NodeResultUpdateEvent([new TextBlock("done")]);
        var sequence = new Sequence(kind switch
        {
            "empty" => [],
            "duplicate" => [terminal, terminal],
            "after_result" => [terminal, new NodeStreamEvent("late")],
            "null_event" => [null!],
            "unknown" => [new Unknown()],
            _ => []
        });
        var graph = new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => kind switch { "null_stream" => null!, "null_iterator" => new NullIterator(), _ => sequence })] });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.IsType<InvalidOperationException>(Assert.Single(result.Results).Error);
        Assert.Equal(kind is "null_stream" or "null_iterator" ? 0 : 1, sequence.Disposals);
    }

    [Fact]
    public void ConstructionRejectsInvalidTopologyAndBoundsBeforeRunningNodes()
    {
        var node = new FunctionNode("node", (_, _, _) => throw new InvalidOperationException("must not run"));
        var valid = new GraphOptions { Nodes = [node] };
        Assert.Throws<ArgumentException>(() => new Graph(valid with { Nodes = [] }));
        Assert.Throws<ArgumentException>(() => new Graph(valid with { Nodes = [node, node] }));
        Assert.Throws<ArgumentException>(() => new Graph(valid with { Edges = [new("missing", "node")] }));
        Assert.Throws<ArgumentException>(() => new Graph(valid with { Sources = ["missing"] }));
        Assert.Throws<ArgumentException>(() => new Graph(valid with { Nodes = [node, new FunctionNode("island", (_, _, _) => throw new InvalidOperationException())], Sources = ["node"] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Graph(valid with { MaxConcurrency = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Graph(valid with { MaxSteps = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Graph(valid with { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Graph(valid with { NodeTimeout = TimeSpan.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Graph(valid with { Dependencies = (DependencyPolicy)99 }));
    }

    [Fact]
    public async Task StructuredAgentOutputIsRetainedSeparatelyFromItsOriginalTypedResponse()
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("response", StructuredOutputTool<System.Text.Json.JsonElement>.ToolName, "{\"value\":42}")));
        await using var agent = new Agent(new() { Model = model });
        var graph = new Graph(new() { Nodes = [new AgentNode(agent, options: new() { StructuredOutput = new() { Schema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "value" } }) } })] });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(Status.Completed, result.Status);
        var node = Assert.Single(result.Results);
        Assert.Equal(42, node.StructuredOutput!.Value.GetProperty("value").GetInt32());
        Assert.Equal("response", Assert.IsType<ToolUseBlock>(Assert.Single(node.Content)).ToolUseId);
        Assert.Empty(agent.Messages);
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Spoof(NodeContext context)
    {
        yield return new NodeStreamEvent(new NodeStreamUpdateEvent("spoofed", "agentNode", context.State, "payload", NodeStreamSource.Agent, context.InvocationState), NodeStreamSource.MultiAgent);
        yield return new NodeResultUpdateEvent();
        await Task.CompletedTask;
    }
    private sealed record Unknown : INodeStreamEvent;
    private sealed class EqualNode(string id) : Node(id)
    {
        public override async IAsyncEnumerable<INodeStreamEvent> HandleAsync(MultiAgentInput input, NodeContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); yield return new NodeResultUpdateEvent([new TextBlock(Id)]); await Task.CompletedTask; }
        public override bool Equals(object? obj) => obj is EqualNode;
        public override int GetHashCode() => 0;
    }
    private sealed class NullIterator : IAsyncEnumerable<INodeStreamEvent> { public IAsyncEnumerator<INodeStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => null!; }
    private sealed class Sequence(IReadOnlyList<INodeStreamEvent> items) : IAsyncEnumerable<INodeStreamEvent>, IAsyncEnumerator<INodeStreamEvent>
    {
        private int _index = -1;
        public int Disposals { get; private set; }
        public INodeStreamEvent Current => items[_index];
        public IAsyncEnumerator<INodeStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(++_index < items.Count);
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
