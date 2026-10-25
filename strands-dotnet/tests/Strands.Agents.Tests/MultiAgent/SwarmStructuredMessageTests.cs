using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class SwarmStructuredMessageTests
{
    [Fact]
    public async Task TypeScriptStructuredEmptyFinalMessageRetainsTheLastTypedContent()
    {
        var model = new StructuredFinalModel("");
        await using var agent = new Agent(new() { Id = "finisher", Model = model });
        var swarm = new Swarm(new()
        {
            Routing = SwarmRoutingMode.Structured,
            Nodes = [new(agent)]
        });

        var result = await swarm.InvokeAsync("complete the task");

        Assert.Equal(Status.Completed, result.Status);
        var node = Assert.Single(result.Results);
        Assert.Equal("finisher", node.NodeId);
        Assert.Equal(Status.Completed, node.Status);
        var structured = node.StructuredOutput!.Value;
        Assert.Equal("", structured.GetProperty("message").GetString());
        Assert.Equal(7, structured.GetProperty("context").GetProperty("proof").GetInt32());
        Assert.False(structured.TryGetProperty("agentId", out _));
        var accepted = Assert.IsType<ToolUseBlock>(Assert.Single(node.Content));
        Assert.Equal(StructuredFinalModel.CallId, accepted.ToolUseId);
        Assert.Equal(StructuredOutputTool<JsonElement>.ToolName, accepted.Name);
        Assert.Equal("", accepted.Input.GetProperty("message").GetString());
        var aggregate = Assert.IsType<ToolUseBlock>(Assert.Single(result.Content));
        Assert.Equal(accepted.ToolUseId, aggregate.ToolUseId);
        Assert.Equal(accepted.Name, aggregate.Name);
        Assert.Equal(accepted.Input.GetRawText(), aggregate.Input.GetRawText());
        Assert.True(swarm.State!.Nodes[agent.Id].Terminus);
        Assert.Equal(1, model.Calls);
        Assert.True(model.Closed);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task TypeScriptStructuredNonemptyFinalMessageOverridesTheLastTypedContent()
    {
        const string message = "the authoritative final answer";
        var model = new StructuredFinalModel(message);
        await using var agent = new Agent(new() { Id = "finisher", Model = model });
        var swarm = new Swarm(new()
        {
            Routing = SwarmRoutingMode.Structured,
            Nodes = [new(agent)]
        });

        var result = await swarm.InvokeAsync("complete the task");

        Assert.Equal(Status.Completed, result.Status);
        var node = Assert.Single(result.Results);
        Assert.Equal("finisher", node.NodeId);
        Assert.Equal(Status.Completed, node.Status);
        var structured = node.StructuredOutput!.Value;
        Assert.Equal(message, structured.GetProperty("message").GetString());
        Assert.False(structured.TryGetProperty("agentId", out _));
        var accepted = Assert.IsType<ToolUseBlock>(Assert.Single(node.Content));
        Assert.Equal(StructuredFinalModel.CallId, accepted.ToolUseId);
        Assert.Equal(StructuredOutputTool<JsonElement>.ToolName, accepted.Name);
        Assert.Equal(message, accepted.Input.GetProperty("message").GetString());
        Assert.Equal(message, Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(message, result.ToString());
        Assert.True(swarm.State!.Nodes[agent.Id].Terminus);
        Assert.Equal(1, model.Calls);
        Assert.True(model.Closed);
        Assert.Empty(agent.Messages);
    }

    private sealed class StructuredFinalModel(string message) : IModel
    {
        internal const string CallId = "structured-final-call";
        internal int Calls { get; private set; }
        internal bool Closed { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var name = Assert.Single(request.Tools!).Name;
            try
            {
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0, new(CallId, name, JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: JsonSerializer.Serialize(new { message, context = new { proof = 7 } }));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            finally { Closed = true; }
        }
    }
}
