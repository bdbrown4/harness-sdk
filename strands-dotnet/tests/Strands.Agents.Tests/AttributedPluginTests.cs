namespace Strands.Agents.Tests;

public sealed class AttributedPluginTests
{
    [Fact]
    public async Task HooksAndToolsFollowTheInitializerAndAsyncHooksAreAwaited()
    {
        var target = new AwaitedTarget();
        var plugin = PluginFactory.FromObject(target, "attributed", (_, _) =>
        {
            target.Order.Add("initializer");
            return default;
        });
        await using var agent = new Agent(new() { Model = new AttributedPluginTestModel(), Plugins = [plugin] });
        var initialization = agent.InitializeAsync();
        try
        {
            await Task.WhenAny(initialization, target.Entered.Task);
            Assert.False(initialization.IsCompleted);
            Assert.Equal(new[] { "initializer", "hook-enter" }, target.Order);
        }
        finally
        {
            target.Release.TrySetResult(true);
            await initialization;
        }
        Assert.Equal(new[] { "initializer", "hook-enter", "hook-leave" }, target.Order);
        Assert.Same(agent, target.SeenAgent);
        Assert.Equal("echo", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(0, target.UnmarkedCalls);
    }

    [Fact]
    public async Task InheritedDeclarationSlotsBindChildOverridesAndSuppressUnmarkedOverrides()
    {
        var target = new ChildTarget();
        await using var agent = new Agent(new()
        {
            Model = new AttributedPluginTestModel(),
            Plugins = [PluginFactory.FromObject(target, "ordered")]
        });
        await agent.InitializeAsync();
        Assert.Equal(new[] { "base-first", "child-override", "child-last" }, target.Order);
    }

    [Fact]
    public async Task LaterInitializationFailureRemovesAutomaticRegistrationsOnly()
    {
        var target = new CountingTarget();
        var applicationCalls = 0;
        var agent = new Agent(new()
        {
            Model = new AttributedPluginTestModel(),
            Tools = [ToolFactory.Create(() => "manual", new() { Name = "manual" })],
            Plugins = [PluginFactory.FromObject(target, "automatic"), new FailingPlugin()]
        });
        using var registration = agent.Hooks.Register<InitializedEvent>((_, _) =>
        {
            applicationCalls++;
            return default;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        await agent.Hooks.InvokeAsync(new InitializedEvent(agent));
        Assert.Equal(1, applicationCalls);
        Assert.Equal(0, target.Calls);
        Assert.Equal("manual", Assert.Single(agent.Tools).Spec.Name);
        try { await agent.DisposeAsync(); }
        catch (InvalidOperationException error) when (error.Message == "later-plugin-failed") { }
    }

    [Fact]
    public async Task UnmarkedInstanceRemainsInertAndCallerOwned()
    {
        var target = new UnmarkedTarget();
        await using (var agent = new Agent(new()
        {
            Model = new AttributedPluginTestModel(),
            Plugins = [PluginFactory.FromObject(target, "unmarked")]
        }))
        {
            await agent.InitializeAsync();
            Assert.Empty(agent.Tools);
        }
        Assert.Equal(0, target.Calls);
        Assert.Equal(0, target.Disposals);
    }

    private sealed class AwaitedTarget
    {
        public List<string> Order { get; } = [];
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Agent? SeenAgent { get; private set; }
        public int UnmarkedCalls { get; private set; }

        [Hook]
        public async Task Initialized(InitializedEvent value, CancellationToken cancellationToken)
        {
            SeenAgent = value.Agent;
            Order.Add("hook-enter");
            Entered.TrySetResult(true);
            await Release.Task;
            Order.Add("hook-leave");
        }

        [Tool("Echoes a value.", Name = "echo")]
        public string Echo(string value) => value;
        public void Unmarked(InitializedEvent value) => UnmarkedCalls++;
    }

    private class ParentTarget
    {
        public List<string> Order { get; } = [];
        [Hook] public void First(InitializedEvent value) => Order.Add("base-first");
        [Hook] public virtual void Replaced(InitializedEvent value) => Order.Add("base-override");
        [Hook] public virtual void Suppressed(InitializedEvent value) => Order.Add("base-suppressed");
    }

    private sealed class ChildTarget : ParentTarget
    {
        [Hook] public override void Replaced(InitializedEvent value) => Order.Add("child-override");
        public override void Suppressed(InitializedEvent value) => Order.Add("child-unmarked");
        [Hook] public void Last(InitializedEvent value) => Order.Add("child-last");
    }

    private sealed class CountingTarget
    {
        public int Calls { get; private set; }
        [Hook] public void Initialized(InitializedEvent value) => Calls++;
        [Tool("Automatic tool.", Name = "automatic")]
        public string Tool() => "automatic";
    }

    private sealed class FailingPlugin : IPlugin
    {
        public string Name => "failing";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("later-plugin-failed");
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class UnmarkedTarget : IDisposable
    {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public void Initialized(InitializedEvent value) => Calls++;
        public string Tool() { Calls++; return "unused"; }
        public void Dispose() => Disposals++;
    }
}
