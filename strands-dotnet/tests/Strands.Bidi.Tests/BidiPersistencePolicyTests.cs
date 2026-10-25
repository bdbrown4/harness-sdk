using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiPersistenceTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiPersistencePolicyTests
{
    [Fact]
    public async Task StopPolicySavesAfterBeforeRestartAndStopCallbacksButNotResponseCompletion()
    {
        var store = new MemoryStore(); await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SessionSaveStrategy = BidiSessionSaveStrategy.Stop });
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>((_, _) => { agent.State.Set("stage", "before restart"); Assert.Equal(0, model.Restarts); return default; });
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>((_, _) => { Assert.Equal("before restart", store.Snapshot!.State!["stage"].GetString()); Assert.Equal(1, store.Saves); return default; });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { agent.State.Set("stage", "stopped"); return default; });
        await agent.StartAsync(); await agent.SendAsync(new BidiMessage([new TextBlock("input")])); Assert.Equal(0, store.Saves);
        model.Current.Emit(new BidiResponseStopEvent("complete")); await using var reader = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(Next())); Assert.IsType<BidiResponseStopEvent>(reader.Current); Assert.Equal(0, store.Saves);
        model.Current.Emit(new BidiConnectionTimeoutException("deadline")); Assert.True(await Within(Next())); Assert.IsType<BidiConnectionRestartEvent>(reader.Current); await Within(model.Reconnected.Task);
        await agent.StopAsync(); Assert.Equal(2, store.Saves); Assert.Equal("stopped", store.Snapshot!.State!["stage"].GetString());
        async Task<bool> Next() => await reader.MoveNextAsync();
    }

    [Fact]
    public async Task AnExplicitSaveCallbackCannotReenterItsOwnAgentLifecycle()
    {
        var store = new CallbackStore(); await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        store.OnSave = async () => await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StopAsync());
        await agent.SaveSessionAsync(); Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task StopCannotHideAMessageCleanupFailureFromACancelledTool()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var failure = new IOException("completion observer");
        var tool = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, token) => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return "unreachable"; });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] }); await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "work", JsonSerializer.SerializeToElement(new { }))]));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); agent.Hooks.Register<BidiMessageUpdatedEvent>((_, _) => throw failure);
        await agent.StartAsync(); await using var reader = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync()); Assert.True(await reader.MoveNextAsync()); await Within(entered.Task);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.StopAsync())); Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages.Last().Content.OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task InitializationDispatchesOnceAfterRestoreAndBeforeAnyNativeConnection()
    {
        var store = new MemoryStore { Snapshot = new(1, [Message.User("restored")]) }; await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store }); var callbacks = 0;
        agent.Hooks.Register<BidiInitializedEvent>((value, _) => { Assert.Same(agent, value.Agent); Assert.Equal(0, model.Starts); Assert.Equal("restored", Assert.Single(agent.Messages).Text); callbacks++; return default; });
        await Task.WhenAll(agent.InitializeAsync(), agent.InitializeAsync()); await agent.StartAsync(); await agent.StopAsync(); Assert.Equal(1, callbacks); Assert.Equal(1, store.Loads);
    }

    private sealed class CallbackStore : ISessionStore
    {
        internal Func<Task>? OnSave; internal int Saves;
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken cancellationToken = default) => new((SessionSnapshot?)null);
        public async ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Saves++; if (OnSave is { } callback) await callback(); }
    }
}
