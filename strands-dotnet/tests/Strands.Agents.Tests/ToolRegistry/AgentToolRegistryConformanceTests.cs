namespace Strands.Agents.Tests.ToolRegistries;

public sealed class AgentToolRegistryConformanceTests
{
    [Fact]
    public async Task RemovalAndReaddAppendAfterRetainedSlotsOnEveryRuntime()
    {
        var model = new RegistryAgentModel();
        await using var agent = new Agent(new() { Model = model, DirectToolNameResolution = ToolNameResolution.TypeScriptDirect, Tools = [AgentRegistryFixtures.Tool("case", "first"), AgentRegistryFixtures.Tool("CASE", "second"), AgentRegistryFixtures.Tool("third", "third")] });
        Assert.True(agent.RemoveApplicationTool("case")); agent.AddTool(AgentRegistryFixtures.Tool("case", "readded"));
        Assert.Equal(new[] { "CASE", "third", "case" }, agent.Tools.Select(value => value.Spec.Name));
        Assert.Equal("second", (await agent.InvokeToolAsync(AgentRegistryFixtures.Call("Case"))).Content);
        Assert.Equal("readded", (await agent.InvokeToolAsync(AgentRegistryFixtures.Call("case"))).Content);
        await agent.InvokeAsync("advertise");
        Assert.Equal(new[] { "CASE", "third", "case" }, model.LastRequest!.Tools!.Select(value => value.Name));
    }

    [Fact]
    public void AddOrReplaceUsesTheLastExactValueAndPreservesExistingPositions()
    {
        var first = AgentRegistryFixtures.Tool("first", "old"); var second = AgentRegistryFixtures.Tool("second", "second");
        var last = AgentRegistryFixtures.Tool("first", "last"); var third = AgentRegistryFixtures.Tool("third", "third");
        var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [first, second] });
        agent.AddOrReplaceApplicationTools([AgentRegistryFixtures.Tool("first", "intermediate"), third, last]);
        Assert.Equal(new ITool[] { last, second, third }, agent.Tools);
        Assert.Throws<KeyNotFoundException>(() => agent.ReplaceApplicationTool(AgentRegistryFixtures.Tool("absent", "absent")));
        Assert.Equal(new ITool[] { last, second, third }, agent.CreateChild().Tools);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalLifecycleRejectsEveryMutation(bool shutdown)
    {
        var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [AgentRegistryFixtures.Tool("existing", "old")] });
        if (shutdown) await agent.ShutdownAsync(); else await agent.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => agent.AddTool(AgentRegistryFixtures.Tool("new", "new")));
        Assert.Throws<ObjectDisposedException>(() => agent.ReplaceApplicationTool(AgentRegistryFixtures.Tool("existing", "new")));
        Assert.Throws<ObjectDisposedException>(() => agent.AddOrReplaceApplicationTool(AgentRegistryFixtures.Tool("new", "new")));
        Assert.Throws<ObjectDisposedException>(() => agent.RemoveApplicationTool("existing")); Assert.Throws<ObjectDisposedException>(() => agent.ClearApplicationTools());
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task DefaultChildrenInheritPoliciesAndApplicationsButNeverEphemeralTools()
    {
        await using var agent = new Agent(new() { Model = new RegistryAgentModel(), ToolNameConflictPolicy = ToolNameConflictPolicy.RejectNormalized, DirectToolNameResolution = ToolNameResolution.UnderscoreAlias, Tools = [AgentRegistryFixtures.Tool("old", "old")] });
        agent.ClearApplicationTools(); agent.AddOrReplaceApplicationTool(AgentRegistryFixtures.Tool("my-tool", "current"));
        await agent.InvokeAsync("temporary", new() { EphemeralTools = [AgentRegistryFixtures.Tool("temporary", "temporary")] });
        await using var child = agent.CreateChild();
        Assert.Equal("my-tool", Assert.Single(child.Tools).Spec.Name);
        Assert.Equal("current", (await child.InvokeToolAsync(AgentRegistryFixtures.Call("my_tool"))).Content);
        Assert.Throws<ArgumentException>(() => child.AddTool(AgentRegistryFixtures.Tool("my_tool", "conflict")));
        Assert.Equal(ToolResultStatus.Error, (await child.InvokeToolAsync(AgentRegistryFixtures.Call("temporary"))).Status);
        child.ClearApplicationTools(); Assert.Single(agent.Tools);
    }

    [Fact]
    public void IndependentMutationsInsideAnApplicationFilterAreNotLostByTheOuterBatch()
    {
        Agent? agent = null;
        var outer = AgentRegistryFixtures.Tool("outer", "outer"); var inner = AgentRegistryFixtures.Tool("inner", "inner");
        agent = new Agent(new() { Model = new RegistryAgentModel(), ToolFilter = value => { if (ReferenceEquals(value, outer)) agent!.AddTool(inner); return true; } });
        agent.AddTool(outer);
        Assert.Equal(new ITool[] { inner, outer }, agent.Tools);
    }
}
