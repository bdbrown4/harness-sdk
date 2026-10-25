using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.ToolRegistries;

public sealed class AgentToolRegistryTests
{
    [Fact]
    public async Task ReplacingPublishesTheNewBodySchemaAdvertisementAndChildTogether()
    {
        var model = new RegistryAgentModel(); var old = AgentRegistryFixtures.Tool("first", "old");
        await using var agent = new Agent(new() { Model = model, Tools = [old, AgentRegistryFixtures.Tool("second", "second")] });
        var snapshot = agent.Tools;
        var schema = AgentRegistryFixtures.Json("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"]}");
        var replacement = AgentRegistryFixtures.Tool("first", "new", schema);
        agent.ReplaceApplicationTool(replacement);
        Assert.Equal(new[] { "first", "second" }, agent.Tools.Select(value => value.Spec.Name)); Assert.Same(old, snapshot[0]);
        Assert.Equal(ToolResultStatus.Error, (await agent.InvokeToolAsync(AgentRegistryFixtures.Call("first"))).Status);
        Assert.Equal("new", (await agent.InvokeToolAsync(AgentRegistryFixtures.Call("first", "{\"value\":1}"))).Content);
        await agent.InvokeAsync("advertise");
        Assert.Equal(schema, model.LastRequest!.Tools![0].InputSchema);
        await using var child = agent.CreateChild();
        Assert.Same(replacement, child.Tools[0]); Assert.Equal("new", (await child.InvokeToolAsync(AgentRegistryFixtures.Call("first", "{\"value\":2}"))).Content);
    }

    [Fact]
    public void AConflictingReplacementBatchCannotPartiallySwapAnyApplicationTool()
    {
        var first = AgentRegistryFixtures.Tool("first", "original");
        var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [first, AgentRegistryFixtures.Tool("with-hyphen", "value")], ToolNameConflictPolicy = ToolNameConflictPolicy.RejectNormalized });
        Assert.Throws<ArgumentException>(() => agent.AddOrReplaceApplicationTools([AgentRegistryFixtures.Tool("first", "replacement"), AgentRegistryFixtures.Tool("with_hyphen", "conflict")]));
        Assert.Same(first, agent.Tools[0]); Assert.Equal(2, agent.Tools.Count);
    }

    [Fact]
    public async Task PluginAuthoritySurvivesApplicationClearAndCannotBeReplaced()
    {
        var pluginTool = AgentRegistryFixtures.Tool("plugin", "plugin"); var application = AgentRegistryFixtures.Tool("app", "application");
        await using var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [application], Plugins = [new RegistryToolPlugin(pluginTool)] });
        await agent.InitializeAsync();
        Assert.Throws<InvalidOperationException>(() => agent.ReplaceApplicationTool(AgentRegistryFixtures.Tool("plugin", "replacement")));
        Assert.Throws<InvalidOperationException>(() => agent.AddOrReplaceApplicationTool(AgentRegistryFixtures.Tool("plugin", "replacement")));
        Assert.False(agent.RemoveApplicationTool("plugin")); agent.ClearApplicationTools();
        Assert.Same(pluginTool, Assert.Single(agent.Tools));
        await using var child = agent.CreateChild(); await child.InitializeAsync(); Assert.Same(pluginTool, Assert.Single(child.Tools));
    }

    [Theory]
    [InlineData(ToolNameResolution.UnderscoreAlias, "my_tool")]
    [InlineData(ToolNameResolution.TypeScriptDirect, "my_tool")]
    [InlineData(ToolNameResolution.TypeScriptDirect, "MY-TOOL")]
    public async Task ExplicitPublicDirectResolutionUsesCanonicalToolNames(ToolNameResolution mode, string requested)
    {
        string? beforeName = null; string? bodyName = null;
        var tool = AgentRegistryFixtures.Tool("my-tool", "body", body: context => bodyName = context.ToolUse.Name);
        await using var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [tool], DirectToolNameResolution = mode });
        using var hook = agent.Hooks.Register<BeforeToolCallEvent>((value, _) => { beforeName = value.ToolUse.Name; return default; });
        var result = await agent.InvokeToolAsync(AgentRegistryFixtures.Call(requested));
        Assert.Equal("body", result.Content); Assert.Equal("my-tool", beforeName); Assert.Equal("my-tool", bodyName); Assert.Equal("call", result.ToolUseId);
    }

    [Fact]
    public async Task PublicDirectAliasesDoNotChangeModelOrBidiProtocolLookup()
    {
        var calls = 0; var model = new RegistryAgentModel { ToolName = "my_tool" };
        var tool = AgentRegistryFixtures.Tool("my-tool", "body", body: _ => calls++);
        await using var agent = new Agent(new() { Model = model, Tools = [tool], DirectToolNameResolution = ToolNameResolution.TypeScriptDirect });
        await agent.InvokeAsync("model");
        Assert.Equal(0, calls); Assert.Equal(ToolResultStatus.Error, agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Single().Status);
        var bidi = await agent.InvokeBidiToolAsync(AgentRegistryFixtures.Call("my_tool"), new Dictionary<string, object?>(), null, default);
        Assert.Equal(ToolResultStatus.Error, bidi.Status); Assert.Equal(0, calls);
    }

    [Fact]
    public void NativeDefaultsRemainExactOnlyButNormalizedConstructorPolicyIsExplicit()
    {
        var tools = new[] { AgentRegistryFixtures.Tool("foo-bar", "one"), AgentRegistryFixtures.Tool("foo_bar", "two") };
        var native = new Agent(new() { Model = new RegistryAgentModel(), Tools = tools }); Assert.Equal(2, native.Tools.Count);
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = new RegistryAgentModel(), Tools = tools, ToolNameConflictPolicy = ToolNameConflictPolicy.RejectNormalized }));
    }

    [Fact]
    public void UnknownAgentRegistryPoliciesAreRejectedBeforeToolsAreInspected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Model = new RegistryAgentModel(), ToolNameConflictPolicy = (ToolNameConflictPolicy)99, Tools = [new OpaqueRegistryTool()] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Model = new RegistryAgentModel(), DirectToolNameResolution = (ToolNameResolution)99, Tools = [new OpaqueRegistryTool()] }));
    }

    [Fact]
    public void FilteredSpecificationsAreNeverInspectedByTheNewMutationAPIs()
    {
        var original = AgentRegistryFixtures.Tool("existing", "old"); var opaque = new OpaqueRegistryTool();
        var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [original], ToolFilter = value => !ReferenceEquals(value, opaque) });
        agent.AddOrReplaceApplicationTool(opaque); agent.ReplaceApplicationTool(opaque);
        Assert.Same(original, Assert.Single(agent.Tools));
    }

    [Fact]
    public async Task AllMutationPathsRejectAPendingInterruptBeforeChangingBindings()
    {
        var tool = AgentRegistryFixtures.Tool("approve", "approved", body: context => context.Interrupt("human"));
        await using var agent = new Agent(new() { Model = new RegistryAgentModel { ToolName = "approve" }, Tools = [tool] });
        Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync("pause")).StopReason);
        Assert.Throws<InvalidOperationException>(() => agent.AddTool(AgentRegistryFixtures.Tool("new", "new")));
        Assert.Throws<InvalidOperationException>(() => agent.ReplaceApplicationTool(AgentRegistryFixtures.Tool("approve", "replacement")));
        Assert.Throws<InvalidOperationException>(() => agent.AddOrReplaceApplicationTool(AgentRegistryFixtures.Tool("new", "new")));
        Assert.Throws<InvalidOperationException>(() => agent.RemoveApplicationTool("approve")); Assert.Throws<InvalidOperationException>(() => agent.ClearApplicationTools());
        Assert.Same(tool, Assert.Single(agent.Tools)); Assert.NotNull(agent.InterruptState);
        await agent.AbortInterruptsAsync("test completed");
    }
}

internal static class AgentRegistryFixtures
{
    internal static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    internal static ToolUseBlock Call(string name, string input = "{}") => new("call", name, Json(input));
    internal static FunctionTool Tool(string name, string result, JsonElement? schema = null, Action<ToolContext>? body = null) =>
        new(new(name, "Test tool.", schema ?? Json("{\"type\":\"object\"}")), (_, context, _) => { body?.Invoke(context); return new ValueTask<string>(result); });
}
internal sealed class OpaqueRegistryTool : ITool
{
    public ToolSpec Spec => throw new InvalidOperationException("Filtered Spec must not be read.");
    public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
}
internal sealed class RegistryToolPlugin(params ITool[] tools) : IPlugin
{
    public string Name => "test:registry-tools";
    public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => default;
    public IReadOnlyList<ITool> GetTools() => tools;
}
internal sealed class RegistryAgentModel : IModel
{
    public ModelRequest? LastRequest { get; private set; }
    public string? ToolName { get; init; }
    public Func<Task>? BeforeStart { get; init; }
    private int _calls;
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastRequest = request; if (BeforeStart is not null) await BeforeStart().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelMessageStartEvent();
        if (++_calls == 1 && ToolName is { } name)
        {
            yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("model-call", name, AgentRegistryFixtures.Json("{}")));
            yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}"); yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
        else
        {
            yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
