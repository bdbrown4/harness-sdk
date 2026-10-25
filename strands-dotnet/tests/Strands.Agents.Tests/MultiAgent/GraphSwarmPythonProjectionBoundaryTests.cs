using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphSwarmPythonProjectionBoundaryTests
{
    [Fact]
    public async Task RepeatedNestedNodeUsesLatestResultInItsFirstInsertionPosition()
    {
        var oldA = Leaf("a", Result(new TextBlock("old-a")));
        var b = Leaf("b", Result(new TextBlock("b")));
        var newA = Leaf("a", Result(new TextBlock("new-a")));
        var inner = new MultiAgentResult([oldA, b, newA], []);

        var received = await ProjectAsync(inner);

        Assert.Equal(new[] { "Original Task: task", "\nInputs from previous nodes:", "\nFrom nested:", "  - Agent: new-a\n", "  - Agent: b\n" }, Texts(received));
        Assert.Equal(3, inner.Results.Count);
        Assert.Same(oldA, inner.Results[0]);
        Assert.Same(b, inner.Results[1]);
        Assert.Same(newA, inner.Results[2]);
    }

    [Fact]
    public async Task LatestByIdReductionIsLocalToEachNestedBoundary()
    {
        var left = new MultiAgentResult([Leaf("same", Result(new TextBlock("left-old"))), Leaf("same", Result(new TextBlock("left-new")))], []);
        var right = new MultiAgentResult([Leaf("same", Result(new TextBlock("right")))], []);
        var inner = new MultiAgentResult([Nested("left", left), Nested("right", right)], []);

        var received = await ProjectAsync(inner);

        Assert.Equal(new[] { "Original Task: task", "\nInputs from previous nodes:", "\nFrom nested:", "  - Agent: left-new\n", "  - Agent: right\n" }, Texts(received));
        Assert.Equal(2, left.Results.Count);
        Assert.Single(right.Results);
        Assert.Equal(2, inner.Results.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrReasoningOnlyTypedResultStillContributesItsSourceLabels(bool reasoning)
    {
        var result = reasoning ? Result(new ReasoningBlock("private reasoning")) : Result();
        var inner = new MultiAgentResult([Leaf("empty", result)], []);

        var received = await ProjectAsync(inner);

        Assert.Equal(new[] { "Original Task: task", "\nInputs from previous nodes:", "\nFrom nested:", "  - Agent: " }, Texts(received));
        Assert.DoesNotContain("private reasoning", string.Concat(Texts(received)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedCitationAndStructuredLeavesUseTheExistingPythonResultRenderer(bool structured)
    {
        var result = structured
            ? Result(new TextBlock("ignored")) with { StructuredOutput = JsonSerializer.SerializeToElement(new { ok = true }) }
            : Result(new CitationsBlock([], [new("first"), new("second")]));
        var inner = new MultiAgentResult([Leaf("leaf", result)], []);

        var received = await ProjectAsync(inner);

        var expected = structured ? "  - Agent: {\"ok\":true}" : "  - Agent: first\nsecond\n";
        Assert.Equal(expected, Texts(received)[3]);
    }

    [Fact]
    public async Task NativeFunctionOutputKeepsItsExistingPythonFormatFallback()
    {
        MultiAgentInput? received = null;
        var graph = new Graph(GraphOptions.Python with
        {
            Nodes = [new FunctionNode("native", (_, _, token) => Output("native-output", token)), Sink(input => received = input)],
            Edges = [new("native", "sink")]
        });

        await graph.InvokeAsync("task");

        Assert.NotNull(received);
        Assert.Equal("  - Agent: native-output", Texts(received)[3]);
    }

    [Fact]
    public async Task PythonKnowledgeReprKeepsUpdatedKeyPositionAndStringEscapes()
    {
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Id = "reader", Model = model });
        var swarm = new Swarm(SwarmOptions.Python with { Nodes = [new(agent)] });
        swarm.SharedContext.AddContext("memory", "first", JsonSerializer.SerializeToElement(1));
        swarm.SharedContext.AddContext("memory", "second", JsonSerializer.SerializeToElement(false));
        swarm.SharedContext.AddContext("memory", "first", JsonSerializer.SerializeToElement(3));
        swarm.SharedContext.AddContext("memory", "text", JsonSerializer.SerializeToElement("a'b\"c\\d\n\t🙂"));

        await swarm.InvokeAsync("task");

        var input = Assert.Single(Assert.Single(model.Requests).Messages, message => message.Role == Role.User);
        Assert.Contains("""• memory: {'first': 3, 'second': False, 'text': 'a\'b"c\\d\n\t🙂'}""" + "\n", input.Text, StringComparison.Ordinal);
        Assert.Equal(new[] { "first", "second", "text" }, swarm.SharedContext.GetAll()["memory"].Keys);
        Assert.Equal(3, swarm.SharedContext.GetAll()["memory"]["first"].GetInt32());
    }

    [Fact]
    public async Task BareToolRoutingCanSelectReprWithoutChangingItsLifetimePolicies()
    {
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Id = "reader", Model = model });
        var swarm = new Swarm(new()
        {
            Nodes = [new(agent)],
            Routing = SwarmRoutingMode.Tool,
            ContextTextProfile = SwarmContextTextProfile.PythonRepr
        });
        swarm.SharedContext.AddContext("memory", "ok", JsonSerializer.SerializeToElement(true));

        await swarm.InvokeAsync("task");

        var input = Assert.Single(Assert.Single(model.Requests).Messages, message => message.Role == Role.User);
        Assert.Contains("• memory: {'ok': True}\n", input.Text, StringComparison.Ordinal);
        Assert.Equal(AgentContextPolicy.RestoreAfterExecution, swarm.Options.AgentContext);
        Assert.Equal(SwarmFailurePolicy.Throw, swarm.Options.FailurePolicy);
        Assert.Null(swarm.Options.MaxSteps);
    }

    [Fact]
    public async Task UnknownContextTextProfileIsRejectedBeforeAnyModelCall()
    {
        var model = new CaptureModel();
        await using var agent = new Agent(new() { Model = model });

        Assert.Throws<ArgumentOutOfRangeException>(() => new Swarm(new()
        {
            Nodes = [new(agent)],
            ContextTextProfile = (SwarmContextTextProfile)123
        }));

        Assert.Empty(model.Requests);
        Assert.Equal(SwarmContextTextProfile.NativeJson, new SwarmOptions().ContextTextProfile);
        Assert.Equal(SwarmContextTextProfile.PythonRepr, SwarmOptions.Python.ContextTextProfile);
    }

    private static AgentResult Result(params ContentBlock[] content)
        => new(StopReasons.EndTurn, new(Role.Assistant, content), new(), 0, new Dictionary<string, object?>());

    private static NodeResult Leaf(string id, AgentResult result)
        => new(id, Status.Completed, result.Message?.Content, null, default, null, result.StructuredOutput, [], result, null);

    private static NodeResult Nested(string id, MultiAgentResult result)
        => new(id, Status.Completed, [], null, default, null, null, [], null, result);

    private static string[] Texts(MultiAgentInput input) => input.Content.OfType<TextBlock>().Select(block => block.Text).ToArray();

    private static async Task<MultiAgentInput> ProjectAsync(MultiAgentResult inner)
    {
        MultiAgentInput? received = null;
        var graph = new Graph(GraphOptions.Python with
        {
            Nodes = [new MultiAgentNode(new FixedOrchestrator(inner), "nested"), Sink(input => received = input)],
            Edges = [new("nested", "sink")]
        });
        await graph.InvokeAsync("task");
        return Assert.IsType<MultiAgentInput>(received);
    }

    private static FunctionNode Sink(Action<MultiAgentInput> receive) => new("sink", (input, _, token) =>
    {
        receive(input);
        return Output("done", token);
    });

    private static async IAsyncEnumerable<INodeStreamEvent> Output(string text, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        yield return new NodeResultUpdateEvent([new TextBlock(text)]);
        await Task.CompletedTask;
    }

    private sealed class FixedOrchestrator(MultiAgentResult result) : IMultiAgent
    {
        public string Id => "fixed";
        public Task<MultiAgentResult> InvokeAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        public async IAsyncEnumerable<IMultiAgentStreamEvent> StreamAsync(MultiAgentInput input, MultiAgentInvokeOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MultiAgentResultEvent(result, options?.InvocationState ?? new Dictionary<string, object?>());
            await Task.CompletedTask;
        }
    }

    private sealed class CaptureModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count != 1) throw new InvalidOperationException("The single-node fixture must not replay a completed visit.");
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
