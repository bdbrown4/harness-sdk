using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class HarnessProgrammaticToolTests
{
    [Fact]
    public async Task FactoryRegistersTheGenuineCallerInTheSharedDefaultPosition()
    {
        Assert.Equal(new[] { "shell", "read", "write", "edit", "web_fetch", "web_search", "programmatic_tool_caller", "subagent" }, Harness.BuiltinToolNames);
        var echo = new FunctionTool(new("echo", "Echo input", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => new(input.GetProperty("value").GetString()!));
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new NoModel(),
            BuiltinTools = new[] { "programmatic_tool_caller" },
            Tools = new[] { echo },
            Session = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Skills = new() { Enabled = false },
            BuiltinPlugins = Array.Empty<string>(),
        });
        var result = await agent.InvokeToolAsync(new("caller", "programmatic_tool_caller", JsonSerializer.SerializeToElement(new { code = "print(await echo(value='genuine'))" })));
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Equal("genuine", result.Content);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task PortableSettingsSelectToolsAndApplyTheDeclaredWallDeadline()
    {
        var options = HarnessAgentConfig.Parse("""{"builtinTools":{"*":false,"programmatic_tool_caller":{"allowedTools":["echo"],"timeout":0.5}},"builtinPlugins":[],"memory":false,"skills":false,"contextManager":false,"session":false}""")
            .ToOptions(new() { AgentOptions = new() { Model = new NoModel() } });
        Assert.Equal(TimeSpan.FromSeconds(0.5), options.ProgrammaticToolCaller.Timeout);
        await using var agent = await Harness.CreateAsync(options with
        {
            BackgroundTasks = null,
            ProgrammaticToolCaller = options.ProgrammaticToolCaller with { Timeout = null },
            Tools = new ITool[] { new FunctionTool(new("echo", "Echo", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new("allowed")), new FunctionTool(new("denied", "Denied", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => throw new InvalidOperationException("No authority")) },
        });
        var result = await agent.InvokeToolAsync(new("selected", "programmatic_tool_caller", JsonSerializer.SerializeToElement(new { code = "print(await echo())\ntry:\n await denied()\nexcept NameError:\n print('denied')" })));
        Assert.True(result.Status == ToolResultStatus.Success, result.Content);
        Assert.Equal("allowed\ndenied", result.Content);
        await using var bounded = await Harness.CreateAsync(options with { BackgroundTasks = null });
        var deadline = await bounded.InvokeToolAsync(new("deadline", "programmatic_tool_caller", JsonSerializer.SerializeToElement(new { code = "while True:\n pass" })));
        Assert.Equal(ToolResultStatus.Error, deadline.Status);
        Assert.Contains("timed out after 0.5 seconds", deadline.Content);
    }

    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No provider call is authorized.");
    }
}
