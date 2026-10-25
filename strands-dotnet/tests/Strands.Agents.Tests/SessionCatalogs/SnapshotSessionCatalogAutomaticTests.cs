using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

public sealed class SnapshotSessionCatalogAutomaticTests
{
    [Fact]
    public async Task DefaultInvocationDefersMessageWritesAndCapturesAfterCompletionHooks()
    {
        var storage = new RecordingStorage(); var manager = new SnapshotSessionManager(storage, "session-a"); var entered = Signal(); var release = Signal();
        var model = new AnswerModel(async () => { entered.TrySetResult(true); await release.Task; });
        var hooks = new HookRegistry(); hooks.Register<AfterInvocationEvent>((value, _) => { value.Agent.State.Set("label", "after"); return default; });
        await using var target = Create(manager, model, hooks); await target.InitializeAsync();
        Assert.Equal(SnapshotSessionSaveStrategy.Invocation, manager.SaveLatestOn);
        var invocation = target.InvokeAsync("new input");
        try { await Within(entered.Task); Assert.Empty(storage.Writes); }
        finally { release.TrySetResult(true); await Within(invocation); }
        var write = Assert.Single(storage.Writes); Assert.EndsWith("snapshot_latest.json", write.Key);
        Assert.Equal("after", StateLabel(write.Bytes));
    }

    [Fact]
    public async Task MessageStrategySavesAfterEachCompletedAcceptedDispatchAndAfterFinalHooks()
    {
        var storage = new RecordingStorage(); var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Message); var before = new List<int>();
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) => { before.Add(storage.Writes.Count); value.Agent.State.Set("label", value.Message.Text); return default; });
        hooks.Register<AfterInvocationEvent>((value, _) => { value.Agent.State.Set("label", "after"); return default; });
        await using var target = Create(manager, new AnswerModel(), hooks);
        await target.InvokeAsync(null, new() { InputMessages = [Message.User("one"), Message.User("two")] });
        Assert.Equal(new[] { 0, 1, 2 }, before);
        Assert.Equal(new[] { "one", "two", "answer", "after" }, storage.Writes.Select(write => StateLabel(write.Bytes)));
        Assert.All(storage.Writes.Take(2), write => Assert.Equal(4, MessageCount(write.Bytes)));
    }

    [Fact]
    public async Task TriggerFalseDoesNotPretendSuppressedWritesWerePersisted()
    {
        var storage = new RecordingStorage(); var calls = 0; var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Trigger, _ => { calls++; return false; });
        await using var target = Create(manager, new AnswerModel()); await target.InvokeAsync("accepted");
        Assert.Empty(storage.Writes); Assert.Equal(1, calls);
        target.State.Set("label", "manual"); Assert.Null(await manager.SaveSnapshotAsync(target, true));
        Assert.Equal("manual", StateLabel(Assert.Single(storage.Writes).Bytes));
    }

    [Fact]
    public async Task TriggerTrueWritesImmutableThenLatestFromOneDetachedCapture()
    {
        var storage = new RecordingStorage(); var calls = 0; var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Trigger, _ => { calls++; return true; });
        await using var target = Create(manager, new AnswerModel());
        storage.BeforeWrite = key => { if (key.IndexOf("immutable_history/", StringComparison.Ordinal) >= 0) target.State.Set("label", "after-capture"); };
        await target.InvokeAsync("accepted");
        Assert.Equal(1, calls); Assert.Equal(2, storage.Writes.Count); Assert.Contains("immutable_history/", storage.Writes[0].Key); Assert.EndsWith("snapshot_latest.json", storage.Writes[1].Key);
        Assert.Equal("saved", StateLabel(storage.Writes[0].Bytes)); Assert.Equal("saved", StateLabel(storage.Writes[1].Bytes));
        Assert.Single(await manager.ListSnapshotIdsAsync(target));
    }

    [Fact]
    public async Task ManualSaveRemainsRealAndPolicyIndependentWithoutInvokingTheTrigger()
    {
        var storage = new RecordingStorage(); var manager = new SnapshotSessionManager(storage, "session-a", SnapshotSessionSaveStrategy.Trigger, _ => throw new InvalidOperationException("Manual methods never evaluate a trigger."));
        await using var target = Create(manager, new AnswerModel());
        var id = Assert.IsType<string>(await manager.SaveSnapshotAsync(target, false)); Assert.Null(await manager.SaveSnapshotAsync(target, true));
        Assert.Equal(2, storage.Writes.Count); Assert.Equal(new[] { id }, await manager.ListSnapshotIdsAsync(target));
    }

    private static Agent Create(SnapshotSessionManager manager, IModel model, HookRegistry? hooks = null) => new(new()
    {
        Id = "agent-a",
        Model = model,
        Hooks = hooks ?? new(),
        Plugins = [manager],
        Messages = [Message.User("seed"), Message.Assistant("seed answer")],
        State = new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("saved") }
    });
    private static string? StateLabel(byte[] bytes) { using var document = JsonDocument.Parse(bytes); return document.RootElement.GetProperty("snapshot").GetProperty("state").GetProperty("label").GetString(); }
    private static int MessageCount(byte[] bytes) { using var document = JsonDocument.Parse(bytes); return document.RootElement.GetProperty("snapshot").GetProperty("messages").GetArrayLength(); }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class AnswerModel(Func<Task>? before = null) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { if (before is not null) await before(); else await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "answer"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); }
    }
    private sealed class RecordingStorage : IStorage, IStorageCharacteristics
    {
        private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
        public bool IsEphemeral => false; public bool IsNamespaced => true;
        public List<(string Key, byte[] Bytes)> Writes { get; } = []; public Action<string>? BeforeWrite { get; set; }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { var copy = (byte[])data.Clone(); BeforeWrite?.Invoke(key); Writes.Add((key, copy)); _data[key] = copy; return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => new(_data.TryGetValue(key, out var value) ? (byte[])value.Clone() : null);
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { _data.Remove(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => new((IReadOnlyList<string>)_data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }
}
