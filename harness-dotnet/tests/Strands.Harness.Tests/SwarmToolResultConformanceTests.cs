using System.Text.Json;
using Strands.Agents;
using Strands.Agents.MultiAgent;
using Strands.Harness.Tools;
using static Strands.Harness.Tests.SwarmToolLifecycleConformanceTests;

namespace Strands.Harness.Tests;

public sealed class SwarmToolResultConformanceTests
{
    [Fact]
    public async Task DeveloperPresetAndToolListsAreDetachedBeforeExecution()
    {
        var read = Tool("read");
        var write = Tool("write");
        var fixedNames = new List<string> { "read" };
        var presetNames = new List<string> { "read" };
        var presets = new Dictionary<string, Preset> { ["role"] = new() { Instructions = "original", Tools = presetNames } };
        SwarmAgentSpec? observed = null;
        var tool = SwarmTool.Create(new()
        {
            Instructions = new Fixed("fixed fallback"),
            Tools = new Fixed(fixedNames),
            Presets = presets,
            Builder = (specification, _, _) =>
            {
                observed = specification;
                return new(new Agent(new()
                {
                    Model = new Events(_ => Answer("done")),
                    Tools = [read],
                    SystemPrompt = specification.Instructions
                }));
            }
        });
        fixedNames.Clear(); fixedNames.Add("write");
        presetNames.Clear(); presetNames.Add("write");
        presets["role"] = new() { Instructions = "changed", Tools = ["write"] };
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("unused")), Tools = [read, write] });
        var input = JsonSerializer.SerializeToElement(new
        {
            task = "",
            agents = new[] { new { name = "a", instructions = "untrusted", tools = new[] { "write" }, model = "unowned/provider" } }
        });
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal("a: done", result.Content);
        Assert.NotNull(observed);
        Assert.Equal("role", observed.AgentType);
        Assert.Equal("original", observed.Instructions);
        Assert.Equal(new[] { "read" }, observed.Tools);
        Assert.Null(observed.Model);
    }

    [Fact]
    public async Task NamedTextPreservesUserWhitespaceAndUnicode()
    {
        const string name = "审查🧠";
        const string text = "line \r\n\n";
        var tool = SwarmTool.Create();
        await using var parent = new Agent(new() { Model = new Events(_ => Answer(text)) });
        var input = Team(name);
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(name + ": " + text, result.Content);
    }

    [Theory]
    [InlineData("iterations")]
    [InlineData("handoffs")]
    public async Task ConfiguredLimitReturnsNamedPartialWithoutStartingTheNextAgent(string limit)
    {
        var nextCalls = 0;
        var model = new Events(request =>
        {
            if (request.SystemPrompt == "A.") return HasResult(request) ? Answer("A partial")
                : Calls("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { agent_name = "b", message = "continue" }));
            nextCalls++;
            return Answer("must not run");
        });
        var tool = SwarmTool.Create(new()
        {
            MaxIterations = limit == "iterations" ? 1 : null,
            MaxHandoffs = limit == "handoffs" ? 1 : null
        });
        await using var parent = new Agent(new() { Model = model });
        var input = JsonSerializer.SerializeToElement(new { task = "work", agents = new[] { new { name = "a", instructions = "A." }, new { name = "b", instructions = "B." } } });
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal("team-call", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("a: A partial", result.Content);
        Assert.IsType<SwarmLimitException>(result.Error);
        Assert.Equal(0, nextCalls);
    }

    [Fact]
    public async Task ConstructionFailureKeepsPrimaryAndAttemptsEveryChildCleanup()
    {
        var primary = new IOException("construction failed");
        var firstCleanup = new IOException("first cleanup failed");
        var secondCleanup = new IOException("second cleanup failed");
        var closed = new List<string>();
        var tool = SwarmTool.Create(new()
        {
            Builder = (specification, _, _) => specification.Name == "c" ? throw primary : new(new Agent(new()
            {
                Model = new Events(_ => Answer("must not run")),
                OwnedResources = [new Resource(() =>
                {
                    closed.Add(specification.Name);
                    throw specification.Name == "a" ? firstCleanup : secondCleanup;
                })]
            }))
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("parent usable")) });
        var input = Team("a", "b", "c");
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Same(primary, result.Error);
        Assert.Equal(new[] { "a", "b" }, closed);
        var failures = Flatten(primary).ToArray();
        Assert.Contains(failures, value => ReferenceEquals(value, firstCleanup));
        Assert.Contains(failures, value => ReferenceEquals(value, secondCleanup));
        Assert.Equal("parent usable", (await parent.InvokeAsync("still usable")).ToString());
    }

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        yield return error;
        if (error.InnerException is { } inner) foreach (var value in Flatten(inner)) yield return value;
        if (error.Data["Strands.CleanupErrors"] is List<Exception> cleanup)
            foreach (var nested in cleanup) foreach (var value in Flatten(nested)) yield return value;
    }
    private static bool HasResult(ModelRequest request) => request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true;
    private static ITool Tool(string name) => new FunctionTool(new(name, "Tool", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new("ok"));
    private static ModelStreamEvent[] Calls(string id, string name, JsonElement input) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new(id, name, input)),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)
    ];
}
