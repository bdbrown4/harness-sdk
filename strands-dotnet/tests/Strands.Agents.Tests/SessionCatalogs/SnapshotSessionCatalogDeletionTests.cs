using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

public sealed class SnapshotSessionCatalogDeletionTests
{
    [Fact]
    public async Task SessionWideDeletionRemovesEveryRecordWithoutReadingOrChangingLiveAgents()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "selected");
        await using var first = Create(manager, "first"); await using var second = Create(manager, "second");
        await manager.SaveSnapshotAsync(first, false); await manager.SaveSnapshotAsync(first, true);
        await manager.SaveSnapshotAsync(second, false); await manager.SaveSnapshotAsync(second, true);
        await storage.WriteAsync("selected/scopes/agent/archived/snapshots/corrupt.json", [0xff]);
        await storage.WriteAsync("selected-other/keep", [7]); await storage.WriteAsync("other/keep", [8]);
        var firstMessages = first.Messages; var reads = storage.Reads;
        await manager.DeleteSessionAsync();
        Assert.Equal(new[] { "other/keep", "selected-other/keep" }, await storage.ListAsync());
        Assert.Equal(reads, storage.Reads); Assert.False(storage.Disposed);
        Assert.Equal(firstMessages, first.Messages); Assert.Equal("first", first.State.GetAll()["label"].GetString());
        Assert.Equal("second", second.State.GetAll()["label"].GetString());
        await manager.SaveSnapshotAsync(first, true);
        Assert.NotNull(await storage.ReadAsync("selected/scopes/agent/first/snapshots/snapshot_latest.json"));
    }

    [Fact]
    public async Task KnownAutomaticSessionStashClearsButCallerScopedSharedOriginalsSurvive()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "selected");
        var automatic = new ContextManager(new() { Strategies = [] });
        var shared = new ContextManager(new() { Strategies = [], StashStorage = storage.Namespace("shared") });
        await using var first = Create(manager, "first", automatic, storage: storage);
        await using var second = Create(manager, "second", shared);
        await first.InitializeAsync(); await second.InitializeAsync();
        var autoStash = Assert.IsType<Stash>(automatic.Stash); var sharedStash = Assert.IsType<Stash>(shared.Stash);
        Assert.NotEmpty(await autoStash.ListAsync()); Assert.NotEmpty(await sharedStash.ListAsync());
        await storage.WriteAsync("context/other/keep", [1]);
        await manager.DeleteSessionAsync();
        Assert.Empty(await autoStash.ListAsync()); Assert.NotEmpty(await sharedStash.ListAsync());
        Assert.NotNull(await storage.ReadAsync("context/other/keep"));
        Assert.Empty(await storage.ListAsync("selected/"));
    }

    [Fact]
    public async Task BusySecondAgentRejectsBeforeIoAndReleasesTheEarlierReservation()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "selected");
        var entered = Signal(); var release = Signal();
        var tool = new FunctionTool(new("hold", "hold", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, _) => { entered.TrySetResult(true); await release.Task; return "ok"; });
        await using var first = Create(manager, "first"); await using var second = Create(manager, "second", tools: [tool]);
        await first.InitializeAsync(); await second.InitializeAsync();
        var body = second.InvokeToolAsync(new("call", "hold", JsonSerializer.SerializeToElement(new { }))).AsTask();
        try
        {
            await Within(entered.Task); var lists = storage.Lists;
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteSessionAsync().AsTask());
            Assert.Equal(lists, storage.Lists); Assert.Empty(storage.Deleted);
            await manager.SaveSnapshotAsync(first, true);
        }
        finally { release.TrySetResult(true); await Within(body); }
    }

    [Fact]
    public async Task CancelledNoncooperativeDeleteKeepsAllAdmissionsUntilPhysicalCompletion()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "selected");
        await using var agent = Create(manager, "first"); await manager.SaveSnapshotAsync(agent, true);
        using var caller = new CancellationTokenSource(); var entered = Signal(); var release = Signal();
        storage.BeforeDelete = async (_, token) => { Assert.Equal(caller.Token, token); entered.TrySetResult(true); await release.Task; };
        var deletion = manager.DeleteSessionAsync(caller.Token).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, deletion, Task.Delay(TimeSpan.FromSeconds(10))));
            await entered.Task; caller.Cancel(); Assert.False(deletion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => Create(manager, "new"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveSnapshotAsync(agent, true).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ShutdownAsync());
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        Assert.Equal(caller.Token, error.CancellationToken);
        storage.BeforeDelete = null; await manager.SaveSnapshotAsync(agent, true);
        await using var rebound = Create(manager, "new");
    }

    [Fact]
    public async Task AlreadyCancelledDeletionDoesNotAccessStorageOrReserveBindings()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "selected");
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DeleteSessionAsync(caller.Token).AsTask());
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, storage.Lists); Assert.Empty(storage.Deleted);
        await using var agent = Create(manager, "first"); await manager.SaveSnapshotAsync(agent, true);
    }

    [Fact]
    public async Task MissingSessionIsIdempotentWithoutReadOrBackendOwnership()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "selected");
        await manager.DeleteSessionAsync(); await manager.DeleteSessionAsync();
        Assert.Empty(storage.Deleted); Assert.Equal(0, storage.Reads); Assert.False(storage.Disposed);
        await using var agent = Create(manager, "first"); await manager.SaveSnapshotAsync(agent, true);
    }

    private static Agent Create(SnapshotSessionManager manager, string id, ContextManager? context = null, IStorage? storage = null, IReadOnlyList<ITool>? tools = null) => new(new()
    {
        Id = id,
        Model = new NeverModel(),
        Messages = [Message.User(id), Message.Assistant(id + " response")],
        State = new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement(id) },
        Storage = storage,
        Plugins = context is null ? [manager] : [manager, context],
        Tools = tools ?? []
    });
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class NeverModel : IModel
    { public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Catalog deletion tests cannot run models or host processes."); }
    private sealed class ProbeStorage : IStorage, IStorageCharacteristics, IDisposable
    {
        private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);
        public bool IsNamespaced => true; public bool IsEphemeral => false;
        public int Reads { get; private set; }
        public int Lists { get; private set; }
        public bool Disposed { get; private set; }
        public List<string> Deleted { get; } = [];
        public Func<string, CancellationToken, ValueTask>? BeforeDelete { get; set; }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { lock (_items) _items[key] = (byte[])data.Clone(); return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { Reads++; lock (_items) return new(_items.TryGetValue(key, out var data) ? (byte[])data.Clone() : null); }
        public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Deleted.Add(key); if (BeforeDelete is { } callback) await callback(key, cancellationToken); lock (_items) _items.Remove(key); }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) { Lists++; lock (_items) return new(_items.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray()); }
        public void Dispose() => Disposed = true;
    }
}
