using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class ToolTransformTests
{
    [Fact]
    public async Task InputAndImplementationTransformsPreserveProviderCorrelationAndOriginalHistory()
    {
        var originalCalls = 0;
        var observedId = "";
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } });
        var original = new FunctionTool(new("original", "Original", schema), (_, _, _) => { originalCalls++; return new("original"); });
        var replacement = new FunctionTool(new("replacement", "Replacement", schema), (input, context, _) => { observedId = context.ToolUse.ToolUseId; return new(input.GetProperty("value").GetInt32().ToString()); });
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            value.ToolUse = new("hook-cannot-change-correlation", "replacement", JsonSerializer.SerializeToElement(new { value = 2 }));
            value.SelectedTool = replacement;
            return default;
        });
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("provider-id", "original", "{\"value\":1}")), AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = [original] });
        await agent.InvokeAsync("run");
        Assert.Equal(0, originalCalls);
        Assert.Equal("provider-id", observedId);
        Assert.Equal(1, Assert.IsType<ToolUseBlock>(agent.Messages[1].Content[0]).Input.GetProperty("value").GetInt32());
        Assert.Equal("2", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task ResultTransformsAreCommittedWithTheOriginalToolUseIdentifier()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var tool = new FunctionTool(new("tool", "Tool", schema), (_, _, _) => new("secret"));
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((value, _) => { value.Result = new("ignored-id", "redacted"); return default; });
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Call(("provider-id", "tool", "{}")), AgentTests.Text("done")), Hooks = hooks, Tools = [tool] });
        await agent.InvokeAsync("run");
        var result = Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]);
        Assert.Equal("redacted", result.Content);
        Assert.Equal("provider-id", result.ToolUseId);
    }
}
