namespace Strands.Agents.Tests;

public sealed class AttributedPluginParentSlotTests
{
    [Fact]
    public async Task ChildMarkedHookRetainsItsPreviouslyUnmarkedParentSlot()
    {
        var target = new ChildHooks();
        await using var agent = new Agent(new() { Model = new AttributedPluginTestModel(), Plugins = [PluginFactory.FromObject(target, "slots")] });
        await agent.InitializeAsync();
        await agent.Hooks.InvokeAsync(new BeforeInvocationEvent(agent, new Dictionary<string, object?>()));
        Assert.Equal(new[] { "child-override", "parent-marked", "child-first-declared" }, target.Calls);
    }

    [Fact]
    public void ChildMarkedToolRetainsItsPreviouslyUnmarkedParentSlot()
    {
        var plugin = PluginFactory.FromObject(new ChildTools(), "slots");
        Assert.Equal(new[] { "child-override", "parent-marked", "child-first-declared" }, plugin.GetTools().Select(tool => tool.Spec.Name));
    }

    private class ParentHooks
    {
        public List<string> Calls { get; } = [];
        public virtual void PreviouslyUnmarked(BeforeInvocationEvent value) => Calls.Add("unmarked-parent");
        [Hook]
        public void MarkedParent(BeforeInvocationEvent value) => Calls.Add("parent-marked");
    }

    private sealed class ChildHooks : ParentHooks
    {
        [Hook]
        public void FirstDeclaredChild(BeforeInvocationEvent value) => Calls.Add("child-first-declared");
        [Hook]
        public override void PreviouslyUnmarked(BeforeInvocationEvent value) => Calls.Add("child-override");
    }

    private class ParentTools
    {
        public virtual string PreviouslyUnmarked() => "unmarked-parent";
        [Tool("Parent method.", Name = "parent-marked")]
        public string MarkedParent() => "parent";
    }

    private sealed class ChildTools : ParentTools
    {
        [Tool("First child method.", Name = "child-first-declared")]
        public string FirstDeclaredChild() => "first";
        [Tool("Child override.", Name = "child-override")]
        public override string PreviouslyUnmarked() => "override";
    }
}
