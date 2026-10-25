using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.A2A;

public sealed class AbortInterruptTests
{
    [Fact]
    public async Task AbortingCommitsAcceptedSiblingsAndExplicitErrorsInOriginalCallOrderWithoutReplayingBodies()
    {
        var completed = 0; var approvals = 0; var model = new PausingModel();
        await using var agent = Create(model, () => completed++, () => approvals++);
        Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync("run")).StopReason);
        Assert.Equal(1, completed); Assert.Equal(0, approvals);

        Assert.True(await agent.AbortInterruptsAsync("Remote task canceled."));

        Assert.Null(agent.InterruptState);
        var results = agent.Messages.Last().Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "done", "approve" }, results.Select(result => result.ToolUseId));
        Assert.Equal(ToolResultStatus.Success, results[0].Status); Assert.Equal("saved", results[0].Content);
        Assert.Equal(ToolResultStatus.Error, results[1].Status); Assert.Equal("Remote task canceled.", results[1].Content);
        Assert.False(await agent.AbortInterruptsAsync("Again")); Assert.Equal(3, agent.Messages.Count);
        await agent.InvokeAsync(null);
        Assert.Equal(1, completed); Assert.Equal(0, approvals); Assert.Equal(2, model.Calls);
    }
    [Fact]
    public async Task PersistenceFailurePreservesTheOriginalPauseAndRetryNeverDuplicatesCommittedResults()
    {
        var store = new Store(); var effects = 0;
        await using var agent = Create(new(), () => effects++, () => throw new InvalidOperationException(), store);
        await agent.InvokeAsync("run");
        var pending = JsonSerializer.Serialize(agent.InterruptState); var primary = new IOException("save"); store.NextError = primary;

        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => { await agent.AbortInterruptsAsync("cancel"); }));

        Assert.Equal(pending, JsonSerializer.Serialize(agent.InterruptState)); Assert.Equal(2, agent.Messages.Count);
        Assert.NotNull(store.Snapshot!.InterruptState); Assert.Equal(1, effects);
        Assert.True(await agent.AbortInterruptsAsync("cancel")); Assert.Equal(3, agent.Messages.Count);
        Assert.Null(store.Snapshot.InterruptState);
        await using var restored = Create(new(), () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), store);
        await restored.InitializeAsync(); Assert.Null(restored.InterruptState); Assert.Equal(3, restored.Messages.Count);
        Assert.False(await agent.AbortInterruptsAsync("again")); Assert.Equal(3, agent.Messages.Count); Assert.Equal(1, effects);
    }
    [Fact]
    public async Task AcceptedPersistenceFinishesDespiteLateCancellationAndExcludesAllCompetingAgentOperations()
    {
        var store = new Store(); var effects = 0;
        await using var agent = Create(new(), () => effects++, () => throw new InvalidOperationException(), store);
        await agent.InvokeAsync("run"); store.HoldNext = true;
        using var caller = new CancellationTokenSource();
        var abort = agent.AbortInterruptsAsync("cancel", caller.Token).AsTask();
        await store.Entered.Task;
        try
        {
            Assert.False(store.SaveToken.CanBeCanceled); Assert.NotNull(agent.InterruptState); Assert.Equal(2, agent.Messages.Count);
            caller.Cancel();
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync(null));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(async () => { await agent.InvokeToolAsync(new("direct", "done", Json("{}"))); });
            await Assert.ThrowsAsync<InvalidOperationException>(async () => { await agent.AbortInterruptsAsync("nested"); });
            await Assert.ThrowsAsync<InvalidOperationException>(async () => { await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }); });
            Assert.Throws<InvalidOperationException>(() => agent.DisposeAsync());
        }
        finally { store.Release.TrySetResult(true); }
        Assert.True(await abort); Assert.Null(agent.InterruptState); Assert.Equal(1, effects);
    }
    [Fact]
    public async Task CancellationBeforeAdmissionRetainsTheCallerTokenAndTheResumableLedger()
    {
        await using var agent = Create(new(), () => { }, () => { }); await agent.InvokeAsync("run");
        var pending = JsonSerializer.Serialize(agent.InterruptState);
        using var caller = new CancellationTokenSource(); caller.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await agent.AbortInterruptsAsync("cancel", caller.Token); });

        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(pending, JsonSerializer.Serialize(agent.InterruptState)); Assert.Equal(2, agent.Messages.Count);
    }
    [Fact]
    public async Task MessageCallbackFailureRetainsTheDurableAbortAndOriginalErrorWithCleanup()
    {
        var primary = new IOException("message callback"); var cleanup = new IOException("final save");
        var store = new Store(); var hooks = new HookRegistry(); var events = 0;
        hooks.Register<MessageAddedEvent>((item, _) =>
        {
            if (!item.Message.Content.OfType<ToolResultBlock>().Any()) return default;
            events++; store.NextError = cleanup; throw primary;
        });
        await using var agent = Create(new(), () => { }, () => { }, store, hooks); await agent.InvokeAsync("run");

        var error = await Assert.ThrowsAsync<IOException>(async () => { await agent.AbortInterruptsAsync("cancel"); });

        Assert.Same(primary, error); Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.Null(agent.InterruptState); Assert.Null(store.Snapshot!.InterruptState); Assert.Equal(3, store.Snapshot.Messages.Count);
        Assert.False(await agent.AbortInterruptsAsync("again")); Assert.Equal(1, events); Assert.Equal(3, agent.Messages.Count);
    }
    [Fact]
    public async Task MessageCallbacksCannotReenterAbortOrRunToolsAgainstTheTransition()
    {
        var hooks = new HookRegistry(); var callbacks = 0;
        hooks.Register<MessageAddedEvent>(async (item, _) =>
        {
            if (!item.Message.Content.OfType<ToolResultBlock>().Any()) return;
            callbacks++;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => { await item.Agent.AbortInterruptsAsync("nested"); });
            await Assert.ThrowsAsync<ConcurrentInvocationException>(async () => { await item.Agent.InvokeToolAsync(new("direct", "done", Json("{}"))); });
        });
        await using var agent = Create(new(), () => { }, () => { }, hooks: hooks); await agent.InvokeAsync("run");
        Assert.True(await agent.AbortInterruptsAsync("cancel")); Assert.Equal(1, callbacks);
    }
    [Fact]
    public async Task UnacceptedCompletedBodyProofDoesNotBecomeAnApprovedResultDuringAbort()
    {
        var assistant = new Message(Role.Assistant, [new ToolUseBlock("done", "done", Json("{}")), new ToolUseBlock("approve", "approve", Json("{}"))]);
        var pending = new InterruptState(assistant, [new("done", "saved")], [new("question", "approval", "approve", source: InterruptSource.Middleware)], [],
            [new(new("approve", "approve", Json("{}")), "approve", new("approve", "unaccepted body success"))]);
        var store = new Store { Snapshot = new(1, [new(Role.User, [new TextBlock("run")]), assistant]) { InterruptState = pending } };
        await using var agent = Create(new(), () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), store);

        Assert.True(await agent.AbortInterruptsAsync("cancel"));

        var result = agent.Messages.Last().Content.OfType<ToolResultBlock>().Last();
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Equal("cancel", result.Content); Assert.Null(store.Snapshot!.InterruptState);
    }
    [Fact]
    public async Task DisposedAgentsRejectAbortWithoutInitializingOrWriting()
    {
        var store = new Store(); var agent = Create(new(), () => { }, () => { }, store); await agent.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => { await agent.AbortInterruptsAsync("cancel"); });
        Assert.Equal(0, store.Loads); Assert.Null(store.Snapshot);
    }
    internal static Agent Create(PausingModel model, Action completed, Action approved, ISessionStore? store = null, HookRegistry? hooks = null) => new(new()
    {
        Model = model,
        SessionStore = store,
        SessionId = store is null ? null : "session",
        Hooks = hooks ?? new(),
        Tools = [new FunctionTool(new("done", "done", Json("{\"type\":\"object\"}")), (_, _, _) => { completed(); return new ValueTask<string>("saved"); }),
            new FunctionTool(new("approve", "approve", Json("{\"type\":\"object\"}")), (_, context, _) => { context.Interrupt("approval"); approved(); return new ValueTask<string>("approved"); })]
    });
    internal static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    internal sealed class PausingModel : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask; Calls++;
            yield return new ModelMessageStartEvent();
            if (Calls == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("done", "done", Json("{}"))); yield return new ModelContentBlockStopEvent(0);
                yield return new ModelContentBlockStartEvent(1, new ToolUseBlock("approve", "approve", Json("{}"))); yield return new ModelContentBlockStopEvent(1);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "finished"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); }
        }
    }
    private sealed class Store : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; set; }
        internal Exception? NextError { get; set; }
        internal int Loads { get; private set; }
        internal bool HoldNext { get; set; }
        internal CancellationToken SaveToken { get; private set; }
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) { Loads++; return new(Snapshot); }
        public async ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (NextError is { } error) { NextError = null; throw error; }
            if (HoldNext) { HoldNext = false; SaveToken = cancellationToken; Entered.TrySetResult(true); await Release.Task; }
            Snapshot = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot));
        }
    }
}
