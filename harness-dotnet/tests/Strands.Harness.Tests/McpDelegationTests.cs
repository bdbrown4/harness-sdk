using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class McpDelegationTests
{
    [Fact]
    public async Task ServerAxisIsIndependentOfOrdinaryToolsAndResolvesAliases()
    {
        var read = Tool("read");
        var remote = Tool("docs_echo");
        await using var parent = new Agent(new() { Model = new TextModel(), Tools = [read, remote] });
        AgentSpec? observed = null;
        var tool = Subagent.Create(new()
        {
            InheritedTools = ["read"],
            InheritedMcpServers = new Dictionary<string, IReadOnlyList<ITool>> { ["docs"] = new[] { remote } },
            McpServers = new Choice([new Option("documentation", "docs")], multiple: true),
            Builder = (spec, _, _) =>
            {
                observed = spec;
                return new(new Agent(new() { Model = new TextModel(), Tools = [remote] }));
            },
        });
        Assert.Equal("documentation", tool.Spec.InputSchema.GetProperty("properties").GetProperty("mcp_servers").GetProperty("items").GetProperty("enum")[0].GetString());
        var input = JsonSerializer.SerializeToElement(new { task = "inspect", tools = Array.Empty<string>(), mcp_servers = new[] { "documentation", "forbidden" } });
        var result = await tool.InvokeResultAsync(input, new(parent, new("delegate", "subagent", input), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Empty(observed!.Tools!);
        Assert.Equal(new[] { "docs" }, observed.McpServers);
    }

    [Fact]
    public async Task EmptyServerGrantCannotBeReacquiredThroughTheOrdinaryToolAxis()
    {
        var remote = Tool("docs_echo");
        await using var parent = new Agent(new() { Model = new TextModel(), Tools = [remote] });
        var tool = Subagent.Create(new()
        {
            InheritedTools = ["docs_echo"],
            InheritedMcpServers = new Dictionary<string, IReadOnlyList<ITool>> { ["docs"] = new[] { remote } },
            McpServers = new Fixed(Array.Empty<string>()),
            Builder = (_, _, _) => new(new Agent(new() { Model = new TextModel(), Tools = [remote] })),
        });
        var input = JsonSerializer.SerializeToElement(new { task = "inspect", tools = new[] { "docs_echo" } });
        var result = await tool.InvokeResultAsync(input, new(parent, new("delegate", "subagent", input), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("exceeds", result.Content, StringComparison.Ordinal);
    }

    private static ITool Tool(string name) => new FunctionTool(new(name, "Helper", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("ok"));
    private sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
