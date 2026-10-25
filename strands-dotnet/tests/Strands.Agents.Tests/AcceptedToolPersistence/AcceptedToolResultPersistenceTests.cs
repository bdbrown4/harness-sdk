using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AcceptedToolResultPersistenceTests
{
    [Fact]
    public async Task OrdinaryCompletedResultSaveFailureRetriesWithoutReplay()
    {
        var primary = new IOException("ordinary accepted result save failed"); var failed = false; var bodies = 0; var added = 0;
        var store = new Store { BeforeSave = (snapshot, _) => { if (!failed && HasResult(snapshot, "work-use")) { failed = true; throw primary; } return default; } };
        var model = new Model(Call(("work-use", "work")), Text("later response"));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("work", (_, _, _) => { bodies++; return new("committed result"); })], SessionStore = store, SessionId = "ordinary-save" });
        agent.Hooks.Register<MessageAddedEvent>((value, _) => { if (value.Message.Content.OfType<ToolResultBlock>().Any()) added++; return default; });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("go")));
        Assert.True(failed);
        Assert.Equal(1, bodies);
        Assert.Equal(1, added);
        Assert.Equal("committed result", Assert.Single(Results(agent)).Content);
        Assert.Equal("committed result", Assert.Single(Results(store.Snapshot!)).Content);
        Assert.Equal("later response", (await agent.InvokeAsync(null)).Message!.Text);
        Assert.Equal(1, bodies);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task EarlyStreamCloseJoinsFailedAcceptedResultWriteAndRetries()
    {
        var primary = new IOException("early close accepted write failed"); var failed = false; var bodies = 0; var entered = Gate(); var release = Gate();
        var store = new Store { BeforeSave = async (snapshot, _) => { if (!failed && HasResult(snapshot, "work-use")) { failed = true; entered.TrySetResult(true); await release.Task; throw primary; } } };
        var model = new Model(Call(("work-use", "work")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("work", (_, _, _) => { bodies++; return new("committed result"); })], SessionStore = store, SessionId = "early-close" });
        var iterator = agent.StreamAsync("go").GetAsyncEnumerator(); Task? disposal = null;
        try
        {
            var found = false;
            while (await iterator.MoveNextAsync()) if (iterator.Current is ToolResultEvent) { found = true; break; }
            Assert.True(found);
            var closing = iterator.DisposeAsync().AsTask(); disposal = closing;
            try { await Wait(entered.Task); Assert.False(closing.IsCompleted); }
            finally { release.TrySetResult(true); await JoinFailure(closing); }
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => closing));
            Assert.Equal("committed result", Assert.Single(Results(store.Snapshot!)).Content);
            Assert.Equal(1, bodies);
            Assert.Equal(1, model.Calls);
        }
        finally
        {
            release.TrySetResult(true);
            if (disposal is null) await JoinFailure(iterator.DisposeAsync().AsTask());
            else await JoinFailure(disposal);
        }
    }

    [Fact]
    public async Task CallerCancellationDuringAcceptedWritePreservesReceiptAndCause()
    {
        using var caller = new CancellationTokenSource(); var primary = new IOException("independent accepted write failure"); var failed = false; var entered = Gate(); var release = Gate(); var bodies = 0; var callbacks = new List<CancellationToken>();
        var store = new Store { BeforeSave = async (snapshot, token) => { if (!failed && HasResult(snapshot, "work-use")) { failed = true; Assert.Equal(CancellationToken.None, token); entered.TrySetResult(true); await release.Task; throw primary; } } };
        var model = new Model(Call(("work-use", "work")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("work", (_, _, _) => { bodies++; return new("committed result"); })], SessionStore = store, SessionId = "caller-during-write" });
        agent.Hooks.Register<MessageAddedEvent>((value, token) => { if (value.Message.Content.OfType<ToolResultBlock>().Any()) callbacks.Add(token); return default; });
        var operation = agent.InvokeAsync("go", cancellationToken: caller.Token);
        try { await Wait(entered.Task); caller.Cancel(); Assert.False(operation.IsCompleted); }
        finally { release.TrySetResult(true); await JoinFailure(operation); }
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => operation));
        Assert.Equal(CancellationToken.None, Assert.Single(callbacks));
        Assert.Equal("committed result", Assert.Single(Results(store.Snapshot!)).Content);
        Assert.Equal(1, bodies);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task IndependentMessageAddedAndWriteFailuresKeepPrimaryAndUniqueCleanup()
    {
        var primary = new IOException("independent result observer failure"); var secondary = new ApplicationException("accepted write cleanup failure"); var failed = false; var bodies = 0; var callbacks = 0;
        var store = new Store { BeforeSave = (snapshot, _) => { if (!failed && HasResult(snapshot, "work-use")) { failed = true; throw secondary; } return default; } };
        var model = new Model(Call(("work-use", "work")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("work", (_, _, _) => { bodies++; return new("committed result"); })], SessionStore = store, SessionId = "hook-and-save" });
        agent.Hooks.Register<MessageAddedEvent>((value, _) => { if (value.Message.Content.OfType<ToolResultBlock>().Any()) { callbacks++; throw primary; } return default; });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("go")));
        AssertUniqueCleanup(primary, secondary);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, bodies);
        Assert.Equal("committed result", Assert.Single(Results(store.Snapshot!)).Content);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task IndependentAfterToolsFailureSurvivesResultWriteRetry()
    {
        var primary = new IOException("independent after-tools failure"); var secondary = new ApplicationException("accepted result save failure"); var failed = false; var bodies = 0; var callbacks = 0;
        var store = new Store { BeforeSave = (snapshot, _) => { if (!failed && HasResult(snapshot, "work-use")) { failed = true; throw secondary; } return default; } };
        var model = new Model(Call(("work-use", "work")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("work", (_, _, _) => { bodies++; return new("committed result"); })], SessionStore = store, SessionId = "after-tools-and-save" });
        agent.Hooks.Register<AfterToolsEvent>((_, _) => { callbacks++; throw primary; });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("go")));
        AssertUniqueCleanup(primary, secondary);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, bodies);
        Assert.Equal("committed result", Assert.Single(Results(store.Snapshot!)).Content);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task SuccessfulConsecutiveSavesDoNotLeaveHistorySpuriouslyDirty()
    {
        var bodies = 0; var store = new Store(); var model = new Model(Call(("work-use", "work")), Text("final response"));
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("work", (_, _, _) => { bodies++; return new("committed result"); })], SessionStore = store, SessionId = "successful-saves" });
        Assert.Equal("final response", (await agent.InvokeAsync("go")).Message!.Text);
        Assert.Equal(new[] { 1, 2, 3, 4 }, store.Successful.Select(snapshot => snapshot.Messages.Count));
        Assert.Empty(Results(store.Successful[0]));
        Assert.Empty(Results(store.Successful[1]));
        Assert.Equal("committed result", Assert.Single(Results(store.Successful[2])).Content);
        Assert.Equal(4, store.Attempts.Count);
        Assert.Equal(1, bodies);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task IndependentAgentsKeepTheirExactAcceptedReceipts()
    {
        var primary = new IOException("first owner write failure"); var failed = false; var enteredA = Gate(); var enteredB = Gate(); var releaseA = Gate(); var releaseB = Gate(); var bodiesA = 0; var bodiesB = 0;
        var firstStore = new Store { BeforeSave = async (snapshot, _) => { if (!failed && HasResult(snapshot, "a-use")) { failed = true; enteredA.TrySetResult(true); await releaseA.Task; throw primary; } } };
        var secondStore = new Store { BeforeSave = async (snapshot, _) => { if (HasResult(snapshot, "b-use") && snapshot.Messages.Count == 3) { enteredB.TrySetResult(true); await releaseB.Task; } } };
        var firstModel = new Model(Call(("a-use", "work")), Text("must not run")); var secondModel = new Model(Call(("b-use", "work")), Text("second final response"));
        await using var first = new Agent(new() { Model = firstModel, Tools = [Tool("work", (_, _, _) => { bodiesA++; return new("first result"); })], SessionStore = firstStore, SessionId = "first-owner" });
        await using var second = new Agent(new() { Model = secondModel, Tools = [Tool("work", (_, _, _) => { bodiesB++; return new("second result"); })], SessionStore = secondStore, SessionId = "second-owner" });
        var a = first.InvokeAsync("go"); var b = second.InvokeAsync("go");
        try { await Wait(Task.WhenAll(enteredA.Task, enteredB.Task)); releaseB.TrySetResult(true); await Wait(b); Assert.False(a.IsCompleted); }
        finally { releaseA.TrySetResult(true); releaseB.TrySetResult(true); await JoinFailure(Task.WhenAll(a, b)); }
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => a));
        Assert.Equal("second final response", (await b).Message!.Text);
        Assert.Equal("first result", Assert.Single(Results(firstStore.Snapshot!)).Content);
        Assert.Equal("second result", Assert.Single(Results(secondStore.Snapshot!)).Content);
        Assert.Equal(1, bodiesA); Assert.Equal(1, bodiesB);
        Assert.Equal(1, firstModel.Calls); Assert.Equal(2, secondModel.Calls);
    }

    [Fact]
    public async Task PausedAcceptedInterruptSaveFailureRetriesDurableLedger()
    {
        var primary = new IOException("accepted pause ledger save failed"); var failed = false; var entered = Gate(); var release = Gate(); var doneBodies = 0; var approvalAttempts = 0; var approvedEffects = 0;
        var store = new Store { BeforeSave = async (snapshot, token) => { if (!failed && snapshot.InterruptState is { CompletedToolResults.Count: 1, Interrupts.Count: 1 }) { failed = true; Assert.Equal(CancellationToken.None, token); entered.TrySetResult(true); await release.Task; throw primary; } } };
        var done = Tool("done", (_, _, _) => { doneBodies++; return new("already committed sibling"); });
        var approve = Tool("approve", (_, context, _) => { approvalAttempts++; context.Interrupt("approval", Json("\"Proceed?\"")); approvedEffects++; return new("approved"); });
        var model = new Model(Call(("done-use", "done"), ("approval-use", "approve")), Text("must not run"));
        await using (var first = new Agent(new() { Model = model, Tools = [done, approve], ToolExecution = ToolExecutionStrategy.Sequential, SessionStore = store, SessionId = "paused-save" }))
        {
            var operation = first.InvokeAsync("go");
            try { await Wait(entered.Task); Assert.False(operation.IsCompleted); }
            finally { release.TrySetResult(true); await JoinFailure(operation); }
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => operation));
            Assert.Equal(1, doneBodies); Assert.Equal(1, approvalAttempts); Assert.Equal(0, approvedEffects);
            Assert.NotNull(first.InterruptState);
            Assert.NotNull(store.Snapshot!.InterruptState);
            Assert.Equal("already committed sibling", Assert.Single(store.Snapshot.InterruptState!.CompletedToolResults).Content);
        }
        var question = Assert.Single(store.Snapshot!.InterruptState!.Interrupts);
        var resumedModel = new Model(Text("after answer"));
        await using var resumed = new Agent(new() { Model = resumedModel, Tools = [done, approve], ToolExecution = ToolExecutionStrategy.Sequential, SessionStore = store, SessionId = "paused-save" });
        var result = await resumed.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] });
        Assert.Equal("after answer", result.Message!.Text);
        Assert.Equal(1, doneBodies); Assert.Equal(2, approvalAttempts); Assert.Equal(1, approvedEffects);
        Assert.Null(resumed.InterruptState);
    }

    private static void AssertUniqueCleanup(Exception primary, Exception secondary)
    {
        var errors = Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]);
        Assert.Single(errors, value => ReferenceEquals(value, secondary));
        Assert.DoesNotContain(errors, value => ReferenceEquals(value, primary));
        Assert.All(errors, value => Assert.Single(errors, other => ReferenceEquals(value, other)));
    }
    private static ToolResultBlock[] Results(Agent agent) => agent.Messages.SelectMany(value => value.Content).OfType<ToolResultBlock>().ToArray();
    private static ToolResultBlock[] Results(SessionSnapshot snapshot) => snapshot.Messages.SelectMany(value => value.Content).OfType<ToolResultBlock>().ToArray();
    private static bool HasResult(SessionSnapshot snapshot, string id) => Results(snapshot).Any(value => value.ToolUseId == id);
    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback) => new(new(name, name, Json("{\"type\":\"object\"}")), callback);
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static async Task JoinFailure(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); _ = await Record.ExceptionAsync(() => task); }
    private static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static ModelStreamEvent[] Call(params (string Id, string Name)[] calls)
    {
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        for (var index = 0; index < calls.Length; index++) { var call = calls[index]; events.Add(new ModelContentBlockStartEvent(index, new(call.Id, call.Name, Json("{}")))); events.Add(new ModelContentBlockDeltaEvent(index, ToolInput: "{}")); events.Add(new ModelContentBlockStopEvent(index)); }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse)); return events.ToArray();
    }
    private sealed class Model(params ModelStreamEvent[][] turns) : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var selected = turns[Calls++]; foreach (var item in selected) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); } }
    }
    private sealed class Store : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; private set; }
        internal List<SessionSnapshot> Attempts { get; } = [];
        internal List<SessionSnapshot> Successful { get; } = [];
        internal Func<SessionSnapshot, CancellationToken, ValueTask>? BeforeSave { get; init; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public async ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { Attempts.Add(snapshot); if (BeforeSave is { } before) await before(snapshot, cancellationToken); Snapshot = snapshot; Successful.Add(snapshot); }
    }
}
