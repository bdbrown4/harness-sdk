using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

public sealed class SnapshotSessionCatalogMinimumTests
{
    [Fact]
    public async Task TwoCompleteCheckpointsSurviveFreshBackendPagingAndOlderInitialRestore()
    {
        var directory = OwnedDirectory();
        try
        {
            string first, second;
            {
                var manager = new SnapshotSessionManager(new LocalFileStorage(directory), "session-a");
                await using var writer = Create(manager, "first");
                await writer.InitializeAsync();
                first = Assert.IsType<string>(await manager.SaveSnapshotAsync(writer, false));
                Assert.NotEmpty(first);
                await ReplaceAsync(writer, "second");
                second = Assert.IsType<string>(await manager.SaveSnapshotAsync(writer, false));
                Assert.NotEqual(first, second);
                Assert.Null(await manager.SaveSnapshotAsync(writer, true));
            }
            var reopened = new SnapshotSessionManager(new LocalFileStorage(directory), "session-a");
            var plugin = new ReadingPlugin();
            var observed = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register<InitializedEvent>((value, _) => { observed.Add(Label(value.Agent)); return default; });
            await using var restored = Create(reopened, "seed", plugin, hooks);
            var ids = await reopened.ListSnapshotIdsAsync(restored);
            Assert.Equal(new[] { first, second }, ids);
            Assert.Equal(new[] { first }, await reopened.ListSnapshotIdsAsync(restored, limit: 1));
            Assert.Equal(new[] { second }, await reopened.ListSnapshotIdsAsync(restored, startAfter: first));
            Assert.True(await reopened.RestoreSnapshotAsync(restored, first));
            AssertProjection(restored, "first");
            Assert.Equal(new[] { "first" }, plugin.Labels);
            Assert.Equal(new[] { "first" }, observed);
            Assert.True(restored.State.GetAll()["plugin-ready"].GetBoolean());
        }
        finally { DeleteOwned(directory); }
    }

    [Fact]
    public async Task LatestRestoresBeforePluginAndInitializedObserversWithoutErasingPluginState()
    {
        var directory = OwnedDirectory();
        try
        {
            {
                var manager = new SnapshotSessionManager(new LocalFileStorage(directory), "session-a");
                await using var writer = Create(manager, "saved");
                await writer.InitializeAsync();
                Assert.Null(await manager.SaveSnapshotAsync(writer, true));
            }
            var plugin = new ReadingPlugin();
            var observed = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register<InitializedEvent>((value, _) => { observed.Add(Label(value.Agent)); return default; });
            var reopened = new SnapshotSessionManager(new LocalFileStorage(directory), "session-a");
            await using var restored = Create(reopened, "seed", plugin, hooks);
            await restored.InitializeAsync();
            AssertProjection(restored, "saved");
            Assert.Equal(new[] { "saved" }, plugin.Labels);
            Assert.Equal(new[] { "saved" }, observed);
            Assert.True(restored.State.GetAll()["plugin-ready"].GetBoolean());
        }
        finally { DeleteOwned(directory); }
    }

    [Fact]
    public async Task InitializedAgentCanRewindThenRestoreLatestWithoutRerunningInitializationOrModel()
    {
        var directory = OwnedDirectory();
        try
        {
            var manager = new SnapshotSessionManager(new LocalFileStorage(directory), "session-a");
            var plugin = new ReadingPlugin();
            await using var agent = Create(manager, "first", plugin);
            await agent.InitializeAsync();
            var first = Assert.IsType<string>(await manager.SaveSnapshotAsync(agent, false));
            await ReplaceAsync(agent, "second");
            Assert.Null(await manager.SaveSnapshotAsync(agent, true));
            Assert.True(await manager.RestoreSnapshotAsync(agent, first));
            AssertProjection(agent, "first");
            Assert.True(await manager.RestoreSnapshotAsync(agent));
            AssertProjection(agent, "second");
            Assert.Single(plugin.Labels);
        }
        finally { DeleteOwned(directory); }
    }

    [Fact]
    public async Task MissingValidCheckpointLeavesUninitializedTargetAndSeedProjectionUntouched()
    {
        var directory = OwnedDirectory();
        try
        {
            var manager = new SnapshotSessionManager(new LocalFileStorage(directory), "session-a");
            var plugin = new ReadingPlugin();
            await using var agent = Create(manager, "seed", plugin);
            Assert.False(await manager.RestoreSnapshotAsync(agent, "018f0ec2-0000-7000-8000-000000000001"));
            AssertProjection(agent, "seed");
            Assert.Empty(plugin.Labels);
        }
        finally { DeleteOwned(directory); }
    }

    [Fact]
    public async Task ExistingTwoMethodStoreStillRestoresBeforePluginWithoutCatalogBinding()
    {
        var plugin = new ReadingPlugin();
        var snapshot = new SessionSnapshot(1, [Message.User("legacy"), Message.Assistant("legacy-response")])
        {
            AgentId = "agent-a",
            HasSystemPrompt = true,
            SystemPrompt = "prompt-legacy",
            State = Values("legacy"),
            ModelState = Values("provider-legacy")
        };
        await using var agent = new Agent(new()
        {
            Model = new NeverModel(),
            Id = "agent-a",
            SessionStore = new LegacyStore(snapshot),
            SessionId = "legacy",
            Messages = [Message.User("seed")],
            Plugins = [plugin],
            State = Values("seed")
        });
        await agent.InitializeAsync();
        AssertProjection(agent, "legacy");
        Assert.Equal(new[] { "legacy" }, plugin.Labels);
        Assert.True(agent.State.GetAll()["plugin-ready"].GetBoolean());
    }

    [Fact]
    public async Task ConstructionDoesNotAccessBorrowedStorageOrInitializeAnAgent()
    {
        var storage = new RefusingStorage();
        var manager = new SnapshotSessionManager(storage, "session-a");
        var plugin = new ReadingPlugin();
        await using var agent = Create(manager, "seed", plugin);
        Assert.Equal("session-a", manager.SessionId);
        Assert.Empty(plugin.Labels);
        Assert.Equal(0, storage.Calls);
    }

    private static Agent Create(SnapshotSessionManager manager, string label, ReadingPlugin? plugin = null, HookRegistry? hooks = null) => new(new()
    {
        Model = new NeverModel(),
        Id = "agent-a",
        SystemPrompt = "prompt-" + label,
        Messages = [Message.User(label), Message.Assistant(label + "-response")],
        State = Values(label),
        ModelState = Values("provider-" + label),
        Hooks = hooks ?? new(),
        Plugins = plugin is null ? [manager] : [manager, plugin]
    });
    private static Task ReplaceAsync(Agent agent, string label) => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>
    {
        ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User(label), Message.Assistant(label + "-response") }),
        ["state"] = JsonSerializer.SerializeToElement(Values(label)),
        ["modelState"] = JsonSerializer.SerializeToElement(Values("provider-" + label)),
        ["systemPrompt"] = JsonSerializer.SerializeToElement("prompt-" + label)
    })).AsTask();
    private static IReadOnlyDictionary<string, JsonElement> Values(string label) => new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement(label) };
    private static string Label(Agent agent) => agent.State.GetAll()["label"].GetString()!;
    private static void AssertProjection(Agent agent, string label)
    {
        Assert.Equal(label, Label(agent));
        Assert.Equal("provider-" + label, agent.ModelState.GetAll()["label"].GetString());
        Assert.Equal("prompt-" + label, agent.SystemPrompt);
        Assert.Equal(new[] { label, label + "-response" }, agent.Messages.Select(message => message.Text));
        Assert.Equal("agent-a", agent.Id);
    }
    private static string OwnedDirectory() => Path.Combine(Path.GetTempPath(), "strands-catalog-minimum", Guid.NewGuid().ToString("N"));
    private static void DeleteOwned(string directory) { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class ReadingPlugin : IPlugin
    {
        public string Name => "test:catalog-state-reader";
        public List<string> Labels { get; } = [];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Labels.Add(Label(agent)); agent.State.Set("plugin-ready", true); return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class NeverModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Catalog tests must not execute a model, tool or host process.");
    }
    private sealed class LegacyStore(SessionSnapshot value) : ISessionStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(value);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => default;
    }
    private sealed class RefusingStorage : IStorage
    {
        public int Calls { get; private set; }
        private Exception Rejected() { Calls++; return new InvalidOperationException("Construction must not access storage."); }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) => throw Rejected();
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => throw Rejected();
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => throw Rejected();
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => throw Rejected();
    }
}
