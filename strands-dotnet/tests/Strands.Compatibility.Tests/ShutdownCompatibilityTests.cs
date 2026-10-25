using System.Runtime.CompilerServices;
using Strands.Agents;

namespace Strands.Compatibility.Tests;

public sealed class ShutdownCompatibilityTests
{
    [Fact]
    public async Task TerminalShutdownAwaitsCallbacksOnceBeforeOwnedModelDisposal()
    {
        var order = new List<string>();
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>(async (_, _) => { await Task.Yield(); order.Add("drain"); }, HookOrder.SdkLast);
        var model = new Model(() => order.Add("model"));
        var agent = new Agent(new() { Model = model, OwnsModel = true, Hooks = hooks });
        await agent.ShutdownAsync();
        await agent.ShutdownAsync();
        Assert.Equal(new[] { "drain" }, order);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.InvokeAsync("cannot restart"));
        await agent.DisposeAsync();
        Assert.Equal(new[] { "drain", "model" }, order);
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task FailedDrainsDoNotPreventRemainingCallbacksOrOwnedResourceCleanup()
    {
        var primary = new IOException("flush failed");
        var secondary = new InvalidOperationException("trigger failed");
        var resourceError = new ApplicationException("resource failed");
        var order = new List<string>();
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("first"); throw primary; });
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("second"); throw secondary; });
        hooks.Register<ShutdownEvent>((_, _) => { order.Add("last"); return default; }, HookOrder.SdkLast);
        var model = new Model(() => { order.Add("model"); throw resourceError; });
        var agent = new Agent(new() { Model = model, OwnsModel = true, Hooks = hooks });
        var error = await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask());
        Assert.Same(primary, error);
        Assert.Equal(new[] { "first", "second", "last", "model" }, order);
        Assert.Collection(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]),
            value => Assert.Same(secondary, value), value => Assert.Same(resourceError, value));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.ShutdownAsync()));
        Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task ActiveInvocationCannotShutDownItsOwnAgentAndRejectedShutdownDoesNotCloseIt()
    {
        var callbacks = 0;
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>((_, _) => { callbacks++; return default; });
        var agent = new Agent(new() { Model = new Model(() => { }), Hooks = hooks });
        await using (var stream = agent.StreamAsync("go").GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ShutdownAsync());
        }
        Assert.Equal(0, callbacks);
        Assert.Equal("reply", (await agent.InvokeAsync(null)).ToString());
        await agent.ShutdownAsync();
        Assert.Equal(1, callbacks);
        await agent.DisposeAsync();
        Assert.Equal(1, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedAsyncDrainCannotReenterAnAncestorLifecycle(bool disposeAncestor)
    {
        Agent? first = null;
        var nestedHooks = new HookRegistry();
        nestedHooks.Register<ShutdownEvent>(async (_, _) =>
        {
            await Task.Yield();
            Assert.Throws<InvalidOperationException>(() =>
            { if (disposeAncestor) _ = first!.DisposeAsync(); else _ = first!.ShutdownAsync(); });
        });
        var nested = new Agent(new() { Model = new Model(() => { }), Hooks = nestedHooks });
        var hooks = new HookRegistry();
        hooks.Register<ShutdownEvent>(async (_, _) => await nested.ShutdownAsync().ConfigureAwait(false));
        first = new Agent(new() { Model = new Model(() => { }), Hooks = hooks });
        await first.ShutdownAsync();
        await first.DisposeAsync();
        await nested.DisposeAsync();
    }

    private sealed class Model(Action dispose) : IModel, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "reply");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.Yield();
        }
        public ValueTask DisposeAsync() { Disposals++; dispose(); return default; }
    }
}
