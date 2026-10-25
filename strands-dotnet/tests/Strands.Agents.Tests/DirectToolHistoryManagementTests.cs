using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests;

// Separate source-only body-review packet. Not part of the first12 scaffold selection.
public sealed class DirectToolHistoryManagementTests
{
    [Fact]
    public async Task PythonNoRecordStandaloneSlidingWindowRemovesOldPairAndPersistsState()
    {
        var seed = History(6);
        var manager = Sliding();
        var store = new Store();
        var model = new RejectModel();
        var bodies = 0;
        var tool = Plain("nested", (_, _, _) => { bodies++; return new ValueTask<string>("nested-done"); });
        await using var agent = new Agent(new() { Model = model, Messages = seed, Tools = [tool], ConversationManager = manager, SessionId = "management", SessionStore = store });
        var messageEvents = 0;
        agent.Hooks.Register<MessageAddedEvent>((_, _) => { messageEvents++; return default; });

        var result = await Within(agent.InvokeToolAsync(Call(), null, PythonNoRecord(), default).AsTask());

        Assert.Equal("nested-done", result.Content);
        Assert.Equal(1, bodies);
        Assert.Equal(seed.Skip(2), agent.Messages);
        for (var index = 0; index < 4; index++) Assert.Same(seed[index + 2], agent.Messages[index]);
        Assert.Equal(2, manager.RemovedMessageCount);
        Assert.Equal(0, messageEvents);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, model.Counts);
        var saved = Assert.IsType<SessionSnapshot>(store.Snapshot);
        Assert.Equal(agent.Messages.Select(message => message.Text), saved.Messages.Select(message => message.Text));
        Assert.Equal(2, saved.ConversationManagerState!.Value.GetProperty("removed_message_count").GetInt64());
    }

    [Fact]
    public async Task NestedPythonNoRecordSlidingWindowTrimsBeforeOuterToolCompletes()
    {
        var seed = History(4);
        var manager = Sliding();
        var model = new CycleModel(outerTool: true);
        var nestedBodies = 0;
        var nested = Plain("nested", (_, _, _) => { nestedBodies++; return new ValueTask<string>("nested-done"); });
        var nestedObserved = false;
        Agent? owner = null;
        var outer = Plain("outer", async (_, _, _) =>
        {
            var agent = Assert.IsType<Agent>(owner);
            var before = agent.Messages;
            Assert.Equal(6, before.Count);
            var result = await agent.InvokeToolAsync(Call(), null, PythonNoRecord(), default);
            Assert.Equal("nested-done", result.Content);
            Assert.Equal(before.Skip(2), agent.Messages);
            Assert.Equal(4, agent.Messages.Count);
            Assert.Equal(2, manager.RemovedMessageCount);
            Assert.Equal("outer-call", Assert.Single(agent.Messages[3].Content.OfType<ToolUseBlock>()).ToolUseId);
            Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), call => call.ToolUseId == "nested-call");
            nestedObserved = true;
            return "outer-done";
        });
        await using var current = new Agent(new() { Model = model, Messages = seed, Tools = [outer, nested], ConversationManager = manager });
        owner = current;

        var final = await Within(current.InvokeAsync("current-user"));

        Assert.Equal("complete", final.Message!.Text);
        Assert.True(nestedObserved);
        Assert.Equal(1, nestedBodies);
        Assert.Equal(2, model.Requests.Count);
        var next = model.Requests[1].Messages;
        var use = Assert.Single(next.SelectMany(message => message.Content).OfType<ToolUseBlock>());
        var resultRow = Assert.Single(next.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("outer-call", use.ToolUseId);
        Assert.Equal(use.ToolUseId, resultRow.ToolUseId);
        Assert.Equal("outer-done", resultRow.Content);
        Assert.Equal("2", next[0].Text);
    }

    [Fact]
    public async Task ForegroundPythonNoRecordSlidingWindowTrimsWhileBeforeInvocationIsHeld()
    {
        var entered = Signal();
        var release = Signal();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>(async (_, _) => { entered.TrySetResult(true); await release.Task; });
        var seed = History(6);
        var manager = Sliding();
        var model = new CycleModel();
        var tool = Plain("nested", (_, _, _) => new ValueTask<string>("nested-done"));
        await using var agent = new Agent(new() { Model = model, Messages = seed, Tools = [tool], ConversationManager = manager, Hooks = hooks });
        Task<AgentResult>? foreground = null;
        try
        {
            foreground = agent.InvokeAsync("new-user");
            await Within(entered.Task);
            var direct = await Within(agent.InvokeToolAsync(Call(), null, PythonNoRecord(), default).AsTask());
            Assert.Equal("nested-done", direct.Content);
            Assert.False(foreground.IsCompleted);
            Assert.Equal(seed.Skip(2), agent.Messages);
            Assert.Equal(2, manager.RemovedMessageCount);
            Assert.Empty(model.Requests);
            release.TrySetResult(true);
            Assert.Equal("complete", (await Within(foreground)).Message!.Text);
            var request = Assert.Single(model.Requests);
            Assert.Equal(new[] { "2", "3", "4", "5", "new-user" }, request.Messages.Select(message => message.Text));
        }
        finally
        {
            release.TrySetResult(true);
            if (foreground is not null) await Within(CaptureAsync(foreground));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PythonNoRecordNullAndSummarizingRoutineControls(bool summarizing)
    {
        Classic.ConversationManager manager = summarizing ? new Classic.SummarizingConversationManager() : new Classic.NullConversationManager();
        var auxiliary = new RejectModel();
        var model = new CycleModel(outerTool: true);
        var nested = Plain("nested", (_, _, _) => new ValueTask<string>("nested-done"));
        var observed = false;
        Agent? owner = null;
        var outer = Plain("outer", async (_, _, _) =>
        {
            var agent = Assert.IsType<Agent>(owner);
            var before = agent.Messages;
            Assert.Equal("nested-done", (await agent.InvokeToolAsync(Call(), null, PythonNoRecord(), default)).Content);
            Assert.Equal(before, agent.Messages);
            Assert.Equal(0, manager.RemovedMessageCount);
            observed = true;
            return "outer-done";
        });
        await using var current = new Agent(new() { Model = model, AuxModel = auxiliary, Messages = History(4), Tools = [outer, nested], ConversationManager = manager });
        owner = current;

        Assert.Equal("complete", (await Within(current.InvokeAsync("current-user"))).Message!.Text);

        Assert.True(observed);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(0, auxiliary.Calls);
        Assert.Equal(0, auxiliary.Counts);
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task PythonNoRecordContextManagerDoesNotAcquireClassicOrForceTokenCount()
    {
        var strategy = new ProbeStrategy();
        var context = new ContextManager(new() { StashEnabled = false, RetrievalToolEnabled = false, Strategies = [strategy] });
        var model = new RejectModel();
        var seed = History(6);
        var tool = Plain("nested", (_, _, _) => new ValueTask<string>("nested-done"));
        await using var agent = new Agent(new() { Model = model, Messages = seed, Tools = [tool], ContextManager = context });
        var messages = 0;
        agent.Hooks.Register<MessageAddedEvent>((_, _) => { messages++; return default; });

        var result = await Within(agent.InvokeToolAsync(Call(), null, PythonNoRecord(), default).AsTask());

        Assert.Equal("nested-done", result.Content);
        Assert.Equal(seed, agent.Messages);
        Assert.Equal(1, strategy.Initializations);
        Assert.Equal(0, strategy.Applications);
        Assert.Equal(0, messages);
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, model.Counts);
    }

    [Fact]
    public async Task PublicClassicManagementCannotBorrowThePrivateDirectReceipt()
    {
        var entered = Signal();
        var release = Signal();
        var manager = Sliding();
        var other = new Classic.NullConversationManager();
        var seed = History(6);
        var tool = Plain("nested", async (_, _, _) => { entered.TrySetResult(true); await release.Task; return "nested-done"; });
        await using var agent = new Agent(new() { Model = new RejectModel(), Messages = seed, Tools = [tool], ConversationManager = manager });
        Task<ToolResultBlock>? direct = null;
        try
        {
            direct = agent.InvokeToolAsync(Call(), null, PythonNoRecord(), default).AsTask();
            await Within(entered.Task);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(async () => await other.ApplyManagementAsync(agent));
            Assert.Equal(seed, agent.Messages);
            Assert.Equal(0, other.RemovedMessageCount);
            release.TrySetResult(true);
            Assert.Equal("nested-done", (await Within(direct)).Content);
            Assert.Equal(seed.Skip(2), agent.Messages);
            Assert.Equal(2, manager.RemovedMessageCount);
        }
        finally
        {
            release.TrySetResult(true);
            if (direct is not null) await Within(CaptureAsync(direct));
        }
    }

    private static DirectToolCallOptions PythonNoRecord() => new() { Profile = DirectToolCallProfile.Python, RecordHistory = false };
    private static Classic.SlidingWindowConversationManager Sliding() => new(new() { WindowSize = 4 });
    private static Message[] History(int count) => Enumerable.Range(0, count).Select(index => new Message(index % 2 == 0 ? Role.User : Role.Assistant, [new TextBlock(index.ToString(System.Globalization.CultureInfo.InvariantCulture))])).ToArray();
    private static ToolUseBlock Call() => new("nested-call", "nested", Json("{}"));
    private static FunctionTool Plain(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback)
        => new(new(name, "Management test", Json("{\"type\":\"object\",\"properties\":{}}")), callback);
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> CaptureAsync(Task task) { try { await task; return null; } catch (Exception error) { return error; } }
    private static async Task Within(Task task)
    {
        using var watchdog = new CancellationTokenSource();
        var timeout = Task.Delay(TimeSpan.FromSeconds(5), watchdog.Token);
        try { Assert.Same(task, await Task.WhenAny(task, timeout)); await task; }
        finally { watchdog.Cancel(); try { await timeout; } catch (OperationCanceledException error) when (error.CancellationToken == watchdog.Token) { } }
    }
    private static async Task<T> Within<T>(Task<T> task) { await Within((Task)task); return await task; }
    private sealed class Store : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Snapshot = snapshot; return default; }
    }
    private sealed class RejectModel : IModel, IModelTokenCounter
    {
        internal int Calls { get; private set; }
        internal int Counts { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("This direct path must not invoke a model."); }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Counts++; throw new InvalidOperationException("This direct path must not count model tokens."); }
    }
    private sealed class ProbeStrategy : ContextStrategy
    {
        public override string Name => "direct-management-probe";
        internal int Initializations { get; private set; }
        internal int Applications { get; private set; }
        public override ValueTask InitializeAsync(Agent agent, Stash? stash = null, CancellationToken cancellationToken = default) { Initializations++; return default; }
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default) { Applications++; return new(false); }
    }
    private sealed class CycleModel(bool outerTool = false) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var call = outerTool && Requests.Count == 1;
            yield return new ModelMessageStartEvent();
            if (call)
            {
                yield return new ModelContentBlockStartEvent(0, new("outer-call", "outer", Json("{}")));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}");
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "complete");
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(call ? StopReasons.ToolUse : StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
