using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;

namespace Strands.Bidi.Tests;

public sealed class BidiPersistenceTests
{
    [Fact]
    public async Task ARealFileStoreRestoresMessagesStateAndExplicitNullPromptIntoAFreshAgent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-bidi-" + Guid.NewGuid().ToString("N")); var store = new FileSessionStore(directory);
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "host" });
        await using (var model = new ScriptedModel())
        await using (var first = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store, SessionId = "native-session" }))
        {
            await first.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["systemPrompt"] = JsonSerializer.SerializeToElement<string?>(null) }));
            first.State.Set("tenant", "persisted"); await first.StartAsync(); await first.SendAsync(new BidiMessage([new TextBlock("remember")])); await first.StopAsync();
        }
        await using var secondModel = new ScriptedModel(); await using var second = new BidiAgent(new() { Model = secondModel, ToolAgent = host, SessionStore = new FileSessionStore(directory), SessionId = "native-session" });
        await second.StartAsync(); Assert.Equal("remember", Assert.Single(secondModel.Session!.Messages).Text); Assert.Equal("persisted", second.State.Get<string>("tenant")); Assert.Null(secondModel.Session.SystemPrompt); await second.StopAsync();
    }

    [Fact]
    public async Task UserAppendHooksRunAfterCommitAndObserverFailureStillPersistsAcceptedInput()
    {
        var store = new MemoryStore(); await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        var failure = new IOException("message observer");
        using var registration = agent.Hooks.Register<BidiMessageAddedEvent>((value, _) => { Assert.Equal(value.Message.TrackingId, Assert.Single(agent.Messages).TrackingId); throw failure; });
        await agent.StartAsync(); Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.SendAsync(new BidiMessage([new TextBlock("accepted")]))));
        Assert.Equal("accepted", Assert.Single(store.Snapshot!.Messages).Text); Assert.Empty(model.Inputs); await agent.StopAsync();
    }

    [Fact]
    public async Task TextReservationAndCompletionProduceAddedThenUpdatedWithTheSameTrackingId()
    {
        var observed = new List<IBidiHookEvent>(); var store = new MemoryStore();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel(new BidiTextStartEvent("text"), new BidiTextDeltaEvent("hello", "text"), new BidiTextStopEvent("text"));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        agent.Hooks.Register<IBidiHookEvent>((value, _) => { if (value is BidiMessageAddedEvent or BidiMessageUpdatedEvent) observed.Add(value); return default; });
        await agent.StartAsync(); await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        while (await reader.MoveNextAsync()) if (reader.Current is BidiTextBlockEvent) break;
        var added = Assert.IsType<BidiMessageAddedEvent>(observed[0]); var updated = Assert.IsType<BidiMessageUpdatedEvent>(observed[1]);
        Assert.Empty(added.Message.Content); Assert.Equal(added.Message.TrackingId, updated.TrackingId); Assert.Equal("hello", updated.Message.Text); Assert.Equal("hello", Assert.Single(store.Snapshot!.Messages).Text); await agent.StopAsync();
    }

    [Fact]
    public async Task InitializationRestoresOnceBeforeCallbacksAndCachesReadFailureWithoutWriting()
    {
        var store = new MemoryStore { LoadError = new IOException("cannot read") }; await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        Assert.Same(store.LoadError, await Assert.ThrowsAsync<IOException>(() => agent.StartAsync()));
        Assert.Same(store.LoadError, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Equal(1, store.Loads); Assert.Equal(0, store.Saves); Assert.Equal(0, model.Starts);
    }

    internal sealed class MemoryStore : ISessionStore
    {
        internal SessionSnapshot? Snapshot; internal Exception? LoadError; internal Exception? SaveError = null; internal int Loads, Saves;
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken cancellationToken = default) { Loads++; if (LoadError is { } error) throw error; return new(Snapshot); }
        public ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Saves++; if (SaveError is { } error) throw error; Snapshot = snapshot; return default; }
    }
}
