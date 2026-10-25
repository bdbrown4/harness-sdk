using System.Text.Json;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.Conversation;

public sealed class ClassicCheckpointTests
{
    [Fact]
    public async Task ExplicitSummaryCannotEnterWhileAnInvocationAlreadyOwnsHistory()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = async () => { if (Interlocked.Increment(ref calls) == 1) entered.TrySetResult(true); else secondOpen.TrySetResult(true); await release.Task; } };
        await using var agent = ClassicConversationManagerTests.Create(ClassicConversationManagerTests.History(8), model);
        var manager = new Classic.SummarizingConversationManager(new() { PreserveRecentMessages = 2 });
        var invoking = agent.InvokeAsync("main");
        Task? reducing = null;
        Exception? invocationFailure = null;
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            reducing = manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow")).AsTask();
            Assert.Same(reducing, await Task.WhenAny(reducing, secondOpen.Task, Task.Delay(8000)));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => reducing);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult(true);
            try { await invoking; } catch (Exception error) { invocationFailure = error; }
            if (reducing is not null) try { await reducing; } catch (Exception) { }
        }
        Assert.Null(invocationFailure);
        Assert.Equal(0, manager.RemovedMessageCount);
    }
    [Fact]
    public async Task AgentInvocationCannotEnterWhileAnExplicitSummaryOwnsItsHistory()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = async () => { if (Interlocked.Increment(ref calls) == 1) entered.TrySetResult(true); else secondOpen.TrySetResult(true); await release.Task; } };
        await using var agent = ClassicConversationManagerTests.Create(ClassicConversationManagerTests.History(8), model);
        var manager = new Classic.SummarizingConversationManager(new() { PreserveRecentMessages = 2 });
        var reducing = manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow")).AsTask();
        Task<AgentResult>? invoking = null;
        Exception? reductionFailure = null;
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            invoking = agent.InvokeAsync("must not append");
            Assert.Same(invoking, await Task.WhenAny(invoking, secondOpen.Task, Task.Delay(8000)));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => invoking);
            Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult(true);
            try { await reducing; } catch (Exception error) { reductionFailure = error; }
            if (invoking is not null) try { await invoking; } catch (Exception) { }
        }
        Assert.Null(reductionFailure);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "must not append");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalLifecycleCannotCloseResourcesDuringAnAdmittedSummary(bool dispose)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new OwnedResource();
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = async () => { entered.TrySetResult(true); await release.Task; } };
        await using var agent = new Agent(new() { Model = model, Messages = ClassicConversationManagerTests.History(8), OwnedResources = [resource] });
        var manager = new Classic.SummarizingConversationManager(new() { PreserveRecentMessages = 2 });
        var reducing = manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow")).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            Assert.Throws<InvalidOperationException>(() => { if (dispose) _ = agent.DisposeAsync(); else _ = agent.ShutdownAsync(); });
            Assert.Equal(0, resource.Disposals);
        }
        finally { release.TrySetResult(true); await reducing; }
        await agent.DisposeAsync();
        Assert.Equal(1, resource.Disposals);
    }
    [Fact]
    public async Task RoutineManagementCannotEraseAnUnansweredDurableToolBatch()
    {
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 0 });
        var tool = new FunctionTool(new("approve", "approval", JsonSerializer.Deserialize<JsonElement>("{}")), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetRawText()));
        await using var agent = new Agent(new() { Model = new ApprovalModel(), ConversationManager = manager, Tools = [tool] });
        var result = await agent.InvokeAsync("task");
        Assert.Equal(StopReasons.Interrupt, result.StopReason);
        var pending = agent.InterruptState!;
        Assert.Contains(agent.Messages, message => message.TrackingId == pending.AssistantMessage.TrackingId);
        Assert.Equal(0, manager.RemovedMessageCount);
    }
    [Fact]
    public async Task OptionsManagerIsInitializedAndItsModelCadenceIsCaptured()
    {
        var manager = new Classic.SlidingWindowConversationManager(new() { PerTurn = 3 });
        await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager });
        await agent.InvokeAsync("task");
        Assert.Equal(1, manager.ModelCallCount);
        var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.ConversationManagerState });
        Assert.Equal(1, snapshot.Data["conversationManagerState"].GetProperty("model_call_count").GetInt64());
    }

    [Fact]
    public async Task ManualRestoreValidatesAllManagerFieldsBeforeInitializationOrOtherStateMutation()
    {
        var plugin = new CountingPlugin();
        var manager = new Classic.SlidingWindowConversationManager();
        await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager, Plugins = [plugin], Messages = [Message.User("configured")] });
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("replacement") }),
            ["conversationManagerState"] = JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":10,"model_call_count":-1}""")
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(snapshot));
        Assert.Equal(0, plugin.Calls);
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task FileSessionsRejectMalformedClassicStateBeforeCreatingTheirDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "strands-classic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync("session", new(1, [])
            { ConversationManagerState = JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":-1}""") }));
            Assert.False(Directory.Exists(path));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task DefaultChildrenReceiveFreshClassicBindingsAndIndependentCounters()
    {
        var manager = new Classic.SlidingWindowConversationManager(new() { PerTurn = 1 });
        await using var parent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager });
        await parent.InvokeAsync("parent");
        await using var child = parent.CreateChild();
        await child.InvokeAsync("child");
        Assert.Equal(1, manager.ModelCallCount);
        Assert.Equal(1, (await child.TakeSnapshotAsync(new() { Include = SnapshotFields.ConversationManagerState })).Data["conversationManagerState"].GetProperty("model_call_count").GetInt64());
    }

    [Fact]
    public async Task FreshFileSessionRestoresCadenceAndACompleteSummaryOnlyOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "strands-classic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var summary = Message.User("previous summary");
            var state = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["__name__"] = "SummarizingConversationManager", ["removed_message_count"] = 11, ["summary_message"] = summary });
            await new FileSessionStore(path).SaveAsync("session", new(1, [summary, Message.Assistant("retained")]) { ConversationManagerState = state });
            var manager = new Classic.SummarizingConversationManager();
            await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager, SessionStore = new FileSessionStore(path), SessionId = "session" });
            await agent.InitializeAsync();
            Assert.Equal(11, manager.RemovedMessageCount);
            Assert.Equal(2, agent.Messages.Count);
            Assert.Equal(summary.TrackingId, agent.Messages[0].TrackingId);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task ManagerOnlyChangesRemainDirtyAfterFailedSaveAndAreRetried()
    {
        var store = new SnapshotStore { Snapshot = new(1, []) { ConversationManagerState = JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":0,"model_call_count":0}""") } };
        var manager = new Classic.SlidingWindowConversationManager(new() { PerTurn = 3 });
        var modelError = new IOException("provider failed");
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = () => throw modelError };
        await using var agent = new Agent(new() { Model = model, ConversationManager = manager, SessionStore = store, SessionId = "session" });
        store.Failure = new IOException("save failed");
        Assert.Same(modelError, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null)));
        Assert.Equal(1, store.SaveCalls);
        store.Failure = null;
        var callbackError = new IOException("before invocation");
        using var callback = agent.Hooks.Register<BeforeInvocationEvent>((_, _) => throw callbackError);
        Assert.Same(callbackError, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null)));
        Assert.Equal(2, store.SaveCalls);
        Assert.Equal(1, store.Snapshot!.ConversationManagerState!.Value.GetProperty("model_call_count").GetInt64());
    }

    private sealed class CountingPlugin : IPlugin
    {
        internal int Calls;
        public string Name => "counting";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Calls++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class OwnedResource : IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class SnapshotStore : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; set; }
        internal IOException? Failure { get; set; }
        internal int SaveCalls;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { SaveCalls++; if (Failure is { } failure) throw failure; Snapshot = snapshot; return default; }
    }
    private sealed class ApprovalModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new("call", "approve", JsonSerializer.Deserialize<JsonElement>("{}")));
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }
}
