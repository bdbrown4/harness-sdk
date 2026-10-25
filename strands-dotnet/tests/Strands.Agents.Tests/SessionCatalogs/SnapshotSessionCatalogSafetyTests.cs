using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.Context;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

public sealed class SnapshotSessionCatalogSafetyTests
{
    private const string Latest = "session-a/scopes/agent/agent-a/snapshots/snapshot_latest.json";

    [Fact]
    public async Task PendingSaveFencesEveryMutatingAdmissionUntilTheBackendPhysicallyReturns()
    {
        var storage = new ProbeStorage();
        var manager = new SnapshotSessionManager(storage, "session-a");
        var bodies = 0;
        var tool = new FunctionTool(new("probe", "probe", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { bodies++; return new("ok"); });
        await using var agent = Create(manager, tools: [tool]);
        await agent.InitializeAsync();
        var entered = Signal(); var release = Signal();
        storage.BeforeWrite = async (_, _) => { entered.TrySetResult(true); await release.Task; };
        var save = manager.SaveSnapshotAsync(agent, false).AsTask();
        try
        {
            await Within(entered.Task);
            Assert.False(save.IsCompleted);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("blocked"));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeToolAsync(Call()).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveSnapshotAsync(agent, true).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreSnapshotAsync(agent).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => Replace(agent, "blocked"));
            Assert.Throws<InvalidOperationException>(() => agent.AddOrReplaceApplicationTool(tool));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ShutdownAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.DisposeAsync().AsTask());
            Assert.Equal(0, bodies);
        }
        finally { release.TrySetResult(true); await Within(save); storage.BeforeWrite = null; }
        Assert.NotNull(await save);
        Assert.Equal("ok", (await agent.InvokeToolAsync(Call())).Content);
        Assert.Equal(1, bodies);
    }

    [Fact]
    public async Task StorageCallbackReentryIsRefusedWithoutAmbientInitializationPrivilege()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        await using var agent = Create(manager);
        var callbacks = 0;
        storage.BeforeRead = async (_, _) =>
        {
            callbacks++;
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreSnapshotAsync(agent).AsTask());
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeToolAsync(Call()).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        };
        Assert.NotNull(await manager.SaveSnapshotAsync(agent, false));
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task CanceledRestoreRetainsReservationUntilNoncooperativeReadSettles()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        await using var agent = Create(manager);
        await agent.InitializeAsync(); await manager.SaveSnapshotAsync(agent, true); await Replace(agent, "current");
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        storage.BeforeRead = async (_, token) => { Assert.Equal(cancellation.Token, token); entered.TrySetResult(true); await release.Task; };
        var restore = manager.RestoreSnapshotAsync(agent, cancellationToken: cancellation.Token).AsTask();
        try
        {
            await Within(entered.Task); cancellation.Cancel();
            Assert.False(restore.IsCompleted);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeToolAsync(Call()).AsTask());
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        AssertProjection(agent, "current"); storage.BeforeRead = null;
        Assert.True(await manager.RestoreSnapshotAsync(agent)); AssertProjection(agent, "saved");
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("prompt")]
    [InlineData("stash")]
    [InlineData("classic")]
    [InlineData("stateful-marker")]
    public async Task MalformedLateFieldRejectsBeforeAnyStashWriteOrLiveProjectionChange(string field)
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        var originals = new ProbeStorage(ephemeral: true);
        var context = new ContextManager(new() { Strategies = [], StashStorage = originals });
        await using var agent = Create(manager, extraPlugins: [context]);
        await agent.InitializeAsync(); await manager.SaveSnapshotAsync(agent, true); await Replace(agent, "current");
        var raw = JsonNode.Parse((await storage.ReadAsync(Latest))!)!;
        var snapshot = raw["snapshot"]!;
        switch (field)
        {
            case "cursor": snapshot["modelHistoryCount"] = 100; break;
            case "prompt": snapshot["systemPromptContent"] = JsonSerializer.SerializeToNode(new ContentBlock[] { new TextBlock("mismatch") }); break;
            case "stash": snapshot["contextStash"] = new JsonObject { ["../invalid"] = JsonSerializer.SerializeToNode<ContentBlock>(new TextBlock("bad")) }; break;
            case "classic": snapshot["conversationManagerState"] = new JsonObject { ["version"] = 999 }; break;
            case "stateful-marker": raw.AsObject().Remove("stateful"); break;
        }
        await storage.WriteAsync(Latest, JsonSerializer.SerializeToUtf8Bytes(raw));
        var writes = originals.Writes;
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RestoreSnapshotAsync(agent).AsTask());
        Assert.Equal(writes, originals.Writes); AssertProjection(agent, "current");
    }

    [Fact]
    public async Task MutableCustomContextRejectsInitializedRestoreBeforeStashOrLiveWrites()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        var originals = new ProbeStorage(ephemeral: true); var strategy = new CountingStrategy();
        var context = new ContextManager(new() { Strategies = [strategy], StashStorage = originals });
        await using var agent = Create(manager, extraPlugins: [context]);
        await agent.InitializeAsync(); await manager.SaveSnapshotAsync(agent, true); await Replace(agent, "current");
        var writes = originals.Writes;
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.RestoreSnapshotAsync(agent).AsTask());
        Assert.Equal(writes, originals.Writes); Assert.Equal(1, strategy.Initializations); AssertProjection(agent, "current");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltInContextRestoresInlineOriginalsWithoutDuplicatingInitialization(bool agentic)
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        var originals = new ProbeStorage(ephemeral: true);
        var context = new ContextManager(new() { Mode = agentic ? ContextMode.Agentic : ContextMode.Auto, StashStorage = originals });
        await using var agent = Create(manager, extraPlugins: [context]);
        await agent.InitializeAsync(); await manager.SaveSnapshotAsync(agent, true);
        var stash = Assert.IsType<Stash>(context.Stash); var reference = Assert.Single(stash.GetReferences(agent.Messages[0].Content[0], agent.Messages[0], 0));
        await stash.ClearAsync(); Assert.Null(await stash.RetrieveAsync(reference)); await Replace(agent, "current");
        Assert.True(await manager.RestoreSnapshotAsync(agent));
        AssertProjection(agent, "saved"); Assert.Equal("saved", Assert.IsType<TextBlock>(await stash.RetrieveAsync(reference)).Text);
        Assert.Single(agent.Tools, tool => tool.Spec.Name == "retrieve_context");
    }

    [Fact]
    public async Task SameManagerRejectsConcurrentDuplicateAndReleasesTheCapturedBindingOnDisposal()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        var first = Create(manager);
        try { Assert.Throws<ArgumentException>(() => Create(manager)); Assert.Equal(0, storage.Reads); }
        finally { await first.DisposeAsync(); }
        await using var next = Create(manager); await next.InitializeAsync();
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task FailedInitializationReleasesBindingAndKeepsOriginalErrorIdentity()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        var primary = new IOException("read primary"); storage.BeforeRead = (_, _) => throw primary;
        await using (var failed = Create(manager))
        {
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => failed.InitializeAsync()));
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => failed.InitializeAsync()));
            storage.BeforeRead = null;
            await using var next = Create(manager); await next.InitializeAsync();
        }
    }

    [Fact]
    public async Task ThrowingShutdownObserverDoesNotRetainManagerBinding()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        var hooks = new HookRegistry(); var primary = new IOException("shutdown observer");
        hooks.Register<ShutdownEvent>((_, _) => throw primary);
        var agent = Create(manager, hooks: hooks); await agent.InitializeAsync();
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));
        await using var next = Create(manager); await next.InitializeAsync();
    }

    [Fact]
    public async Task StatefulCursorAndProviderStateRestoreTogetherWithoutExecutingTheModel()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        await using var agent = Create(manager, stateful: true); await agent.InitializeAsync();
        await Replace(agent, "saved", cursor: 2); var id = Assert.IsType<string>(await manager.SaveSnapshotAsync(agent, false));
        await Replace(agent, "current", cursor: 0);
        Assert.True(await manager.RestoreSnapshotAsync(agent, id)); AssertProjection(agent, "saved");
        var snapshot = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.Equal(2, snapshot.Data["modelHistoryCount"].GetInt32());
        Assert.Equal("provider-saved", agent.ModelState.GetAll()["label"].GetString());
    }

    [Fact]
    public async Task BackendFailureWinsRacingCancellationAndSuccessfulAcceptedWriteStillReturnsItsId()
    {
        var storage = new ProbeStorage(); var manager = new SnapshotSessionManager(storage, "session-a");
        await using var agent = Create(manager); await agent.InitializeAsync();
        using var cancellation = new CancellationTokenSource(); var primary = new IOException("write primary");
        storage.BeforeWrite = (_, _) => { cancellation.Cancel(); throw primary; };
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => manager.SaveSnapshotAsync(agent, false, cancellation.Token).AsTask()));
        using var accepted = new CancellationTokenSource();
        storage.BeforeWrite = (_, _) => { accepted.Cancel(); return default; };
        Assert.NotNull(await manager.SaveSnapshotAsync(agent, false, accepted.Token));
        storage.BeforeWrite = null;
    }

    private static Agent Create(SnapshotSessionManager manager, IReadOnlyList<ITool>? tools = null, IReadOnlyList<IPlugin>? extraPlugins = null, HookRegistry? hooks = null, bool stateful = false) => new(new()
    {
        Id = "agent-a",
        Model = stateful ? new NeverStatefulModel() : new NeverModel(),
        SystemPrompt = "prompt-saved",
        Messages = [Message.User("saved"), Message.Assistant("saved-response")],
        State = Values("saved"),
        ModelState = Values("provider-saved"),
        Plugins = new IPlugin[] { manager }.Concat(extraPlugins ?? []).ToArray(),
        Tools = tools ?? [],
        Hooks = hooks ?? new()
    });
    private static Task Replace(Agent agent, string label, int? cursor = null)
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User(label), Message.Assistant(label + "-response") }),
            ["state"] = JsonSerializer.SerializeToElement(Values(label)),
            ["modelState"] = JsonSerializer.SerializeToElement(Values("provider-" + label)),
            ["systemPrompt"] = JsonSerializer.SerializeToElement("prompt-" + label)
        };
        if (cursor.HasValue) values["modelHistoryCount"] = JsonSerializer.SerializeToElement(cursor.Value);
        return agent.LoadSnapshotAsync(new(values)).AsTask();
    }
    private static IReadOnlyDictionary<string, JsonElement> Values(string value) => new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement(value) };
    private static void AssertProjection(Agent agent, string value)
    {
        Assert.Equal(value, agent.State.GetAll()["label"].GetString()); Assert.Equal("provider-" + value, agent.ModelState.GetAll()["label"].GetString());
        Assert.Equal("prompt-" + value, agent.SystemPrompt); Assert.Equal(new[] { value, value + "-response" }, agent.Messages.Select(message => message.Text));
    }
    private static ToolUseBlock Call() => new("call-a", "probe", JsonSerializer.SerializeToElement(new { }));
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task;
    }
    private sealed class NeverModel : IModel
    { public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model or host process may run in catalog safety tests."); }
    private sealed class NeverStatefulModel : IStatefulModel
    { public bool Stateful => true; public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No stateful provider call may run in catalog safety tests."); }
    private sealed class CountingStrategy : ContextStrategy
    {
        public int Initializations { get; private set; }
        public override string Name => "test:mutable";
        public override ValueTask InitializeAsync(Agent agent, Stash? stash = null, CancellationToken cancellationToken = default) { Initializations++; return default; }
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default) => new(false);
    }
    private sealed class ProbeStorage(bool ephemeral = false) : IStorage, IStorageCharacteristics
    {
        private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);
        public bool IsEphemeral => ephemeral; public bool IsNamespaced => true;
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Func<string, CancellationToken, ValueTask>? BeforeRead { get; set; }
        public Func<string, CancellationToken, ValueTask>? BeforeWrite { get; set; }
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        { var detached = (byte[])data.Clone(); Writes++; if (BeforeWrite is { } callback) await callback(key, cancellationToken); lock (_items) _items[key] = detached; }
        public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { Reads++; if (BeforeRead is { } callback) await callback(key, cancellationToken); lock (_items) return _items.TryGetValue(key, out var value) ? (byte[])value.Clone() : null; }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { lock (_items) _items.Remove(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
        { lock (_items) return new((IReadOnlyList<string>)_items.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray()); }
    }
}
