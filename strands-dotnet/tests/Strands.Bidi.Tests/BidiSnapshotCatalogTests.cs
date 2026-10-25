using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Storage;
using static Strands.Bidi.Tests.BidiAgentTests;

namespace Strands.Bidi.Tests;

public sealed class BidiSnapshotCatalogTests
{
    [Fact]
    public async Task ImmutableCatalogPagesAndRestoresThroughAReopenedFileBackend()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new BidiSnapshotSessionManager(new LocalFileStorage(directory), "session");
            await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "original prompt" });
            await using var model = new ScriptedModel();
            await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager, Messages = [Message.User("original")] });
            agent.State.Set("counter", 1);
            var first = await manager.SaveSnapshotAsync(agent, false);
            agent.State.Set("counter", 2);
            var second = await manager.SaveSnapshotAsync(agent, false);
            Assert.NotNull(first); Assert.NotNull(second); Assert.True(string.CompareOrdinal(first, second) < 0);
            Assert.Equal(new[] { first }, await manager.ListSnapshotIdsAsync(agent, 1));
            Assert.Equal(new[] { second }, await manager.ListSnapshotIdsAsync(agent, startAfter: first));
            Assert.Empty(await manager.ListSnapshotIdsAsync(agent, 0));
            var reopened = new BidiSnapshotSessionManager(new LocalFileStorage(directory), "session");
            await using var target = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = reopened });
            Assert.True(await reopened.RestoreSnapshotAsync(target, first));
            Assert.Equal("original", Assert.Single(target.Messages).Text); Assert.Equal(1, target.State.Get<int>("counter")); Assert.Equal("original prompt", target.SystemPrompt);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task TriggerAtStopCapturesAfterCallbacksOnceAndWritesImmutableBeforeLatest()
    {
        var storage = new RecordingStorage(); var triggers = 0;
        var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Trigger, snapshotTrigger: agent => { triggers++; Assert.Equal(7, agent.State.Get<int>("after")); return true; });
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { agent.State.Set("after", 7); return default; });
        await agent.StartAsync(); await agent.SendAsync(new BidiMessage([new TextBlock("hello")])); Assert.Empty(storage.Writes);
        await agent.StopAsync(); Assert.Equal(1, triggers); Assert.Equal(2, storage.Writes.Count);
        Assert.Contains("/immutable_history/", storage.Writes[0].Key); Assert.EndsWith("/snapshot_latest.json", storage.Writes[1].Key);
        Assert.Equal(storage.Writes[0].Data, storage.Writes[1].Data);
    }

    [Fact]
    public async Task LatestWrittenOnMessageInitializesAFreshAgentWithoutOpeningASocket()
    {
        var storage = new InMemoryStorage(); var manager = new BidiSnapshotSessionManager(storage, "roundtrip");
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using (var source = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager }))
        { await source.StartAsync(); await source.SendAsync(new BidiMessage([new TextBlock("accepted")])); await source.StopAsync(); }
        await using var fresh = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = new(storage, "roundtrip") });
        await fresh.InitializeAsync(); Assert.Equal("accepted", Assert.Single(fresh.Messages).Text); Assert.Equal(1, model.Starts); Assert.Equal("roundtrip", fresh.SessionId);
    }

    [Fact]
    public async Task SessionStoreAndManagerAreRejectedBeforeEitherCanRun()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        Assert.Throws<ArgumentException>(() => new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = new(new InMemoryStorage()), SessionStore = new FileSessionStore(Path.GetTempPath()) }));
    }

    private sealed class RecordingStorage : IStorage
    {
        private readonly InMemoryStorage _inner = new();
        internal List<(string Key, byte[] Data)> Writes { get; } = [];
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { Writes.Add((key, (byte[])data.Clone())); await _inner.WriteAsync(key, data, cancellationToken); }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => _inner.ReadAsync(key, cancellationToken);
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);
    }
}
