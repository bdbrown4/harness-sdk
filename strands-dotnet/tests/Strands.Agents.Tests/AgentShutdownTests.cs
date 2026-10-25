using System.Runtime.CompilerServices;

namespace Strands.Agents.Tests;

public sealed class AgentShutdownTests
{
    [Fact]
    public async Task ShutdownRunsAllPrioritiesOnceAndMakesAgentTerminal()
    {
        var order = new List<string>();
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("sdk-last"); return default; }, HookOrder.SdkLast);
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("first"); return default; });
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("second"); return default; });
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("sdk-first"); return default; }, HookOrder.SdkFirst);
        var agent = new Agent(new() { Model = new Model(), Hooks = hooks });
        await agent.ShutdownAsync();
        await agent.ShutdownAsync();
        Assert.Equal(["sdk-first", "first", "second", "sdk-last"], order);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.InvokeAsync("cannot restart"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.InitializeAsync());
        Assert.Throws<ObjectDisposedException>(() => agent.CreateChild());
    }

    [Fact]
    public async Task ShutdownAttemptsEveryCallbackAndCachesFirstFailureWithSecondaryErrors()
    {
        var primary = new IOException("first drain failed");
        var secondary = new InvalidOperationException("second drain failed");
        var order = new List<string>();
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("first"); throw primary; });
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("second"); throw secondary; });
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("last"); return default; }, HookOrder.SdkLast);
        var agent = new Agent(new() { Model = new Model(), Hooks = hooks });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.ShutdownAsync()));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.ShutdownAsync()));
        Assert.Equal(["first", "second", "last"], order);
        Assert.Same(secondary, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task DisposeWaitsForShutdownBeforeReleasingOwnedResourcesAndModel()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>(async (_, _) => { started.TrySetResult(true); await finish.Task; order.Add("drained"); });
        var resource = new Resource(() => order.Add("resource"));
        var model = new Model { OnDispose = () => order.Add("model") };
        var agent = new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [resource], Hooks = hooks });
        var disposal = agent.DisposeAsync().AsTask();
        try
        {
            await Task.WhenAny(started.Task, disposal);
            Assert.True(started.Task.IsCompleted);
            Assert.False(disposal.IsCompleted);
            Assert.Empty(order);
        }
        finally { finish.TrySetResult(true); }
        await disposal;
        Assert.Equal(["drained", "resource", "model"], order);
    }

    [Fact]
    public async Task ShutdownFailureDoesNotPreventResourceOrModelDisposal()
    {
        var primary = new IOException("flush failed");
        var resourceFailure = new InvalidOperationException("resource failed");
        var modelFailure = new ApplicationException("model failed");
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => throw primary);
        var resource = new Resource(() => throw resourceFailure);
        var model = new Model { OnDispose = () => throw modelFailure };
        var agent = new Agent(new() { Model = model, Hooks = hooks, OwnsModel = true, OwnedResources = [resource] });
        var error = await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask());
        Assert.Same(primary, error);
        Assert.Collection(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]),
            value => Assert.Same(resourceFailure, value), value => Assert.Same(modelFailure, value));
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(1, model.Disposals);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task StandaloneShutdownKeepsOwnedDependenciesUntilDispose()
    {
        var resource = new Resource(() => { });
        var model = new Model();
        var agent = new Agent(new() { Model = model, OwnsModel = true, OwnedResources = [resource] });
        await agent.ShutdownAsync();
        Assert.Equal(0, resource.Disposals);
        Assert.Equal(0, model.Disposals);
        await agent.DisposeAsync();
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task ActiveInvocationRejectsShutdownWithoutMakingAgentTerminal()
    {
        var agent = new Agent(new() { Model = new Model() });
        await using (var stream = agent.StreamAsync("go").GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ShutdownAsync());
        }
        Assert.Equal("reply", (await agent.InvokeAsync(null)).ToString());
        await agent.ShutdownAsync();
    }

    [Fact]
    public async Task ActiveInitializationRejectsShutdownWithoutMakingAgentTerminal()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializer = new Initializer(async (_, _) => { started.TrySetResult(true); await finish.Task; });
        var agent = new Agent(new() { Model = new Model(), Plugins = [initializer] });
        var initialization = agent.InitializeAsync();
        await started.Task;
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ShutdownAsync()); }
        finally { finish.TrySetResult(true); }
        await initialization;
        Assert.Equal("reply", (await agent.InvokeAsync("go")).ToString());
        await agent.ShutdownAsync();
    }

    [Fact]
    public async Task ConcurrentShutdownAndDisposeShareOneAwaitedDrain()
    {
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drains = 0;
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>(async (_, _) => { drains++; await finish.Task; });
        var model = new Model();
        var agent = new Agent(new() { Model = model, OwnsModel = true, Hooks = hooks });
        var shutdown = agent.ShutdownAsync();
        var disposal = agent.DisposeAsync().AsTask();
        try { Assert.Equal(1, drains); Assert.False(disposal.IsCompleted); Assert.Equal(0, model.Disposals); }
        finally { finish.TrySetResult(true); }
        await Task.WhenAll(shutdown, disposal);
        Assert.Equal(1, drains);
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task ShutdownDoesNotInitializeUnusedPluginsOrDisposeBorrowedModel()
    {
        var initializations = 0;
        var initializer = new Initializer((_, _) => { initializations++; return default; });
        var model = new Model();
        var agent = new Agent(new() { Model = model, Plugins = [initializer] });
        await agent.ShutdownAsync();
        await agent.DisposeAsync();
        Assert.Equal(0, initializations);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task ShutdownCallbackCannotAwaitItsOwnShutdownTask()
    {
        var hooks = new HookRegistry();
        var lastRan = false;
        hooks.Register<ShutdownEvent>((value, cancellationToken) =>
        { Assert.Throws<InvalidOperationException>(() => { _ = value.Agent.ShutdownAsync(); }); return default; });
        hooks.Register<ShutdownEvent>((_, _) => { lastRan = true; return default; }, HookOrder.SdkLast);
        var agent = new Agent(new() { Model = new Model(), Hooks = hooks });
        await agent.ShutdownAsync();
        Assert.True(lastRan);
    }

    [Fact]
    public async Task ShutdownCallbackCannotStartItsOwnDisposal()
    {
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((value, cancellationToken) =>
        { Assert.Throws<InvalidOperationException>(() => { _ = value.Agent.DisposeAsync(); }); return default; });
        var resource = new Resource(() => { });
        var agent = new Agent(new() { Model = new Model(), Hooks = hooks, OwnedResources = [resource] });
        await agent.ShutdownAsync();
        Assert.Equal(0, resource.Disposals);
        await agent.DisposeAsync();
        Assert.Equal(1, resource.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedShutdownCannotAwaitAnAncestorLifecycle(bool disposeAncestor)
    {
        Agent? first = null;
        var nestedHooks = new HookRegistry();
        nestedHooks.Register<ShutdownEvent>((_, _) =>
        {
            Assert.Throws<InvalidOperationException>(() =>
            { if (disposeAncestor) _ = first!.DisposeAsync(); else _ = first!.ShutdownAsync(); });
            return default;
        });
        var nested = new Agent(new() { Model = new Model(), Hooks = nestedHooks });
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>(async (_, _) => await nested.ShutdownAsync());
        first = new Agent(new() { Model = new Model(), Hooks = hooks });
        await first.ShutdownAsync();
        await first.DisposeAsync();
        await nested.DisposeAsync();
    }

    [Fact]
    public async Task OwnedResourceCannotAwaitItsOwnersDisposal()
    {
        Agent? agent = null;
        var resource = new Resource(() => Assert.Throws<InvalidOperationException>(() => { _ = agent!.DisposeAsync(); }));
        agent = new Agent(new() { Model = new Model(), OwnedResources = [resource] });
        await agent.DisposeAsync();
        Assert.Equal(1, resource.Disposals);
    }

    private sealed class Initializer(Func<Agent, CancellationToken, ValueTask> initialize) : IPlugin
    {
        public string Name => "test:initializer";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => initialize(agent, cancellationToken);
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class Resource(Action dispose) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; dispose(); return default; }
    }
    private sealed class Model : IModel, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        internal Action? OnDispose { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { foreach (var value in AgentTests.Text("reply")) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); } }
        public ValueTask DisposeAsync() { Disposals++; OnDispose?.Invoke(); return default; }
    }
}
