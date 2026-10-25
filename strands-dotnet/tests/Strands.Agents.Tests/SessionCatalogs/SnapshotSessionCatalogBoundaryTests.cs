using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.Context;
using Strands.Agents.Conversation;
using Strands.Agents.Memory;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

public sealed class SnapshotSessionCatalogBoundaryTests
{
    private const string Latest = "session-a/scopes/agent/agent-a/snapshots/snapshot_latest.json";

    [Fact]
    public async Task AnActiveDirectToolPreventsSaveAndRestoreUntilItsBodyPhysicallyReturns()
    {
        var storage = new ByteStore(); var manager = new SnapshotSessionManager(storage, "session-a");
        var entered = Signal(); var release = Signal();
        var tool = new FunctionTool(Spec(), async (_, _, _) => { entered.TrySetResult(true); await release.Task; return "joined"; });
        await using var agent = Create(manager, tools: [tool]); await agent.InitializeAsync();
        var direct = agent.InvokeToolAsync(Call()).AsTask();
        try
        {
            await Within(entered.Task);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveSnapshotAsync(agent, false).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RestoreSnapshotAsync(agent).AsTask());
            Assert.Equal(0, storage.Writes);
        }
        finally { release.TrySetResult(true); await Within(direct); }
        Assert.Equal("joined", (await direct).Content); Assert.NotNull(await manager.SaveSnapshotAsync(agent, false));
    }

    [Fact]
    public async Task CaptureDetachesBothStateDictionariesBeforeTheFirstInlineStashAwait()
    {
        var storage = new ByteStore(); var originals = new ByteStore(ephemeral: true);
        var manager = new SnapshotSessionManager(storage, "session-a"); var context = new ContextManager(new() { Strategies = [], StashStorage = originals });
        await using var agent = Create(manager, extra: [context]); await agent.InitializeAsync();
        var entered = Signal(); var release = Signal();
        originals.BeforeList = async () => { entered.TrySetResult(true); await release.Task; };
        var save = manager.SaveSnapshotAsync(agent, false).AsTask();
        try
        {
            await Within(entered.Task); agent.State.Set("label", "future"); agent.ModelState.Set("label", "provider-future");
            Assert.False(save.IsCompleted);
        }
        finally { release.TrySetResult(true); await Within(save); originals.BeforeList = null; }
        Assert.True(await manager.RestoreSnapshotAsync(agent, Assert.IsType<string>(await save)));
        Assert.Equal("saved", Label(agent)); Assert.Equal("provider-saved", agent.ModelState.GetAll()["label"].GetString());
    }

    [Fact]
    public async Task PreparedClassicCallbacksRunOutsideTheAgentMonitorWhileReservationStillRefusesMutation()
    {
        var manager = new SnapshotSessionManager(new ByteStore(), "session-a"); var classic = new CallbackClassicManager();
        await using var agent = Create(manager, classic: classic); await agent.InitializeAsync();
        var gate = typeof(Agent).GetField("_initializationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent)!;
        var seen = new List<string>();
        void Observe(string phase)
        {
            Assert.False(Monitor.IsEntered(gate));
            Assert.Throws<InvalidOperationException>(() => agent.AddOrReplaceApplicationTool(new FunctionTool(Spec(), (_, _, _) => new("unused"))));
            seen.Add(phase);
        }
        classic.Observe = Observe; var id = Assert.IsType<string>(await manager.SaveSnapshotAsync(agent, false));
        classic.Observe = null; await Replace(agent, "current"); classic.Value = "current";
        classic.Observe = Observe; Assert.True(await manager.RestoreSnapshotAsync(agent, id)); classic.Observe = null;
        Assert.Equal("saved", classic.Value); Assert.Equal("saved", Label(agent));
        Assert.Contains("prepare", seen); Assert.Contains("apply", seen); Assert.Contains("write", seen);
    }

    [Fact]
    public async Task FreshMalformedRestoreDoesNotInitializePluginsOrStashOrAlterSeedState()
    {
        var storage = new ByteStore();
        {
            var writerManager = new SnapshotSessionManager(storage, "session-a");
            await using var writer = Create(writerManager); await writer.InitializeAsync(); await writerManager.SaveSnapshotAsync(writer, true);
        }
        var raw = JsonNode.Parse((await storage.ReadAsync(Latest))!)!; raw["snapshot"]!["modelHistoryCount"] = 999;
        await storage.WriteAsync(Latest, JsonSerializer.SerializeToUtf8Bytes(raw));
        var originals = new ByteStore(ephemeral: true); var context = new ContextManager(new() { Strategies = [], StashStorage = originals }); var observer = new ReadingPlugin();
        var manager = new SnapshotSessionManager(storage, "session-a");
        await using var target = Create(manager, "seed", extra: [context, observer]);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RestoreSnapshotAsync(target).AsTask());
        Assert.Equal("seed", Label(target)); Assert.Empty(observer.Labels); Assert.Null(context.Stash); Assert.Equal(0, originals.Writes);
    }

    [Fact]
    public async Task ACustomContextStrategyStillSupportsSelectedRestoreBeforeItsFirstInitialization()
    {
        var storage = new ByteStore();
        {
            var writerManager = new SnapshotSessionManager(storage, "session-a");
            await using var writer = Create(writerManager, extra: [new ContextManager(new() { Strategies = [new CustomStrategy()] })]);
            await writer.InitializeAsync(); await writerManager.SaveSnapshotAsync(writer, true);
        }
        var manager = new SnapshotSessionManager(storage, "session-a"); var strategy = new CustomStrategy(); var observer = new ReadingPlugin();
        await using var target = Create(manager, "seed", extra: [new ContextManager(new() { Strategies = [strategy] }), observer]);
        Assert.True(await manager.RestoreSnapshotAsync(target)); Assert.Equal("saved", Label(target)); Assert.Equal(1, strategy.Initializations); Assert.Equal(new[] { "saved" }, observer.Labels);
    }

    [Fact]
    public async Task LegacyMessageOnlyStoreKeepsOriginalMessageReferencesAndOptionalFieldBehavior()
    {
        var messages = new[] { Message.User("old"), Message.Assistant("answer") };
        await using var target = new Agent(new() { Id = "agent-a", Model = new NeverModel(), SessionStore = new OldStore(new(1, messages)), SessionId = "opaque / old", State = Values("seed"), ModelState = Values("provider-seed"), SystemPrompt = "configured" });
        await target.InitializeAsync();
        Assert.Same(messages[0], target.Messages[0]); Assert.Same(messages[1], target.Messages[1]);
        Assert.Equal("seed", Label(target)); Assert.Equal("provider-seed", target.ModelState.GetAll()["label"].GetString()); Assert.Equal("configured", target.SystemPrompt);
    }

    [Theory]
    [InlineData("external-store")]
    [InlineData("duplicate-manager")]
    [InlineData("session-mismatch")]
    public void ConflictingBindingsRejectBeforeStorageOrApplicationToolGetters(string conflict)
    {
        var storage = new ByteStore(); var manager = new SnapshotSessionManager(storage, "session-a"); var tool = new RefusingSpecTool();
        var options = new AgentOptions { Id = "agent-a", Model = new NeverModel(), Plugins = [manager], Tools = [tool] };
        options = conflict switch
        {
            "external-store" => options with { SessionId = "session-a", SessionStore = new OldStore(null) },
            "duplicate-manager" => options with { Plugins = [manager, manager] },
            _ => options with { SessionId = "other-session" }
        };
        Assert.Throws<ArgumentException>(() => new Agent(options)); Assert.Equal(0, storage.Reads + storage.Writes); Assert.Equal(0, tool.Getters);
    }

    [Fact]
    public async Task ChildrenStripParentBindingAndCanExplicitlyRebindTheirIndependentIdentity()
    {
        var storage = new ByteStore(); var manager = new SnapshotSessionManager(storage, "session-a");
        await using var parent = Create(manager); await parent.InitializeAsync();
        await using var plain = parent.CreateChild();
        Assert.NotEqual(parent.Id, plain.Id); Assert.Null(plain.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ListSnapshotIdsAsync(plain).AsTask());
        var before = storage.Reads; await plain.InitializeAsync(); Assert.Equal(before, storage.Reads);
        await using var rebound = parent.CreateChild(options => options with { Id = "child-a", Plugins = options.Plugins.Concat(new IPlugin[] { manager }).ToArray() });
        await rebound.InitializeAsync(); Assert.Equal(before + 1, storage.Reads); Assert.Equal("session-a", rebound.SessionId);
        Assert.NotNull(await manager.SaveSnapshotAsync(rebound, false));
    }

    [Fact]
    public async Task PagingExcludesLatestMalformedHandlesAndOtherAgentScopesAndShortCircuitsNonpositiveLimit()
    {
        var storage = new ByteStore(); var manager = new SnapshotSessionManager(storage, "session-a");
        await using var target = Create(manager); await target.InitializeAsync();
        var first = Assert.IsType<string>(await manager.SaveSnapshotAsync(target, false)); var second = Assert.IsType<string>(await manager.SaveSnapshotAsync(target, false)); await manager.SaveSnapshotAsync(target, true);
        await storage.WriteAsync("session-a/scopes/agent/agent-a/snapshots/immutable_history/snapshot_invalid.json", []);
        await storage.WriteAsync("session-a/scopes/agent/other/snapshots/immutable_history/snapshot_" + second + ".json", []);
        Assert.Equal(new[] { first, second }, await manager.ListSnapshotIdsAsync(target));
        var lists = storage.Lists; Assert.Empty(await manager.ListSnapshotIdsAsync(target, 0, "not-a-cursor")); Assert.Equal(lists, storage.Lists);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ListSnapshotIdsAsync(target, startAfter: "not-a-cursor").AsTask()); Assert.Equal(lists, storage.Lists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidStashWriteFailureUsesConfiguredPolicyAndDoesNotInventRollback(bool continueAfterFailure)
    {
        var storage = new ByteStore(); var originals = new ByteStore(ephemeral: true);
        var manager = new SnapshotSessionManager(storage, "session-a"); var context = new ContextManager(new() { Strategies = [], StashStorage = originals, StashRestoreFailurePolicy = continueAfterFailure ? StashRestoreFailurePolicy.Continue : StashRestoreFailurePolicy.Throw });
        await using var target = Create(manager, extra: [context]); await target.InitializeAsync(); await manager.SaveSnapshotAsync(target, true); await Replace(target, "current");
        var primary = new IOException("stash write"); originals.WriteFailure = primary;
        if (continueAfterFailure) { Assert.True(await manager.RestoreSnapshotAsync(target)); Assert.Equal("saved", Label(target)); }
        else { Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => manager.RestoreSnapshotAsync(target).AsTask())); Assert.Equal("current", Label(target)); }
        originals.WriteFailure = null;
    }

    [Fact]
    public async Task ManagerNeverDisposesItsBorrowedStorage()
    {
        var storage = new ByteStore(); var manager = new SnapshotSessionManager(storage, "session-a");
        await using (var target = Create(manager)) { await target.InitializeAsync(); await manager.SaveSnapshotAsync(target, true); }
        Assert.False(storage.Disposed); await storage.WriteAsync("caller-owned", [1]); Assert.Equal(new byte[] { 1 }, await storage.ReadAsync("caller-owned"));
    }

    [Fact]
    public async Task RewindPreservesAcceptedMemoryEventWorkWithoutReextractingRestoredHistory()
    {
        var storage = new ByteStore(); var manager = new SnapshotSessionManager(storage, "session-a"); var trigger = new ManualTrigger(); var store = new MessageMemory(trigger);
        var memory = new MemoryManager(new() { Stores = [store], SearchTool = null });
        await using var target = new Agent(new() { Id = "agent-a", Model = new AnswerModel(), Messages = [Message.User("checkpoint"), Message.Assistant("checkpoint-answer")], State = Values("saved"), Plugins = [manager, memory] });
        await target.InitializeAsync(); var old = Assert.IsType<string>(await manager.SaveSnapshotAsync(target, false));
        Assert.Equal(StopReasons.EndTurn, (await target.InvokeAsync("accepted future")).StopReason);
        Assert.Empty(store.Batches); Assert.True(await manager.RestoreSnapshotAsync(target, old));
        Assert.Equal(new[] { "checkpoint", "checkpoint-answer" }, target.Messages.Select(message => message.Text));
        await memory.FlushAsync(); var batch = Assert.Single(store.Batches); Assert.Equal(new[] { "accepted future", "fake answer" }, batch.Messages.Select(message => message.Text)); Assert.Equal(new long[] { 0, 1 }, batch.Sequences);
        await memory.FlushAsync(); Assert.Single(store.Batches);
    }

    private static Agent Create(SnapshotSessionManager manager, string label = "saved", IReadOnlyList<ITool>? tools = null, IReadOnlyList<IPlugin>? extra = null, ConversationManager? classic = null) => new(new()
    {
        Id = "agent-a",
        Model = new NeverModel(),
        SystemPrompt = "prompt-" + label,
        State = Values(label),
        ModelState = Values("provider-" + label),
        Messages = [Message.User(label), Message.Assistant(label + "-response")],
        Tools = tools ?? [],
        Plugins = new IPlugin[] { manager }.Concat(extra ?? []).ToArray(),
        ConversationManager = classic
    });
    private static Task Replace(Agent target, string label) => target.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>
    {
        ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User(label), Message.Assistant(label + "-response") }),
        ["state"] = JsonSerializer.SerializeToElement(Values(label)),
        ["modelState"] = JsonSerializer.SerializeToElement(Values("provider-" + label)),
        ["systemPrompt"] = JsonSerializer.SerializeToElement("prompt-" + label)
    })).AsTask();
    private static IReadOnlyDictionary<string, JsonElement> Values(string label) => new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement(label) };
    private static string Label(Agent target) => target.State.GetAll()["label"].GetString()!;
    private static ToolSpec Spec() => new("probe", "probe", JsonSerializer.SerializeToElement(new { type = "object" }));
    private static ToolUseBlock Call() => new("call-a", "probe", JsonSerializer.SerializeToElement(new { }));
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class NeverModel : IModel { public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model or host process may run."); }
    private sealed class AnswerModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "fake answer"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); }
    }
    private sealed class ReadingPlugin : IPlugin
    { public string Name => "test:reader"; public List<string> Labels { get; } = []; public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Labels.Add(Label(agent)); return default; } public IReadOnlyList<ITool> GetTools() => []; }
    private sealed class CustomStrategy : ContextStrategy
    { public override string Name => "test:custom"; public int Initializations { get; private set; } public override ValueTask InitializeAsync(Agent agent, Stash? stash = null, CancellationToken cancellationToken = default) { Initializations++; return default; } public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default) => new(false); }
    private sealed class CallbackClassicManager : ConversationManager
    {
        public string Value { get; set; } = "saved"; public Action<string>? Observe { get; set; }
        protected override void WriteState(IDictionary<string, object?> state) { Observe?.Invoke("write"); state["value"] = Value; }
        protected override Action PrepareAdditionalState(JsonElement state) { Observe?.Invoke("prepare"); var value = state.GetProperty("value").GetString()!; return () => { Observe?.Invoke("apply"); Value = value; }; }
        protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken) => default;
        protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken) => default;
    }
    private sealed class OldStore(SessionSnapshot? snapshot) : ISessionStore { public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(snapshot); public ValueTask SaveAsync(string sessionId, SessionSnapshot value, CancellationToken cancellationToken = default) => default; }
    private sealed class RefusingSpecTool : ITool
    { public int Getters { get; private set; } public ToolSpec Spec { get { Getters++; throw new InvalidOperationException("Must reject binding before tool getter."); } } public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Never execute."); }
    private sealed class ManualTrigger : ExtractionTrigger { public override string Name => "test:manual"; public override IDisposable Attach(Agent agent, Action fire) => new Noop(); private sealed class Noop : IDisposable { public void Dispose() { } } }
    private sealed class MessageMemory(ExtractionTrigger trigger) : MemoryStore, IMessageMemoryStore
    {
        public override string Name => "events"; public override bool Writable => true; public override MemoryExtractionOptions Extraction => new() { Triggers = [trigger] };
        public List<(IReadOnlyList<Message> Messages, IReadOnlyList<long> Sequences)> Batches { get; } = [];
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)Array.Empty<MemoryEntry>());
        public ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default) { Batches.Add((messages.ToArray(), sequenceNumbers.ToArray())); return default; }
    }
    private sealed class ByteStore(bool ephemeral = false) : IStorage, IStorageCharacteristics, IDisposable
    {
        private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
        public bool IsEphemeral => ephemeral; public bool IsNamespaced => true; public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int Lists { get; private set; }
        public Func<ValueTask>? BeforeList { get; set; }
        public Exception? WriteFailure { get; set; }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { Writes++; if (WriteFailure is { } failure) throw failure; _data[key] = (byte[])data.Clone(); return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { Reads++; return new(_data.TryGetValue(key, out var data) ? (byte[])data.Clone() : null); }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { _data.Remove(key); return default; }
        public async ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) { Lists++; if (BeforeList is { } callback) await callback(); return _data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray(); }
        public void Dispose() => Disposed = true;
    }
}
