using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiHookTests
{
    [Fact]
    public async Task StopUnwindsEqualPrioritiesAndAttemptsAllCallbacksAfterCleanup()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var seen = new List<int>(); var first = new IOException("last registered cleanup"); var second = new IOException("earlier cleanup");
        agent.Hooks.Register<BidiAgentStopEvent>((value, token) => { Assert.Same(agent, value.Agent); Assert.False(token.CanBeCanceled); Assert.Equal(1, model.Stops); seen.Add(1); return default; }, -100);
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { seen.Add(2); throw second; });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { seen.Add(3); throw first; });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { seen.Add(4); return default; }, 100);
        await agent.StartAsync();

        Assert.Same(first, await Assert.ThrowsAsync<IOException>(() => agent.StopAsync()));

        Assert.Equal(new[] { 1, 3, 2, 4 }, seen);
        Assert.Same(second, Assert.Single(Assert.IsType<List<Exception>>(first.Data["Strands.CleanupErrors"])));
        await agent.StopAsync(); Assert.Equal(4, seen.Count);
    }

    [Fact]
    public async Task ActualResponseAndBargeInHooksRunBeforeTheirPublicOutput()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var seen = new List<string>();
        agent.Hooks.Register<BidiBargeInHookEvent>((value, token) => { Assert.Same(agent, value.Agent); Assert.True(token.CanBeCanceled); seen.Add("barge"); return default; });
        agent.Hooks.Register<BidiResponseStopHookEvent>((value, _) => { seen.Add(value.ResponseId); return default; });
        await agent.StartAsync(); model.Current.Emit(new BidiBargeInEvent());
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await Within(NextAsync())); Assert.IsType<BidiBargeInEvent>(receiver.Current); Assert.Equal(new[] { "barge" }, seen);
        model.Current.Emit(new BidiResponseStopEvent("actual")); model.Current.Close();
        Assert.True(await Within(NextAsync())); Assert.IsType<BidiResponseStopEvent>(receiver.Current); Assert.Equal(new[] { "barge", "actual" }, seen);
        Assert.False(await Within(NextAsync()));
        Assert.Equal(new[] { "barge", "actual" }, seen); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task ADisconnectDoesNotInventAModelResponseCompletion()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); var callbacks = 0;
        agent.Hooks.Register<BidiResponseStopHookEvent>((_, _) => { callbacks++; return default; });
        await agent.StartAsync(); var failure = new IOException("disconnected"); model.Current.Emit(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in agent.ReceiveAsync()) { } }));
        await agent.StopAsync(); Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task ReactiveHooksSurroundTheRealSwapAndKeepTheOriginalTimeout()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); var seen = new List<string>();
        var timeout = new BidiConnectionTimeoutException("native timeout");
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>((value, _) =>
        { Assert.Same(agent, value.Agent); Assert.Equal(BidiRestartReason.Timeout, value.Reason); Assert.Same(timeout, value.TimeoutError); Assert.Equal(0, model.Restarts); seen.Add("before"); return default; });
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>((value, _) =>
        { Assert.Equal(BidiRestartReason.Timeout, value.Reason); Assert.Null(value.Error); Assert.Equal(1, model.Restarts); seen.Add("after"); return default; });
        await agent.StartAsync(); model.Current.Emit(timeout);
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        await Within(model.Reconnected.Task); model.Current.Close(); Assert.False(await Within(NextAsync()));
        Assert.Equal(new[] { "before", "after" }, seen); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task ABeforeFailurePreventsTheSwapAndReleasesSendersWithTheSameFailure()
    {
        var failure = new IOException("before hook"); var unexpected = new IOException("native swap must not run");
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { RestartError = unexpected };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); var after = 0;
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>((_, _) => throw failure);
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>((_, _) => { after++; return default; });
        await agent.StartAsync(); model.Current.Emit(new BidiConnectionTimeoutException("timeout"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(NextAsync));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.SendAsync(new BidiMessage([new TextBlock("queued")]))));
        Assert.Equal(0, model.Restarts); Assert.Equal(0, after); Assert.Empty(model.Inputs); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task ASwapFailureRemainsPrimaryWhenTheAfterHookAlsoFails()
    {
        var failure = new IOException("native swap"); var cleanup = new IOException("after hook");
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { RestartError = failure };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>((value, token) => { Assert.Same(failure, value.Error); Assert.False(token.CanBeCanceled); throw cleanup; });
        await agent.StartAsync(); model.Current.Emit(new BidiConnectionTimeoutException("timeout"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(NextAsync));
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]))); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task DispatchSnapshotsRegistrationsAndDisposalRemovesOnlyTheSelectedHandle()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel(); var registry = new BidiHookRegistry();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, Hooks = registry });
        Assert.Same(registry, agent.Hooks); var seen = new List<string>(); IDisposable? later = null; IDisposable? added = null;
        registry.Register<BidiResponseStopHookEvent>((_, _) =>
        { seen.Add("first"); later!.Dispose(); added ??= registry.Register<IBidiHookEvent>((_, _) => { seen.Add("new"); return default; }); return default; });
        later = registry.Register<BidiResponseStopHookEvent>((_, _) => { seen.Add("removed"); return default; });
        await registry.InvokeAsync(new BidiResponseStopHookEvent(agent, "one")); Assert.Equal(new[] { "first", "removed" }, seen);
        seen.Clear(); await registry.InvokeAsync(new BidiResponseStopHookEvent(agent, "two")); Assert.Equal(new[] { "first", "new" }, seen);
        added!.Dispose(); added.Dispose(); seen.Clear(); await registry.InvokeAsync(new BidiResponseStopHookEvent(agent, "three")); Assert.Equal(new[] { "first" }, seen);
    }

    [Fact]
    public async Task AStopCallbackCannotAwaitItsOwnLifecycleAndDisposalStillReleasesOwnedResources()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel(); var callbacks = 0;
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiAgentStopEvent>(async (_, _) =>
        {
            callbacks++;
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StopAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StartAsync());
            Assert.Throws<InvalidOperationException>(() => { agent.DisposeAsync(); });
        });
        await agent.StartAsync(); await Within(StopAsync()); Assert.Equal(1, callbacks);
        async Task<bool> StopAsync() { await agent.StopAsync(); return true; }
    }
}
