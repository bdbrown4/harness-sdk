using System.Text.Json;
using Strands.Agents.Tasks;

namespace Strands.Agents.Tests.Tasks;

public sealed class BackgroundTaskPolicyTests
{
    [Fact]
    public void DefaultsAndExactRulesFollowUpstreamWildcardPrecedence()
    {
        Assert.Equal(new BackgroundTaskRule(BackgroundTaskMode.Agentic, false), new BackgroundTaskPolicy(new()).For("work"));
        var policy = new BackgroundTaskPolicy(new() { Never = new[] { "*" }, Always = new[] { "work" } });
        Assert.Equal(new BackgroundTaskRule(BackgroundTaskMode.Always, true), policy.For("work"));
        Assert.Equal(new BackgroundTaskRule(BackgroundTaskMode.Never, false), policy.For("other"));
        Assert.Throws<ArgumentException>(() => new BackgroundTaskPolicy(new() { Agentic = new[] { "work" }, Always = new[] { "work" } }));
    }

    [Fact]
    public void SelectionFlagIsAdvertisedWithoutMutatingTheRegisteredSchemaAndStrippedBeforeValidation()
    {
        var policy = new BackgroundTaskPolicy(new());
        var tool = BackgroundTaskManagerTests.Tool();
        var original = tool.Spec.InputSchema.GetRawText();
        var transformed = policy.AddSelection(tool.Spec);
        Assert.NotNull(transformed);
        Assert.Equal("boolean", transformed.InputSchema.GetProperty("properties").GetProperty(BackgroundTaskPolicy.SelectionProperty).GetProperty("type").GetString());
        Assert.Equal(original, tool.Spec.InputSchema.GetRawText());
        var (call, selected) = policy.Prepare(new("call", "work", JsonSerializer.SerializeToElement(new { value = 42, _background_execution = true })));
        Assert.True(selected);
        Assert.False(call.Input.TryGetProperty(BackgroundTaskPolicy.SelectionProperty, out _));
        Assert.Equal(42, call.Input.GetProperty("value").GetInt32());
        Assert.True(policy.ShouldDispatch(tool, selected));
        Assert.False(policy.ShouldDispatch(tool, false));
        Assert.Throws<ArgumentException>(() => policy.Prepare(new("call", "work", JsonSerializer.SerializeToElement(new { _background_execution = "true" }))));
    }

    [Fact]
    public void ForegroundToolsAndCompositeSchemasAreNeverSilentlyGrantedBackgroundExecution()
    {
        var policy = new BackgroundTaskPolicy(new());
        var manage = Tool(BackgroundTaskPolicy.ManageToolName, "{\"type\":\"object\"}");
        Assert.False(policy.ShouldDispatch(manage, true));
        Assert.Throws<ArgumentException>(() => new BackgroundTaskPolicy(new() { Always = new[] { BackgroundTaskPolicy.ManageToolName } }).Validate(new[] { manage }));
        var composite = Tool("composite", "{\"allOf\":[{\"type\":\"object\"}]}");
        Assert.Null(policy.AddSelection(composite.Spec));
        Assert.False(policy.ShouldDispatch(composite, true));
        Assert.Throws<ArgumentException>(() => new BackgroundTaskPolicy(new() { Agentic = new[] { "composite" } }).Validate(new[] { composite }));
        var reserved = Tool("reserved", "{\"properties\":{\"_background_execution\":{\"type\":\"boolean\"}}}");
        Assert.Throws<ArgumentException>(() => policy.Validate(new[] { reserved }));
    }

    private static ITool Tool(string name, string json) => new FunctionTool(new(name, "Work", JsonSerializer.Deserialize<JsonElement>(json)), (_, _, _) => new ValueTask<string>("unused"));
}
