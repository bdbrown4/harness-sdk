using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphSwarmPythonTextProjectionTests
{
    [Fact]
    public async Task PythonGraphUsesPerBlockNewlinesAndOrdinaryResultFallbackLabel()
    {
        var model = new TextModel("one", "two");
        await using var agent = new Agent(new() { Name = "configured-agent-name", Model = model });
        MultiAgentInput? received = null;
        var graph = new Graph(GraphOptions.Python with
        {
            Nodes = [new AgentNode(agent, "first"), Sink(input => received = input)],
            Edges = [new("first", "sink")]
        });

        await graph.InvokeAsync("task");

        Assert.NotNull(received);
        Assert.Equal(new[] { "Original Task: task", "\nInputs from previous nodes:", "\nFrom first:", "  - Agent: one\ntwo\n" },
            received.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task PythonGraphFlattensBothNestedAgentResultsInTheirSourceOrder()
    {
        var alpha = new TextModel("alpha");
        var beta = new TextModel("beta");
        await using var first = new Agent(new() { Name = "not-result-agent-name", Model = alpha });
        await using var second = new Agent(new() { Name = "also-not-result-agent-name", Model = beta });
        var inner = new Graph(GraphOptions.Python with
        {
            Nodes = [new AgentNode(first, "a"), new AgentNode(second, "b")],
            Edges = [new("a", "b")]
        });
        MultiAgentInput? received = null;
        var graph = new Graph(GraphOptions.Python with
        {
            Nodes = [new MultiAgentNode(inner, "nested"), Sink(input => received = input)],
            Edges = [new("nested", "sink")]
        });

        await graph.InvokeAsync("task");

        Assert.NotNull(received);
        Assert.Equal(new[] { "Original Task: task", "\nInputs from previous nodes:", "\nFrom nested:", "  - Agent: alpha\n", "  - Agent: beta\n" },
            received.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Equal(new[] { "a", "b" }, inner.State!.Results.Select(result => result.NodeId));
        Assert.Equal(1, alpha.Calls);
        Assert.Equal(1, beta.Calls);
    }

    [Fact]
    public async Task TypedGraphRetainsItsCurrentTerminusProjection()
    {
        var alpha = new TextModel("alpha");
        var beta = new TextModel("beta");
        await using var first = new Agent(new() { Model = alpha });
        await using var second = new Agent(new() { Model = beta });
        var inner = new Graph(new()
        {
            Nodes = [new AgentNode(first, "a"), new AgentNode(second, "b")],
            Edges = [new("a", "b")]
        });
        MultiAgentInput? received = null;
        var graph = new Graph(new()
        {
            Nodes = [new MultiAgentNode(inner, "nested"), Sink(input => received = input)],
            Edges = [new("nested", "sink")]
        });

        await graph.InvokeAsync("task");

        Assert.NotNull(received);
        Assert.Equal(new[] { "task", "[node: nested]", "beta" }, received.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Equal(1, alpha.Calls);
        Assert.Equal(1, beta.Calls);
    }

    [Fact]
    public async Task PythonSwarmPresetUsesDictionaryReprForCommittedJsonKnowledge()
    {
        var context = JsonSerializer.SerializeToElement(new { ok = true, value = (object?)null, text = "can't", list = new object?[] { false, "line\n", "🙂" } });
        var firstModel = new HandoffModel(context);
        var targetModel = new TextModel("done");
        await using var researcher = new Agent(new() { Id = "researcher", Model = firstModel });
        await using var writer = new Agent(new() { Id = "writer", Model = targetModel });
        var swarm = await Swarm.CreateAsync(SwarmOptions.Python with { Nodes = [new(researcher), new(writer)] });
        var image = new ImageBlock("image/png", "AAEC");

        await swarm.InvokeAsync(new MultiAgentInput([new TextBlock("task"), image]));

        var request = Assert.Single(targetModel.Requests);
        var input = Assert.Single(request.Messages, message => message.Role == Role.User);
        Assert.Contains("• researcher: {'ok': True, 'value': None, 'text': \"can't\", 'list': [False, 'line\\n', '🙂']}\n", input.Text);
        Assert.Contains("User Request: Multi-modal task", input.Text);
        var receivedImage = Assert.Single(input.Content.OfType<ImageBlock>());
        Assert.Equal(image.MediaType, receivedImage.MediaType);
        Assert.Equal(image.Data, receivedImage.Data);
        Assert.Empty(researcher.Tools);
        Assert.Empty(writer.Tools);
    }

    [Fact]
    public async Task NativeToolRoutingRetainsItsExistingCompactJsonKnowledge()
    {
        var context = JsonSerializer.SerializeToElement(new { ok = true, value = (object?)null });
        var firstModel = new HandoffModel(context);
        var targetModel = new TextModel("done");
        await using var researcher = new Agent(new() { Id = "researcher", Model = firstModel });
        await using var writer = new Agent(new() { Id = "writer", Model = targetModel });
        var swarm = new Swarm(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(researcher), new(writer)] });

        await swarm.InvokeAsync("task");

        var input = Assert.Single(Assert.Single(targetModel.Requests).Messages, message => message.Role == Role.User);
        Assert.Contains("• researcher: {\"ok\":true,\"value\":null}\n", input.Text);
        Assert.Equal("done", swarm.State!.Results.Last().Content.OfType<TextBlock>().Single().Text);
    }

    [Fact]
    public void ExistingOptInAgentRendererProvidesThePythonSeparatorPrimitive()
    {
        var result = new AgentResult(StopReasons.EndTurn, new(Role.Assistant, [new TextBlock("one"), new TextBlock("two")]),
            new(), 0, new Dictionary<string, object?>());

        Assert.Equal("one\ntwo\n", AgentResultRendering.Render(result, AgentResultOutputProfile.Python));
        Assert.Equal("onetwo", result.ToString());
    }

    private static FunctionNode Sink(Action<MultiAgentInput> received) => new("sink", (input, _, cancellationToken) =>
    {
        received(input);
        return Done(cancellationToken);
    });

    private static async IAsyncEnumerable<INodeStreamEvent> Done([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new NodeResultUpdateEvent([new TextBlock("done")]);
        await Task.CompletedTask;
    }

    private sealed class TextModel(params string[] parts) : IModel
    {
        internal int Calls { get; private set; }
        internal List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Requests.Add(request);
            if (Calls != 1) throw new InvalidOperationException("A fixed text visit must not be repeated.");
            yield return new ModelMessageStartEvent();
            for (var index = 0; index < parts.Length; index++)
            {
                yield return new ModelContentBlockStartEvent(index);
                yield return new ModelContentBlockDeltaEvent(index, Text: parts[index]);
                yield return new ModelContentBlockStopEvent(index);
            }
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }

    private sealed class HandoffModel(JsonElement context) : IModel
    {
        private int _calls;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains(request.Tools!, tool => tool.Name == "handoff_to_agent");
            yield return new ModelMessageStartEvent();
            if (++_calls == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: JsonSerializer.Serialize(new { agent_name = "writer", message = "write it", context }));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "handed off");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            await Task.CompletedTask;
        }
    }
}
