using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Interventions;
using Strands.Agents.Interventions.Steering;

namespace Strands.Agents.Tests.Interventions;

public sealed class SteeringHandlerTests
{
    [Fact]
    public async Task ObserversRunOnceAfterPluginsAndBeforeInitializedCallbacks()
    {
        var order = new List<string>(); var hooks = new HookRegistry(); hooks.Register<InitializedEvent>((_, _) => { order.Add("initialized"); return default; });
        await using var agent = new Agent(new() { Model = new NoCallsModel(), Hooks = hooks, Plugins = [new Plugin(order)], Interventions = [new Observer(order)] });
        await agent.InitializeAsync(); await agent.InitializeAsync();
        Assert.Equal(new[] { "plugin", "observer", "initialized" }, order);
    }

    [Fact]
    public async Task ContextualGuidanceSeesDetachedProviderDataAndPreventsToolExecution()
    {
        var provider = new Provider("data"); var calls = 0;
        var policy = new Policy(new() { ContextProviders = [provider] }) { Before = (self, value) => { var context = Assert.Single(self.GetSteeringContext(value.Agent)); Assert.Equal(42, context.GetProperty("value").GetInt32()); return new Guide("choose another action"); } };
        await using var agent = new Agent(new() { Model = new ToolModel(), Interventions = [policy], Tools = [Tool(() => calls++)] });
        await agent.InvokeAsync("task");
        Assert.Equal(0, calls); Assert.Single(provider.Bindings);
        Assert.Contains("GUIDANCE: [steering] choose another action", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task FailedObservationRollsBackEveryOwnedBindingInReverseOrderAndCachesTheOriginalError()
    {
        var order = new List<string>(); var failure = new IOException("third failed");
        var first = new Provider("first", order, new IOException("first cleanup")); var second = new Provider("second", order, new IOException("second cleanup"));
        var third = new Provider("third") { Failure = failure };
        var policy = new Policy(new() { ContextProviders = [first, second, third] });
        await using var agent = new Agent(new() { Model = new NoCallsModel(), Interventions = [policy] });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Equal(new[] { "second", "first" }, order);
        Assert.Equal(new[] { "second cleanup", "first cleanup" }, Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]).Select(error => error.Message));
        Assert.Equal(1, first.Creates); Assert.Equal(1, second.Creates); Assert.Equal(1, third.Creates);
        await agent.Hooks.InvokeAsync(new BeforeToolCallEvent(agent, new("id", "effect", JsonSerializer.SerializeToElement(new { })), new Dictionary<string, object?>()));
        Assert.All(first.Bindings.Concat(second.Bindings), binding => Assert.Equal(0, binding.Callbacks));
    }

    [Fact]
    public async Task CancelledObservationRetainsTheOriginalCancellationAndRollbackErrors()
    {
        using var cancellation = new CancellationTokenSource(); var failure = new OperationCanceledException(cancellation.Token); var cleanup = new IOException("rollback failed");
        var first = new Provider("first", cleanup: cleanup); var second = new Provider("second") { Creating = cancellation.Cancel, Failure = failure };
        await using var agent = new Agent(new() { Model = new NoCallsModel(), Interventions = [new Policy(new() { ContextProviders = [first, second] })] });
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InitializeAsync(cancellation.Token));
        Assert.Same(failure, actual); Assert.Contains(cleanup, Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, Assert.Single(first.Bindings).Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALaterObserverOrInitializedCallbackFailureClosesEarlierSteeringBindings(bool initializedCallback)
    {
        var failure = new IOException("later initialization failed"); var provider = new Provider("data"); var hooks = new HookRegistry();
        if (initializedCallback) hooks.Register<InitializedEvent>((_, _) => throw failure);
        var policies = new List<InterventionHandler> { new Policy(new() { ContextProviders = [provider] }) };
        if (!initializedCallback) policies.Add(new FailingObserver(failure));
        await using var agent = new Agent(new() { Model = new NoCallsModel(), Hooks = hooks, Interventions = policies });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Equal(1, Assert.Single(provider.Bindings).Disposals);
        await agent.Hooks.InvokeAsync(new BeforeToolCallEvent(agent, new("call", "effect", JsonSerializer.SerializeToElement(new { })), new Dictionary<string, object?>()));
        Assert.Equal(0, provider.Bindings[0].Callbacks);
    }

    [Fact]
    public async Task SharedHandlersBindIndependentlyAndDisposingAParentDoesNotCloseItsChild()
    {
        var provider = new Provider("data"); var policy = new Policy(new() { ContextProviders = [provider] });
        var parent = new Agent(new() { Model = new NoCallsModel(), Interventions = [policy] });
        await using var child = parent.CreateChild();
        await parent.InitializeAsync(); await child.InitializeAsync();
        var call = new ToolUseBlock("same", "effect", JsonSerializer.SerializeToElement(new { }));
        await parent.Hooks.InvokeAsync(new BeforeToolCallEvent(parent, call, new Dictionary<string, object?>()));
        await child.Hooks.InvokeAsync(new BeforeToolCallEvent(child, call, new Dictionary<string, object?>()));
        var retained = Assert.Single(policy.GetSteeringContext(parent));
        await parent.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => policy.GetSteeringContext(parent));
        await child.Hooks.InvokeAsync(new BeforeToolCallEvent(child, call, new Dictionary<string, object?>()));
        Assert.Equal(1, retained.GetProperty("callbacks").GetInt32()); Assert.Equal(2, Assert.Single(policy.GetSteeringContext(child)).GetProperty("callbacks").GetInt32());
        Assert.Equal(1, provider.Bindings[0].Disposals); Assert.Equal(0, provider.Bindings[1].Disposals);
    }

    [Fact]
    public async Task ShutdownAttemptsEveryBindingAndKeepsTheSameCachedFailure()
    {
        var order = new List<string>(); var one = new IOException("first cleanup"); var two = new IOException("second cleanup");
        var first = new Provider("first", order, one); var second = new Provider("second", order, two);
        var agent = new Agent(new() { Model = new NoCallsModel(), Interventions = [new Policy(new() { ContextProviders = [first, second] })] });
        await agent.InitializeAsync();
        Assert.Same(two, await Assert.ThrowsAsync<IOException>(() => agent.ShutdownAsync()));
        Assert.Contains(one, Assert.IsType<List<Exception>>(two.Data["Strands.CleanupErrors"])); Assert.Equal(new[] { "second", "first" }, order);
        Assert.Same(two, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));
        Assert.All(first.Bindings.Concat(second.Bindings), binding => Assert.Equal(1, binding.Disposals));
    }

    [Theory]
    [InlineData(InterventionErrorPolicy.Throw)]
    [InlineData(InterventionErrorPolicy.Proceed)]
    public async Task SteeringFailureCanUseTypeScriptThrowOrPythonProceedPolicy(InterventionErrorPolicy policy)
    {
        var failure = new IOException("steering failed"); var calls = 0;
        await using var agent = new Agent(new() { Model = new ToolModel(), Tools = [Tool(() => calls++)], Interventions = [new Policy(new()) { ErrorPolicy = policy, Before = (_, _) => throw failure }] });
        if (policy == InterventionErrorPolicy.Throw) Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("task")));
        else Assert.Equal("done", (await agent.InvokeAsync("task")).ToString());
        Assert.Equal(policy == InterventionErrorPolicy.Proceed ? 1 : 0, calls);
    }

    [Fact]
    public async Task ProviderSnapshotsOutliveProducerJsonAndAgentShutdown()
    {
        using var document = JsonDocument.Parse("{\"type\":\"custom\",\"value\":42}");
        var provider = new DocumentProvider(document); var handler = new Policy(new() { ContextProviders = [provider] });
        var agent = new Agent(new() { Model = new NoCallsModel(), Interventions = [handler] });
        await agent.InitializeAsync(); var snapshot = Assert.Single(handler.GetSteeringContext(agent));
        document.Dispose(); await agent.DisposeAsync();
        Assert.Equal(42, snapshot.GetProperty("value").GetInt32()); Assert.True(provider.Disposed);
    }

    private static FunctionTool Tool(Action effect) => new(new("effect", "effect", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { effect(); return new ValueTask<string>("executed"); });
    private sealed class Policy(SteeringHandlerOptions options) : SteeringHandler(options)
    {
        public override string Name => "steering";
        internal InterventionErrorPolicy ErrorPolicy { get; init; }
        public override InterventionErrorPolicy OnError => ErrorPolicy;
        internal Func<Policy, BeforeToolCallEvent, IToolSteeringAction>? Before { get; init; }
        public override ValueTask<IToolSteeringAction> SteerBeforeToolAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default) => new(Before?.Invoke(this, value) ?? new Proceed());
    }
    private sealed class Observer(List<string> order) : InterventionHandler, IAgentLifecycleObserver
    {
        public override string Name => "observer";
        public ValueTask ObserveAgentAsync(Agent agent, CancellationToken cancellationToken = default) { order.Add("observer"); return default; }
    }
    private sealed class FailingObserver(Exception error) : InterventionHandler, IAgentLifecycleObserver
    {
        public override string Name => "failing observer";
        public ValueTask ObserveAgentAsync(Agent agent, CancellationToken cancellationToken = default) => throw error;
    }
    private sealed class Plugin(List<string> order) : IPlugin
    {
        public string Name => "plugin";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { order.Add("plugin"); return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class Provider(string name, List<string>? disposal = null, Exception? cleanup = null) : ISteeringContextProvider
    {
        public string Name => name;
        internal Exception? Failure { get; init; }
        internal Action? Creating { get; init; }
        internal int Creates { get; private set; }
        internal List<Binding> Bindings { get; } = [];
        public ValueTask<ISteeringContext> CreateAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            Creates++; Creating?.Invoke(); if (Failure is { } error) throw error;
            var value = new Binding(agent, name, disposal, cleanup); Bindings.Add(value); return new((ISteeringContext)value);
        }
    }
    private sealed class Binding : ISteeringContext
    {
        private readonly IDisposable _registration; private readonly string _name; private readonly List<string>? _disposal; private readonly Exception? _cleanup;
        internal int Callbacks { get; private set; }
        internal int Disposals { get; private set; }
        internal Binding(Agent agent, string name, List<string>? disposal, Exception? cleanup)
        {
            _name = name; _disposal = disposal; _cleanup = cleanup;
            _registration = agent.Hooks.Register<BeforeToolCallEvent>((_, _) => { Callbacks++; return default; });
        }
        public JsonElement Context => JsonSerializer.SerializeToElement(new { type = _name, value = 42, callbacks = Callbacks });
        public ValueTask DisposeAsync() { Disposals++; _registration.Dispose(); _disposal?.Add(_name); if (_cleanup is { } error) throw error; return default; }
    }
    private sealed class DocumentProvider(JsonDocument document) : ISteeringContextProvider, ISteeringContext
    {
        public string Name => "document";
        internal bool Disposed { get; private set; }
        public JsonElement Context => document.RootElement;
        public ValueTask<ISteeringContext> CreateAsync(Agent agent, CancellationToken cancellationToken = default) => new((ISteeringContext)this);
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
    private sealed class NoCallsModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The model must not be called.");
    }
    private sealed class ToolModel : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent();
            if (_calls++ == 0) yield return new ModelContentBlockStartEvent(0, new("call", "effect", JsonSerializer.SerializeToElement(new { })));
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); }
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(_calls == 1 ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
