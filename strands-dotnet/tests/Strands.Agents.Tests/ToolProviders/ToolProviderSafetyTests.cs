using System.Text.Json;
using static Strands.Agents.Tests.ToolProviders.ToolProviderLifecycleTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class ToolProviderSafetyTests
{
    [Fact]
    public void InvalidDefinitionsFailBeforeAnyCallback()
    {
        Assert.Throws<ArgumentNullException>(() => new Agent(new() { Model = new Model(), ToolProviders = null! }));
        Assert.Throws<ArgumentNullException>(() => new Agent(new() { Model = new Model(), ToolProviders = [null!] }));
    }

    [Theory]
    [InlineData("add", "initialize")]
    [InlineData("add", "shutdown")]
    [InlineData("add", "dispose")]
    [InlineData("load", "initialize")]
    [InlineData("load", "shutdown")]
    [InlineData("load", "dispose")]
    [InlineData("remove", "initialize")]
    [InlineData("remove", "shutdown")]
    [InlineData("remove", "dispose")]
    public async Task ProviderCallbacksCannotAwaitTheirOwnLifecycle(string phase, string operation)
    {
        var observed = 0;
        async ValueTask Reenter(ToolProviderContext context)
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => operation switch
            {
                "initialize" => context.Agent.InitializeAsync(),
                "shutdown" => context.Agent.ShutdownAsync(),
                _ => context.Agent.DisposeAsync().AsTask()
            });
            observed++;
        }
        var provider = new Provider([]);
        if (phase == "add") provider.OnAdd = Reenter;
        if (phase == "load") provider.OnLoad = Reenter;
        if (phase == "remove") provider.OnRemove = Reenter;
        var agent = new Agent(new() { Model = new Model(), ToolProviders = [provider] });
        await agent.InitializeAsync(); await agent.DisposeAsync();
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task ParentAndChildHaveDistinctConsumersDespiteEqualAgentIdsAndBorrowProviders()
    {
        var active = new HashSet<string>(); var closed = 0;
        var provider = new Provider([Tool("shared")])
        {
            OnAdd = context => { Assert.True(active.Add(context.ConsumerId)); return default; },
            OnRemove = context => { Assert.True(active.Remove(context.ConsumerId)); if (active.Count == 0) closed++; return default; }
        };
        var parent = new Agent(new() { Model = new Model(), ToolProviders = [provider] });
        await parent.InitializeAsync();
        var child = parent.CreateChild(options => options with { Id = parent.Id });
        await child.InitializeAsync();
        Assert.Equal(parent.Id, child.Id); Assert.Equal(2, active.Count); Assert.Equal(0, closed);
        Assert.Same(Assert.Single(parent.Tools), Assert.Single(child.Tools));
        await parent.DisposeAsync(); Assert.Single(active); Assert.Equal(0, closed);
        await child.DisposeAsync(); Assert.Empty(active); Assert.Equal(1, closed); Assert.Equal(0, provider.Disposals);
        Assert.Equal(2, provider.Contexts.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentInitializationAndDisposalPublishOneTaskBeforeCallbacks()
    {
        var entered = Signal(); var release = Signal(); var removing = Signal(); var removed = Signal();
        var provider = new Provider([])
        {
            OnLoad = async _ => { entered.TrySetResult(true); await release.Task; },
            OnRemove = async _ => { removing.TrySetResult(true); await removed.Task; }
        };
        var agent = new Agent(new() { Model = new Model(), ToolProviders = [provider] });
        var first = agent.InitializeAsync(); await entered.Task;
        Assert.Same(first, agent.InitializeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ShutdownAsync());
        Assert.Throws<InvalidOperationException>(() => agent.DisposeAsync());
        release.TrySetResult(true); await first;
        var disposal = agent.DisposeAsync().AsTask(); await removing.Task;
        Assert.Same(disposal, agent.DisposeAsync().AsTask()); Assert.False(disposal.IsCompleted);
        removed.TrySetResult(true); await disposal;
        Assert.Equal(["add", "load", "remove"], provider.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedCancellationKeepsCallerTokenAndCleanupDiagnostics(bool cleanupFails)
    {
        using var caller = new CancellationTokenSource();
        var cause = new OperationCanceledException("caller canceled", caller.Token);
        var cleanup = new IOException("remove failed");
        var provider = new Provider([])
        {
            OnLoad = _ => { caller.Cancel(); throw cause; },
            OnRemove = _ => { if (cleanupFails) throw cleanup; return default; }
        };
        var agent = new Agent(new() { Model = new Model(), ToolProviders = [provider] });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InitializeAsync(caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Same(cause, error);
        if (cleanupFails) { Assert.Same(cause, error); Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]))); }
        Assert.Equal(["add", "load", "remove"], provider.Events);
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownHooksRunBeforeForwardAttemptedAllRemovalAndOwnedDisposal()
    {
        var order = new List<string>(); var primary = new IOException("hook"); var first = new ApplicationException("first remove"); var second = new ApplicationException("second remove");
        var one = new Provider([]) { OnRemove = _ => { order.Add("one"); throw first; } };
        var two = new Provider([]) { OnRemove = _ => { order.Add("two"); throw second; } };
        var hooks = new HookRegistry(); hooks.Register<ShutdownEvent>((_, _) => { order.Add("hook"); throw primary; });
        var agent = new Agent(new() { Model = new Model(), Hooks = hooks, ToolProviders = [one, two], OwnedResources = [new Resource(() => order.Add("resource"))] });
        await agent.InitializeAsync();
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));
        Assert.Equal(["hook", "one", "two", "resource"], order);
        var cleanup = Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]);
        Assert.Same(first, Assert.Single(cleanup)); Assert.Same(second, Assert.Single(Assert.IsType<List<Exception>>(first.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task RealConsumerFileHandleIsReleasedOnLaterInitializationFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".provider");
        FileStream? stream = null;
        var provider = new Provider([])
        {
            OnAdd = _ => { stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); return default; },
            OnRemove = _ => { stream!.Dispose(); return default; }
        };
        var agent = new Agent(new() { Model = new Model(), ToolProviders = [provider], Plugins = [new Plugin(_ => throw new IOException("plugin"), [])] });
        try
        {
            await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync());
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            await agent.DisposeAsync();
        }
        finally { stream?.Dispose(); File.Delete(path); }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Resource(Action cleanup) : IAsyncDisposable { public ValueTask DisposeAsync() { cleanup(); return default; } }
}
