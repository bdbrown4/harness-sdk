using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

[Collection("SDK logging")]
public sealed class SnapshotSessionCatalogAutomaticSafetyTests
{
    private const string Latest = "session-a/scopes/agent/agent-a/snapshots/snapshot_latest.json";

    [Theory]
    [InlineData(SnapshotSessionSaveStrategy.Invocation)]
    [InlineData(SnapshotSessionSaveStrategy.Message)]
    [InlineData(SnapshotSessionSaveStrategy.Trigger)]
    public async Task StandaloneAbortPersistsStateFromItsAcceptedMessageHook(SnapshotSessionSaveStrategy strategy)
    {
        var storage = new Store(); var triggerCalls = 0; var manager = new SnapshotSessionManager(storage, "session-a", strategy, _ => { triggerCalls++; return false; });
        var hooks = new HookRegistry(); hooks.Register<MessageAddedEvent>((value, _) => { if (value.Message.Content.OfType<ToolResultBlock>().Any()) value.Agent.State.Set("label", "after-abort-hook"); return default; });
        var bodies = 0; var tool = new FunctionTool(new("approve", "approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => { bodies++; return new(context.Interrupt("approval").GetRawText()); });
        var model = new PausingModel(); await using var target = new Agent(new() { Id = "agent-a", Model = model, Tools = [tool], Plugins = [manager], Hooks = hooks });
        Assert.Equal(StopReasons.Interrupt, (await target.InvokeAsync("pause")).StopReason); Assert.NotNull(target.InterruptState);
        Assert.True(await target.AbortInterruptsAsync("settled"));
        Assert.Equal("after-abort-hook", Label((await storage.ReadAsync(Latest))!)); Assert.Null(target.InterruptState);
        Assert.Equal(1, bodies); Assert.Equal(1, model.Calls); Assert.Equal(1, triggerCalls);
        using var data = JsonDocument.Parse((await storage.ReadAsync(Latest))!); Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("snapshot").GetProperty("interruptState").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RaisingTriggerFallsBackToLatestEvenWhenDiagnosticObserverFails(bool throwingLogger)
    {
        var original = SdkLogging.Logger; var logger = new ProbeLogger(throwingLogger); var storage = new Store(); var primary = new IOException("trigger cause");
        var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Trigger, _ => throw primary);
        try
        {
            SdkLogging.ConfigureLogging(logger); await using var target = Create(manager, new TextModel());
            Assert.Equal(StopReasons.EndTurn, (await target.InvokeAsync("accepted")).StopReason);
            Assert.Single(storage.Writes); Assert.EndsWith("snapshot_latest.json", storage.Writes[0].Key); Assert.Empty(await manager.ListSnapshotIdsAsync(target));
            Assert.Same(primary, logger.Error); Assert.False(logger.Disposed);
        }
        finally { SdkLogging.ConfigureLogging(original); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairWriteFailuresNeverAdvanceLatestPastTheAcceptedImmutableFrontier(bool failLatest)
    {
        var storage = new Store(); var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Trigger, _ => true); var model = new TextModel();
        await using var target = Create(manager, model); await manager.SaveSnapshotAsync(target, true); var prior = (await storage.ReadAsync(Latest))!;
        var primary = new IOException(failLatest ? "latest cause" : "immutable cause");
        storage.Failure = key => (key.EndsWith("snapshot_latest.json", StringComparison.Ordinal) == failLatest) ? primary : null;
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => target.InvokeAsync("accepted")));
        Assert.Equal(prior, await storage.ReadAsync(Latest));
        var history = await manager.ListSnapshotIdsAsync(target); if (failLatest) Assert.Single(history); else Assert.Empty(history);
        storage.Failure = null; target.State.Set("label", "explicit-retry"); await manager.SaveSnapshotAsync(target, true);
        Assert.Equal("explicit-retry", Label((await storage.ReadAsync(Latest))!)); Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task FailedInitializationNeverEvaluatesTheTriggerOrWritesACompletionCheckpoint()
    {
        var storage = new Store(); var calls = 0; var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Invocation, _ => { calls++; return true; }); var primary = new IOException("initialization cause");
        await using var target = new Agent(new() { Id = "agent-a", Model = new TextModel(), Plugins = [manager, new FailingPlugin(primary)] });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => target.InvokeAsync("never accepted")));
        Assert.Empty(storage.Writes); Assert.Equal(0, calls);
        await using var fresh = Create(manager, new TextModel()); await fresh.InitializeAsync();
    }

    [Fact]
    public async Task LegacyStoreKeepsItsWholeBatchAndPostHookWriteBoundaries()
    {
        var store = new LegacyStore(); var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) => { value.Agent.State.Set("label", value.Message.Text); return default; });
        hooks.Register<AfterInvocationEvent>((value, _) => { value.Agent.State.Set("label", "after"); return default; });
        await using var target = new Agent(new() { Model = new TextModel(), SessionStore = store, SessionId = "old", Hooks = hooks });
        await target.InvokeAsync(null, new() { InputMessages = [Message.User("one"), Message.User("two")] });
        Assert.Equal(new[] { "two", "answer", "after" }, store.Snapshots.Select(value => value.State!["label"].GetString()));
        Assert.Equal(new[] { 2, 3, 3 }, store.Snapshots.Select(value => value.Messages.Count));
    }

    [Theory]
    [InlineData(SnapshotSessionSaveStrategy.Invocation)]
    [InlineData(SnapshotSessionSaveStrategy.Message)]
    [InlineData(SnapshotSessionSaveStrategy.Trigger)]
    public async Task RedactionForcesARealLatestWriteBeforeAssistantAcceptanceInEveryStrategy(SnapshotSessionSaveStrategy strategy)
    {
        var storage = new Store(); var manager = new SnapshotSessionManager(storage, "session-a", strategy, _ => false); var hooks = new HookRegistry(); var checks = 0;
        hooks.Register<MessageAddedEvent>(async (value, _) =>
        {
            if (value.Message.Role != Role.Assistant) return;
            using var document = JsonDocument.Parse((await storage.ReadAsync(Latest))!);
            var snapshot = document.RootElement.GetProperty("snapshot").Deserialize<SessionSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal("safe input", snapshot.Messages.Last().Text); checks++;
        });
        await using var target = Create(manager, new TextModel(redact: true), hooks);
        await target.InvokeAsync("secret input"); Assert.Equal(1, checks);
        Assert.DoesNotContain("secret input", System.Text.Encoding.UTF8.GetString((await storage.ReadAsync(Latest))!));
    }

    [Fact]
    public async Task CompletionCheckpointPhysicallyJoinsAndUsesTheExistingNoncancelableCleanupToken()
    {
        var storage = new Store(); var manager = new SnapshotSessionManager(storage, "session-a"); var entered = Signal(); var release = Signal();
        await using var target = Create(manager, new TextModel());
        storage.BeforeWrite = async (_, token) => { Assert.False(token.CanBeCanceled); entered.TrySetResult(true); await release.Task; };
        var invocation = target.InvokeAsync("accepted");
        try { await Within(entered.Task); Assert.False(invocation.IsCompleted); await Assert.ThrowsAsync<InvalidOperationException>(() => target.DisposeAsync().AsTask()); }
        finally { release.TrySetResult(true); await Within(invocation); storage.BeforeWrite = null; }
        Assert.Single(storage.Writes);
    }

    [Fact]
    public async Task TriggerHasNoPublicManualOrLifecycleAdmissionBypass()
    {
        var storage = new Store(); SnapshotSessionManager? manager = null; var calls = 0;
        manager = new(storage, "session-a", SnapshotSessionSaveStrategy.Trigger, target =>
        {
            calls++;
            Assert.Throws<InvalidOperationException>(() => manager!.SaveSnapshotAsync(target, true).GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(() => { _ = target.ShutdownAsync(); });
            return true;
        });
        await using var target = Create(manager, new TextModel()); await target.InvokeAsync("accepted"); Assert.Equal(1, calls); Assert.Equal(2, storage.Writes.Count);
    }

    [Fact]
    public async Task AcceptedInputPrimaryAndCompletionSaveCleanupKeepTheirCausalIdentities()
    {
        var storage = new Store(); var manager = new SnapshotSessionManager(storage, "session-a"); var primary = new IOException("before model cause"); var cleanup = new IOException("completion save cause"); var hooks = new HookRegistry(); var fail = true;
        hooks.Register<BeforeModelCallEvent>((_, _) => { if (fail) throw primary; return default; });
        await using var target = Create(manager, new TextModel(), hooks); storage.Failure = _ => cleanup;
        var error = await Assert.ThrowsAsync<IOException>(() => target.InvokeAsync("accepted once"));
        Assert.Same(primary, error); Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal("accepted once", target.Messages.Last().Text);
        storage.Failure = null; fail = false; await target.InvokeAsync(null);
        using var document = JsonDocument.Parse((await storage.ReadAsync(Latest))!); var snapshot = document.RootElement.GetProperty("snapshot").Deserialize<SessionSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(snapshot.Messages, message => message.Text == "accepted once");
    }

    [Fact]
    public async Task FailedAttemptWithNoAcceptedHistoryStillRunsCompletionStrategy()
    {
        var storage = new Store(); var calls = 0; var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Invocation, _ => { calls++; return false; }); var primary = new IOException("before input"); var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((_, _) => throw primary);
        await using var target = new Agent(new() { Id = "agent-a", Model = new TextModel(), Plugins = [manager], Hooks = hooks });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => target.InvokeAsync("unaccepted")));
        Assert.Single(storage.Writes); Assert.Equal(1, calls); Assert.Empty(target.Messages);
    }

    private static Agent Create(SnapshotSessionManager manager, IModel model, HookRegistry? hooks = null) => new(new() { Id = "agent-a", Model = model, Plugins = [manager], Hooks = hooks ?? new(), State = new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("saved") } });
    private static string? Label(byte[] data) { using var document = JsonDocument.Parse(data); return document.RootElement.GetProperty("snapshot").GetProperty("state").GetProperty("label").GetString(); }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class TextModel(bool redact = false) : IModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "answer"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); if (redact) yield return new ModelRedactionEvent("safe input"); }
    }
    private sealed class PausingModel : IModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0, new("approval-call", "approve", JsonSerializer.SerializeToElement(new { }))); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse); }
    }
    private sealed class FailingPlugin(Exception failure) : IPlugin { public string Name => "test:failure"; public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => throw failure; public IReadOnlyList<ITool> GetTools() => []; }
    private sealed class LegacyStore : ISessionStore
    { public List<SessionSnapshot> Snapshots { get; } = []; public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new((SessionSnapshot?)null); public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Snapshots.Add(snapshot); return default; } }
    private sealed class Store : IStorage, IStorageCharacteristics
    {
        private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
        public bool IsEphemeral => false; public bool IsNamespaced => true;
        public List<(string Key, byte[] Bytes)> Writes { get; } = []; public Func<string, Exception?>? Failure { get; set; }
        public Func<string, CancellationToken, ValueTask>? BeforeWrite { get; set; }
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { var copy = (byte[])data.Clone(); if (BeforeWrite is { } before) await before(key, cancellationToken); if (Failure?.Invoke(key) is { } error) throw error; Writes.Add((key, copy)); _data[key] = copy; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => new(_data.TryGetValue(key, out var data) ? (byte[])data.Clone() : null);
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { _data.Remove(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => new((IReadOnlyList<string>)_data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }
    private sealed class ProbeLogger(bool throwing) : ILogger, IDisposable
    {
        public Exception? Error { get; private set; }
        public bool Disposed { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { Error = exception; if (throwing) throw new IOException("logger cause"); }
        public void Dispose() => Disposed = true;
    }
}
