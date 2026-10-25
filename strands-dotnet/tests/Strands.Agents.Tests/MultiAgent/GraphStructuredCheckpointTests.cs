using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphStructuredCheckpointTests
{
    [Fact]
    public async Task TypeScriptStructuredCheckpointCompletesWithoutInventingJson()
    {
        var effects = 0;
        var model = new SingleCallModel(structured: false);
        await using var agent = CreateAgent(model, CheckpointBehavior.TypeScriptRegenerate, () => effects++);

        var run = await ExecuteGraph(agent, StructuredOutputBehavior.TypeScript);

        AssertCheckpoint(run.AgentResult);
        Assert.Equal(Status.Completed, run.Result.Status);
        var node = Assert.Single(run.Result.Results);
        Assert.Equal("worker", node.NodeId);
        Assert.Equal(Status.Completed, node.Status);
        Assert.Null(node.Error);
        Assert.Null(node.StructuredOutput);
        Assert.Empty(node.Interrupts);
        var call = Assert.Single(node.Content.OfType<ToolUseBlock>());
        Assert.Equal("effect-call", call.ToolUseId);
        Assert.Equal("effect", call.Name);
        Assert.Equal(new Usage(2, 1), node.Usage);
        AssertNoPendingHumanResume(run.Graph);
        Assert.Equal(0, effects);
        Assert.Single(model.Requests);
        Assert.Contains(model.Requests[0].Tools!, tool => tool.Name == StructuredOutputTool<JsonElement>.ToolName);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task NativeStructuredCheckpointRetainsTheAcceptedValueRequirement()
    {
        var effects = 0;
        var model = new SingleCallModel(structured: false);
        await using var agent = CreateAgent(model, CheckpointBehavior.TypeScriptRegenerate, () => effects++);

        var run = await ExecuteGraph(agent, StructuredOutputBehavior.Native);

        AssertCheckpoint(run.AgentResult);
        Assert.Equal(Status.Failed, run.Result.Status);
        var node = Assert.Single(run.Result.Results);
        Assert.Equal(Status.Failed, node.Status);
        Assert.IsType<StructuredOutputException>(node.Error);
        Assert.Null(node.StructuredOutput);
        Assert.Empty(node.Interrupts);
        AssertNoPendingHumanResume(run.Graph);
        Assert.Equal(0, effects);
        Assert.Single(model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task UnstructuredCheckpointAlreadyCompletesWithoutJson()
    {
        var effects = 0;
        var model = new SingleCallModel(structured: false);
        await using var agent = CreateAgent(model, CheckpointBehavior.TypeScriptRegenerate, () => effects++);

        var run = await ExecuteGraph(agent, behavior: null);

        AssertCheckpoint(run.AgentResult);
        Assert.Equal(Status.Completed, run.Result.Status);
        var node = Assert.Single(run.Result.Results);
        Assert.Equal(Status.Completed, node.Status);
        Assert.Null(node.Error);
        Assert.Null(node.StructuredOutput);
        Assert.Equal("effect-call", Assert.Single(node.Content.OfType<ToolUseBlock>()).ToolUseId);
        AssertNoPendingHumanResume(run.Graph);
        Assert.Equal(0, effects);
        Assert.Single(model.Requests);
        Assert.DoesNotContain(model.Requests[0].Tools!, tool => tool.Name == StructuredOutputTool<JsonElement>.ToolName);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task TypeScriptAcceptedStructuredValueStillProjectsJson()
    {
        var effects = 0;
        var model = new SingleCallModel(structured: true);
        await using var agent = CreateAgent(model, CheckpointBehavior.Disabled, () => effects++);

        var run = await ExecuteGraph(agent, StructuredOutputBehavior.TypeScript);

        Assert.Equal(StopReasons.ToolUse, run.AgentResult.StopReason);
        Assert.Null(run.AgentResult.Checkpoint);
        Assert.Equal(Status.Completed, run.Result.Status);
        var node = Assert.Single(run.Result.Results);
        Assert.Equal(Status.Completed, node.Status);
        Assert.Null(node.Error);
        Assert.Equal(7, Assert.IsType<JsonElement>(node.StructuredOutput).GetProperty("answer").GetInt32());
        Assert.Equal(new Usage(2, 1), node.Usage);
        AssertNoPendingHumanResume(run.Graph);
        Assert.Equal(0, effects);
        Assert.Single(model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Empty(agent.Messages);
    }

    private static Agent CreateAgent(IModel model, CheckpointBehavior checkpointBehavior, Action effect)
    {
        var tool = new FunctionTool(new("effect", "Record a visible effect", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                effect();
                return new ValueTask<string>("accepted");
            });
        return new(new() { Id = "borrowed-worker", Model = model, Tools = [tool], CheckpointBehavior = checkpointBehavior });
    }

    private static async Task<(Graph Graph, MultiAgentResult Result, AgentResult AgentResult)> ExecuteGraph(
        Agent agent, StructuredOutputBehavior? behavior)
    {
        var options = new AgentNodeOptions
        {
            StructuredOutput = behavior is { } selected ? new()
            {
                Behavior = selected,
                Schema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { answer = new { type = "integer" } },
                    required = new[] { "answer" },
                    additionalProperties = false
                })
            } : null
        };
        var graph = new Graph(new() { Nodes = [new AgentNode(agent, "worker", options)] });
        var agentResults = new List<AgentResult>();
        MultiAgentResult? result = null;
        await foreach (var item in graph.StreamAsync(new MultiAgentInput("task")))
        {
            if (item is NodeStreamUpdateEvent { Inner: AgentResultEvent agentResult }) agentResults.Add(agentResult.Result);
            if (item is MultiAgentResultEvent terminal) result = terminal.Result;
        }
        Assert.NotNull(result);
        return (graph, result, Assert.Single(agentResults));
    }

    private static void AssertCheckpoint(AgentResult result)
    {
        Assert.Equal(StopReasons.Checkpoint, result.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), result.Checkpoint);
        Assert.Empty(result.Interrupts);
    }

    private static void AssertNoPendingHumanResume(Graph graph)
    {
        Assert.False(graph.CanResume);
        Assert.Empty(graph.PendingInterrupts);
    }

    private sealed class SingleCallModel(bool structured) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal int IteratorDisposals { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var name = structured ? StructuredOutputTool<JsonElement>.ToolName : "effect";
            Assert.Contains(request.Tools!, tool => tool.Name == name);
            try
            {
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0,
                    new ToolUseBlock(structured ? "structured-call" : "effect-call", name,
                        structured ? JsonSerializer.SerializeToElement(new { answer = 7 }) : JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMetadataEvent(new Usage(2, 1));
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
                await Task.CompletedTask;
            }
            finally { IteratorDisposals++; }
        }
    }
}
