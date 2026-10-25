using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class PluginStateTests
{
    [Fact]
    public void ApplicationValuesAreDetachedAndFailedSerializationPreservesThePreviousValue()
    {
        var values = new[] { "one" };
        var store = new StateStore();
        store.Set("values", values);
        values[0] = "changed";
        var returned = store.Get<string[]>("values")!;
        returned[0] = "also changed";
        Assert.Equal("one", store.Get<string[]>("values")![0]);
        Assert.Throws<JsonException>(() => store.Set("values", new Cyclic()));
        Assert.Equal("one", store.Get<string[]>("values")![0]);
        Assert.True(store.Delete("values"));
        Assert.Null(store.Get("values"));
    }

    [Fact]
    public async Task PluginsInitializeOnceBeforeToolsAreDiscoveredAndReceiveRestoredState()
    {
        var snapshot = new SessionSnapshot(1, []) { State = new Dictionary<string, JsonElement> { ["restored"] = JsonSerializer.SerializeToElement(7) } };
        var plugin = new TestPlugin();
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Text("one"), AgentTests.Text("two")), Plugins = [plugin], SessionStore = new Store(snapshot), SessionId = "session" });
        await agent.InitializeAsync();
        await agent.InvokeAsync("first");
        await agent.InvokeAsync("second");
        Assert.Equal(1, plugin.Initializations);
        Assert.Equal(7, plugin.RestoredValue);
        Assert.Equal("extension", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task FailedInitializationIsCachedAndDoesNotRegisterHalfAToolBatch()
    {
        var plugin = new TestPlugin { DuplicateTools = true };
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(), Plugins = [plugin] });
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Same(first, second);
        Assert.Equal(1, plugin.Initializations);
        Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task PluginHooksBelongToEachAgentWhileLateApplicationHooksRemainLive()
    {
        var shared = new HookRegistry();
        var plugin = new TestPlugin();
        var first = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Text("one")), Hooks = shared, Plugins = [plugin] });
        var second = new Agent(new() { Model = new AgentTests.ScriptedModel(AgentTests.Text("two")), Hooks = shared, Plugins = [plugin] });
        await first.InitializeAsync();
        await second.InitializeAsync();
        var applicationCalls = 0;
        using var registration = shared.Register<BeforeInvocationEvent>((_, _) => { applicationCalls++; return ValueTask.CompletedTask; });
        await first.InvokeAsync("first");
        await second.InvokeAsync("second");
        Assert.Equal(2, applicationCalls);
        Assert.Equal(1, first.State.Get<int>("hookCalls"));
        Assert.Equal(1, second.State.Get<int>("hookCalls"));
    }

    private sealed class Cyclic { public Cyclic Self => this; }
    private sealed class Store(SessionSnapshot snapshot) : ISessionStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken cancellationToken = default) => new(snapshot);
        public ValueTask SaveAsync(string id, SessionSnapshot value, CancellationToken cancellationToken = default) => default;
    }
    private sealed class TestPlugin : IPlugin
    {
        public string Name => "test";
        public int Initializations { get; private set; }
        public int RestoredValue { get; private set; }
        public bool DuplicateTools { get; init; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            Initializations++;
            RestoredValue = agent.State.Get<int>("restored");
            agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { value.Agent.State.Set("hookCalls", value.Agent.State.Get<int>("hookCalls") + 1); return ValueTask.CompletedTask; });
            return default;
        }
        public IReadOnlyList<ITool> GetTools()
        {
            Assert.True(Initializations > 0);
            var tool = new FunctionTool(new("extension", "Extension", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new("ok"));
            return DuplicateTools ? [tool, tool] : [tool];
        }
    }
}
