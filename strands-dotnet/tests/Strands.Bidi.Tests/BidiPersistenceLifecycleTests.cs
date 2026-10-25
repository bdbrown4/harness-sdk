using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiPersistenceTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiPersistenceLifecycleTests
{
    [Fact]
    public async Task CancelledInitializationCannotOutliveDisposalAndMutateHistory()
    {
        var entered = Signal(); var release = Signal(); var store = new ControlledStore { Read = async () => { entered.TrySetResult(true); await release.Task; return new(1, [Message.User("late")]); } };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        using var cancellation = new CancellationTokenSource(); var initialization = agent.InitializeAsync(cancellation.Token); await Within(entered.Task); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        var disposal = agent.DisposeAsync().AsTask();
        try { Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(100))); }
        finally { release.TrySetResult(true); }
        await Bounded(disposal); Assert.Empty(agent.Messages); Assert.Equal(0, model.Starts);
    }

    [Fact]
    public async Task ConcurrentSavesSerializeAndCaptureStateAfterTheirOwnAdmission()
    {
        var entered = Signal(); var release = Signal(); var writes = new List<SessionSnapshot>(); var active = 0; var maximum = 0;
        var store = new ControlledStore { Write = async value => { maximum = Math.Max(maximum, ++active); writes.Add(value); if (writes.Count == 1) { entered.TrySetResult(true); await release.Task; } active--; } };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        agent.State.Set("version", 1); var first = agent.SaveSessionAsync().AsTask(); await Within(entered.Task); agent.State.Set("version", 2); var second = agent.SaveSessionAsync().AsTask();
        try { Assert.False(second.IsCompleted); Assert.Single(writes); } finally { release.TrySetResult(true); }
        await Bounded(Task.WhenAll(first, second)); Assert.Equal(1, maximum); Assert.Equal(new[] { 1, 2 }, writes.Select(value => value.State!["version"].GetInt32()));
    }

    [Fact]
    public async Task DisposalJoinsAnAlreadyAdmittedExplicitSessionWrite()
    {
        var entered = Signal(); var release = Signal(); var store = new ControlledStore { Write = async _ => { entered.TrySetResult(true); await release.Task; } };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store });
        var save = agent.SaveSessionAsync().AsTask(); await Within(entered.Task); var disposal = agent.DisposeAsync().AsTask();
        try { Assert.NotSame(disposal, await Task.WhenAny(disposal, Task.Delay(100))); }
        finally { release.TrySetResult(true); }
        await Bounded(Task.WhenAll(save, disposal));
    }

    [Fact]
    public async Task InvalidSnapshotFailsBeforeSessionReadOrInitializationObservers()
    {
        var store = new MemoryStore { LoadError = new IOException("must not read") }; await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store }); var initialized = false;
        agent.Hooks.Register<BidiInitializedEvent>((_, _) => { initialized = true; return default; });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["state"] = JsonSerializer.SerializeToElement(3) })));
        Assert.Equal(0, store.Loads); Assert.False(initialized); Assert.Equal(0, model.Starts);
    }

    [Fact]
    public async Task RestoredDispatchAndTranscriptReservationsBecomeIncompleteBeforeConnecting()
    {
        var call = new ToolUseBlock("old", "work", JsonSerializer.SerializeToElement(new { }));
        var pending = JsonSerializer.SerializeToElement(new { custom = new { bidi = new { kind = "tool_dispatch" } } });
        var transcript = JsonSerializer.SerializeToElement(new { custom = new { bidi = new { kind = "transcript", status = "pending" } } });
        var messages = new[] { new Message(Role.Assistant, [call]), new Message(Role.User, [new ToolResultBlock("old", "pending", ToolResultStatus.Error)], metadata: pending), new Message(Role.User, [], metadata: transcript) };
        var store = new MemoryStore { Snapshot = new(1, messages) }; await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store }); var updated = new List<Message>();
        agent.Hooks.Register<BidiMessageUpdatedEvent>((value, _) => { Assert.Equal(0, model.Starts); updated.Add(value.Message); return default; });
        await agent.StartAsync(); Assert.Equal(2, updated.Count); Assert.Equal(messages[1].TrackingId, updated[0].TrackingId); Assert.Contains("unknown", Assert.Single(updated[0].Content.OfType<ToolResultBlock>()).Content); Assert.Equal("[Transcript unavailable.]", updated[1].Text);
        Assert.Equal("incomplete", model.Session!.Messages[2].Metadata!.Value.GetProperty("custom").GetProperty("bidi").GetProperty("status").GetString()); await agent.StopAsync();
    }

    [Fact]
    public async Task InitializationObserverFailureIsCachedWithoutReplayingRestoration()
    {
        var failure = new IOException("initialized observer"); var store = new MemoryStore(); await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionStore = store }); var callbacks = 0;
        agent.Hooks.Register<BidiInitializedEvent>((_, _) => { callbacks++; throw failure; });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync())); Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.StartAsync())); Assert.Equal(1, callbacks); Assert.Equal(1, store.Loads); Assert.Equal(0, store.Saves);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Bounded(Task task) => await Within(CompleteTask(task));
    private static async Task<bool> CompleteTask(Task task) { await task; return true; }
    private sealed class ControlledStore : ISessionStore
    {
        internal Func<Task<SessionSnapshot?>>? Read; internal Func<SessionSnapshot, Task>? Write;
        public async ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken cancellationToken = default) => Read is { } read ? await read() : null;
        public async ValueTask SaveAsync(string id, SessionSnapshot value, CancellationToken cancellationToken = default) { if (Write is { } write) await write(value); }
    }
}
