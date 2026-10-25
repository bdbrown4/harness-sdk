using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Tests.ToolProviders;

namespace Strands.Agents.Tests;

public sealed class AgentInitializationCancellationTests
{
    [Fact]
    public async Task PreCanceledInitializationDoesNotLoadOrInitializeAndCanBeRetried()
    {
        using var caller = new CancellationTokenSource();
        var store = new Store();
        var plugin = new Plugin();
        await using var agent = new Agent(new() { Model = new Model(), SessionId = "initialization", SessionStore = store, Plugins = [plugin] });
        caller.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InitializeAsync(caller.Token));

        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(0, store.Loads);
        Assert.Equal(0, plugin.Initializations);
        Assert.Null(agent.State.Get("restored"));
        var initialization = agent.InitializeAsync();
        await initialization;
        Assert.Same(initialization, agent.InitializeAsync());
        Assert.Equal(1, store.Loads);
        Assert.Equal(1, plugin.Initializations);
        Assert.Equal("accepted", agent.State.Get<string>("restored"));
    }

    [Fact]
    public async Task AdmittedCancellationCachesAcceptedRestoreAndPluginEffectsWithoutRetrying()
    {
        using var caller = new CancellationTokenSource();
        var model = new Model();
        var store = new Store();
        var owned = new Resource();
        var effects = 0;
        var shutdowns = 0;
        var plugin = new Plugin((agent, token) =>
        {
            Assert.Equal("accepted", agent.State.Get<string>("restored"));
            agent.State.Set("plugin-effect", ++effects);
            agent.Hooks.Register<ShutdownEvent>((_, _) => { shutdowns++; return default; });
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            return default;
        });
        await using var agent = new Agent(new()
        {
            Model = model,
            SessionId = "initialization",
            SessionStore = store,
            Plugins = [plugin],
            OwnedResources = [owned]
        });

        var initialization = agent.InitializeAsync(caller.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.Equal(caller.Token, error.CancellationToken);
        var repeated = agent.InitializeAsync();
        Assert.Same(initialization, repeated);
        var repeatedError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repeated);
        Assert.Equal(caller.Token, repeatedError.CancellationToken);
        Assert.Equal(1, store.Loads);
        Assert.Equal(1, plugin.Initializations);
        Assert.Equal(1, effects);
        Assert.Equal("accepted", agent.State.Get<string>("restored"));
        Assert.Equal(1, agent.State.Get<int>("plugin-effect"));
        Assert.Equal(0, model.Calls);

        await agent.DisposeAsync();
        await agent.DisposeAsync();
        Assert.Equal(1, shutdowns);
        Assert.Equal(1, owned.Disposals);
        Assert.Equal(0, model.Disposals);

        var freshPlugin = new Plugin();
        await using var fresh = new Agent(new() { Model = model, SessionId = "initialization", SessionStore = store, Plugins = [freshPlugin] });
        Assert.Equal("reply", (await fresh.InvokeAsync("continue")).ToString());
        Assert.Equal(2, store.Loads);
        Assert.Equal(1, freshPlugin.Initializations);
        Assert.Equal(1, plugin.Initializations);
        Assert.Equal(1, effects);
        Assert.Equal(1, model.Calls);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task CachedCancellationRetainsCleanupDiagnosticsAndDoesNotRepeatRemoval()
    {
        using var caller = new CancellationTokenSource();
        var primary = new OperationCanceledException("initialization canceled", caller.Token);
        var cleanup = new IOException("consumer removal failed");
        var provider = new ToolProviderLifecycleTests.Provider([])
        {
            OnLoad = _ => { caller.Cancel(); throw primary; },
            OnRemove = _ => throw cleanup
        };
        var owned = new Resource();
        await using var agent = new Agent(new() { Model = new Model(), ToolProviders = [provider], OwnedResources = [owned] });

        var initialization = agent.InitializeAsync(caller.Token);
        Assert.Same(primary, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization));
        Assert.Equal(caller.Token, primary.CancellationToken);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        var repeated = agent.InitializeAsync();
        Assert.Same(initialization, repeated);
        Assert.Same(primary, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repeated));
        Assert.Equal(["add", "load", "remove"], provider.Events);

        await agent.DisposeAsync();
        await agent.DisposeAsync();
        Assert.Equal(["add", "load", "remove"], provider.Events);
        Assert.Equal(1, owned.Disposals);
        Assert.Equal(0, provider.Disposals);
    }

    private sealed class Store : ISessionStore
    {
        internal int Loads { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Loads++;
            cancellationToken.ThrowIfCancellationRequested();
            return new(new SessionSnapshot(1, []) { State = new Dictionary<string, JsonElement> { ["restored"] = JsonSerializer.SerializeToElement("accepted") } });
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }

    private sealed class Plugin(Func<Agent, CancellationToken, ValueTask>? initialize = null) : IPlugin
    {
        internal int Initializations { get; private set; }
        public string Name => "test:initialization-cancellation";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            Initializations++;
            return initialize?.Invoke(agent, cancellationToken) ?? default;
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class Resource : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class Model : IModel, IAsyncDisposable
    {
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "reply");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent("end_turn");
            await Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
