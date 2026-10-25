using System.Text.Json;
using Strands.Agents.Conversation;

namespace Strands.Agents.Tests;

// Source-only proposed API tests. The new overload/options scaffold is not applied or compiled.
public sealed class DirectToolHistoryMinimumTests
{
    [Fact]
    public async Task OldInvokeAndDefaultLiteralCallsKeepNoHistory()
    {
        var bodies = 0;
        var manager = new ProbeManager();
        var model = new NoModel();
        var tool = Plain((_, _, _) => { bodies++; return new ValueTask<string>("done"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], ConversationManager = manager });

        Assert.Equal("done", (await agent.InvokeToolAsync(Call())).Content);
        Assert.Equal("done", (await agent.InvokeToolAsync(Call(), null)).Content);
        Assert.Equal("done", (await agent.InvokeToolAsync(Call(), default, default)).Content);
        Func<ToolUseBlock, IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<ToolResultBlock>> old = agent.InvokeToolAsync;
        Assert.Equal("done", (await old(Call(), null, default)).Content);

        Assert.Equal(4, bodies);
        Assert.Equal(0, manager.Calls);
        Assert.Empty(agent.Messages);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task OldStreamKeepsProgressAndNoHistory()
    {
        var manager = new ProbeManager();
        var model = new NoModel();
        var tool = Streaming((_, context, _) => Items([new ToolStreamEvent("working"), Terminal(context, "done")]));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], ConversationManager = manager });
        Func<ToolUseBlock, IReadOnlyDictionary<string, object?>?, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> old = agent.StreamToolAsync;

        var events = await Within(CollectAsync(old(Call(), default, default)));

        Assert.Equal(2, events.Count);
        Assert.Equal("working", Assert.IsType<ToolStreamEvent>(events[0]).Data);
        Assert.Equal("done", Assert.IsType<ToolStreamResultEvent>(events[1]).Result.Content);
        Assert.Equal(0, manager.Calls);
        Assert.Empty(agent.Messages);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task TypeScriptInvokeRecords3RowsFullInputAndNativeState()
    {
        var marker = new object();
        Action unencodable = () => { };
        var callerState = new Dictionary<string, object?> { ["marker"] = marker, ["callback"] = unencodable };
        ToolContext? bodyContext = null;
        var visiblePrefixes = new List<int>();
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) =>
        {
            visiblePrefixes.Add(value.Agent.Messages.Count);
            Assert.Same(Assert.IsType<ToolContext>(bodyContext).InvocationState, value.InvocationState);
            Assert.Same(marker, value.InvocationState["marker"]);
            Assert.Same(unencodable, value.InvocationState["callback"]);
            return default;
        });
        var model = new NoModel();
        var tool = Plain((input, context, _) =>
        {
            bodyContext = context;
            Assert.Equal(7, input.GetProperty("extra").GetInt32());
            Assert.NotSame(callerState, context.InvocationState);
            return new ValueTask<string>("done");
        });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var result = await Within(agent.InvokeToolAsync(Call(input: Json("{\"allowed\":\"yes\",\"extra\":7}")), callerState, TypeScript(), default).AsTask());

        AssertTypeScriptRows(agent.Messages, result.Content, ToolResultStatus.Success);
        Assert.Equal(7, Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[0].Content)).Input.GetProperty("extra").GetInt32());
        Assert.Equal([1, 2, 3], visiblePrefixes);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task PythonInvokeRecords4RowsFiltersOnlyHistoryAndKeepsOverrideBlocks()
    {
        var bodySawExtra = false;
        var manager = new ProbeManager();
        var model = new NoModel();
        var input = Json("{\"allowed\":\"yes\",\"extra\":7}");
        var tool = Plain((value, _, _) => { bodySawExtra = value.GetProperty("extra").GetInt32() == 7; return new ValueTask<string>("done"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], ConversationManager = manager });

        var result = await Within(agent.InvokeToolAsync(Call(input: input), null, Python("approve"), default).AsTask());

        AssertPythonRows(agent.Messages, result.Content);
        Assert.Equal("approve\n", Assert.IsType<TextBlock>(agent.Messages[0].Content[0]).Text);
        Assert.Equal(2, agent.Messages[0].Content.Count);
        var filtered = Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[1].Content)).Input;
        Assert.Equal("yes", filtered.GetProperty("allowed").GetString());
        Assert.False(filtered.TryGetProperty("extra", out _));
        var description = Assert.IsType<TextBlock>(agent.Messages[0].Content[1]).Text;
        Assert.StartsWith("agent.tool.streamer direct tool call.\nInput parameters: ", description);
        Assert.EndsWith("\n", description);
        var jsonOffset = description.IndexOf("Input parameters: ", StringComparison.Ordinal) + "Input parameters: ".Length;
        var displayed = Json(description.Substring(jsonOffset).TrimEnd('\n'));
        Assert.Equal("allowed", Assert.Single(displayed.EnumerateObject()).Name);
        Assert.True(bodySawExtra);
        Assert.Equal(7, input.GetProperty("extra").GetInt32());
        Assert.Equal(1, manager.Calls);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task TypeScriptStreamRecordsOnlyAfterCompletedAcceptedAttempt()
    {
        var release = Signal();
        var messagesAdded = 0;
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((_, _) => { messagesAdded++; return default; });
        var model = new NoModel();
        var tool = Streaming((_, context, _) => Body(context));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });
        var iterator = agent.StreamToolAsync(Call(), null, TypeScript(), default).GetAsyncEnumerator();
        Task<bool>? move = null;
        try
        {
            move = iterator.MoveNextAsync().AsTask();
            Assert.True(await Within(move));
            Assert.IsType<ToolStreamEvent>(iterator.Current);
            Assert.Empty(agent.Messages);
            Assert.Equal(0, messagesAdded);
            release.TrySetResult(true);
            move = iterator.MoveNextAsync().AsTask();
            Assert.True(await Within(move));
            var terminal = Assert.IsType<ToolStreamResultEvent>(iterator.Current).Result;
            AssertTypeScriptRows(agent.Messages, terminal.Content, ToolResultStatus.Success);
            Assert.Equal(3, messagesAdded);
            move = iterator.MoveNextAsync().AsTask();
            Assert.False(await Within(move));
            Assert.Equal(0, model.Calls);
        }
        finally
        {
            release.TrySetResult(true);
            if (move is not null) await Within(CaptureAsync(move));
            await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }

        async IAsyncEnumerable<IToolStreamEvent> Body(ToolContext context)
        {
            yield return new ToolStreamEvent("working");
            await release.Task;
            yield return Terminal(context, "done");
        }
    }

    [Fact]
    public async Task PythonStreamRecords4RowsAndAppliesManagement()
    {
        var manager = new ProbeManager();
        var model = new NoModel();
        var tool = Streaming((_, context, _) => Items([new ToolStreamEvent("working"), Terminal(context, "done")]));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], ConversationManager = manager });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call(), null, Python(), default)));

        Assert.Equal(2, events.Count);
        Assert.IsType<ToolStreamEvent>(events[0]);
        var terminal = Assert.IsType<ToolStreamResultEvent>(events[1]).Result;
        AssertPythonRows(agent.Messages, terminal.Content);
        Assert.Single(agent.Messages[0].Content);
        Assert.Equal(1, manager.Calls);
        Assert.Equal(4, manager.MessagesAtApply);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task PythonNoRecordStillPhysicallyJoinsConfiguredManagement()
    {
        var manager = new ProbeManager(hold: true);
        var bodies = 0;
        var added = 0;
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((_, _) => { added++; return default; });
        var model = new NoModel();
        var tool = Plain((_, _, _) => { bodies++; return new ValueTask<string>("done"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], ConversationManager = manager, Hooks = hooks });
        Task<ToolResultBlock>? call = null;
        try
        {
            call = agent.InvokeToolAsync(Call(), null, Python() with { RecordHistory = false }, default).AsTask();
            await Within(manager.Entered.Task);
            Assert.False(call.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(agent.ShutdownAsync);
            Assert.Equal(1, bodies);
            Assert.Empty(agent.Messages);
            Assert.Equal(0, added);
            manager.Release.TrySetResult(true);
            Assert.Equal("done", (await Within(call)).Content);
            Assert.Equal(1, manager.Calls);
            await Within(agent.ShutdownAsync());
            Assert.Equal(0, model.Calls);
        }
        finally
        {
            manager.Release.TrySetResult(true);
            if (call is not null) await Within(CaptureAsync(call));
        }
    }

    [Fact]
    public async Task TypeScriptHookFailureRetainsOnlyPublishedPrefix()
    {
        var primary = new ApplicationException("first MessageAdded");
        var cleanup = new IOException("accepted prefix save");
        var store = new RecordingStore { SaveError = cleanup };
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) =>
        {
            Assert.Single(value.Agent.Messages);
            throw primary;
        });
        var model = new NoModel();
        var tool = Plain((_, _, _) => new ValueTask<string>("done"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks, SessionStore = store, SessionId = "direct-history" });

        var error = await Within(CaptureAsync(agent.InvokeToolAsync(Call(), null, TypeScript(), default).AsTask()));

        Assert.Same(primary, error);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.IsType<ToolUseBlock>(Assert.Single(Assert.Single(agent.Messages).Content));
        Assert.Single(Assert.IsType<SessionSnapshot>(store.Snapshot).Messages);
        Assert.Equal(1, store.Writes);
        Assert.False(store.WriteToken.CanBeCanceled);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task PythonHookFailureRetainsUserThenToolUsePrefix()
    {
        var primary = new ApplicationException("second MessageAdded");
        var callbacks = 0;
        var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) =>
        {
            Assert.Equal(++callbacks, value.Agent.Messages.Count);
            if (callbacks == 2) throw primary;
            return default;
        });
        var manager = new ProbeManager();
        var store = new RecordingStore();
        var model = new NoModel();
        var tool = Plain((_, _, _) => new ValueTask<string>("done"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks, ConversationManager = manager, SessionStore = store, SessionId = "direct-history" });

        var error = await Within(CaptureAsync(agent.InvokeToolAsync(Call(), null, Python(), default).AsTask()));

        Assert.Same(primary, error);
        Assert.Equal(2, agent.Messages.Count);
        Assert.Equal(Role.User, agent.Messages[0].Role);
        Assert.Equal(Role.Assistant, agent.Messages[1].Role);
        Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[1].Content));
        Assert.Equal(2, Assert.IsType<SessionSnapshot>(store.Snapshot).Messages.Count);
        Assert.Equal(0, manager.Calls);
        Assert.Equal(2, callbacks);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task ExplicitErrorTerminalIsRecordedOnce()
    {
        var tool = new RichErrorTool();
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });

        var result = await Within(agent.InvokeToolAsync(Call(), null, TypeScript(), default).AsTask());

        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Null(result.Error);
        AssertTypeScriptRows(agent.Messages, "declined", ToolResultStatus.Error);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task EarlyCloseBeforeAcceptanceRecordsNothingAndJoins()
    {
        var stream = new BlockingStream();
        var manager = new ProbeManager();
        var model = new NoModel();
        var tool = Streaming((_, _, _) => stream);
        await using var agent = new Agent(new() { Model = model, Tools = [tool], ConversationManager = manager });
        var iterator = agent.StreamToolAsync(Call(), null, Python(), default).GetAsyncEnumerator();
        Task<bool>? first = null;
        Task<Exception?>? close = null;
        try
        {
            first = iterator.MoveNextAsync().AsTask();
            Assert.True(await Within(first));
            Assert.IsType<ToolStreamEvent>(iterator.Current);
            close = CaptureAsync(iterator.DisposeAsync().AsTask());
            await Within(stream.DisposeEntered.Task);
            Assert.False(close.IsCompleted);
            Assert.True(stream.OperationToken.IsCancellationRequested);
            Assert.Empty(agent.Messages);
            Assert.Equal(0, manager.Calls);
            stream.ReleaseDispose.TrySetResult(true);
            Assert.Null(await Within(close));
            Assert.Equal(1, stream.Disposals);
            Assert.Equal(0, model.Calls);
        }
        finally
        {
            stream.ReleaseMoveNext.TrySetResult(true);
            stream.ReleaseDispose.TrySetResult(true);
            if (first is not null) await Within(CaptureAsync(first));
            if (close is not null) await Within(close);
            else await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task RetryProgressRecordsOnlyTheFinalAcceptedAttempt()
    {
        var bodies = 0;
        var after = 0;
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((value, _) => { value.Retry = ++after == 1; return default; });
        var model = new NoModel();
        var tool = Streaming((_, context, _) =>
        {
            var attempt = ++bodies;
            return Items([new ToolStreamEvent(attempt), Terminal(context, "attempt-" + attempt)]);
        });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call(), null, TypeScript(), default)));

        Assert.Equal(3, events.Count);
        Assert.Equal(1, Assert.IsType<int>(Assert.IsType<ToolStreamEvent>(events[0]).Data));
        Assert.Equal(2, Assert.IsType<int>(Assert.IsType<ToolStreamEvent>(events[1]).Data));
        Assert.Equal("attempt-2", Assert.IsType<ToolStreamResultEvent>(events[2]).Result.Content);
        AssertTypeScriptRows(agent.Messages, "attempt-2", ToolResultStatus.Success);
        Assert.Equal(2, bodies);
        Assert.Equal(2, after);
        Assert.Equal(0, model.Calls);
    }

    private static DirectToolCallOptions TypeScript() => new() { Profile = DirectToolCallProfile.TypeScript };
    private static DirectToolCallOptions Python(string? prefix = null) => new() { Profile = DirectToolCallProfile.Python, UserMessageOverride = prefix };
    private static ToolUseBlock Call(JsonElement? input = null) => new("call", "streamer", input ?? Json("{}"));
    private static ToolSpec Spec() => new("streamer", "Direct history test", Json("{\"type\":\"object\",\"properties\":{\"allowed\":{\"type\":\"string\"}}}"));
    private static FunctionTool Plain(Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback) => new(Spec(), callback);
    private static StreamingFunctionTool Streaming(Func<JsonElement, ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> callback) => new(Spec(), callback);
    private static ToolStreamResultEvent Terminal(ToolContext context, string text) => new(new(context.ToolUse.ToolUseId, text));
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void AssertTypeScriptRows(IReadOnlyList<Message> messages, string output, ToolResultStatus status)
    {
        Assert.Equal([Role.Assistant, Role.User, Role.Assistant], messages.Select(value => value.Role).ToArray());
        var use = Assert.IsType<ToolUseBlock>(Assert.Single(messages[0].Content));
        Assert.Equal("call", use.ToolUseId);
        Assert.Equal("streamer", use.Name);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(messages[1].Content));
        Assert.Equal("call", result.ToolUseId);
        Assert.Equal(output, result.Content);
        Assert.Equal(status, result.Status);
        Assert.Equal("agent.tool.streamer was called.", Assert.IsType<TextBlock>(Assert.Single(messages[2].Content)).Text);
    }
    private static void AssertPythonRows(IReadOnlyList<Message> messages, string output)
    {
        Assert.Equal(4, messages.Count);
        Assert.Equal(Role.User, messages[0].Role);
        AssertTypeScriptRows(messages.Skip(1).ToArray(), output, ToolResultStatus.Success);
    }
    private static async Task<List<IToolStreamEvent>> CollectAsync(IAsyncEnumerable<IToolStreamEvent> source)
    {
        var events = new List<IToolStreamEvent>();
        await foreach (var item in source) events.Add(item);
        return events;
    }
    private static async Task<Exception?> CaptureAsync(Task task) { try { await task; return null; } catch (Exception error) { return error; } }
    private static async Task Within(Task task)
    {
        using var watchdog = new CancellationTokenSource();
        var timeout = Task.Delay(TimeSpan.FromSeconds(5), watchdog.Token);
        try { Assert.Same(task, await Task.WhenAny(task, timeout)); await task; }
        finally
        {
            watchdog.Cancel();
            try { await timeout; }
            catch (OperationCanceledException error) when (error.CancellationToken == watchdog.Token) { }
        }
    }
    private static async Task<T> Within<T>(Task<T> task) { await Within((Task)task); return await task; }
    private static async IAsyncEnumerable<IToolStreamEvent> Items(IEnumerable<IToolStreamEvent> events)
    {
        foreach (var item in events) yield return item;
        await Task.CompletedTask;
    }

    private sealed class NoModel : IModel
    {
        public int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Direct history tests must not call a model."); }
    }
    private sealed class ProbeManager : ConversationManager
    {
        internal int Calls { get; private set; }
        internal int MessagesAtApply { get; private set; }
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        internal ProbeManager(bool hold = false) { if (!hold) Release.TrySetResult(true); }
        protected override async ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken)
        {
            Calls++;
            MessagesAtApply = agent.Messages.Count;
            Entered.TrySetResult(true);
            await Release.Task;
        }
        protected override ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken)
            => throw new InvalidOperationException("No reactive/model reduction is expected.");
    }
    private sealed class RichErrorTool : IRichTool
    {
        public ToolSpec Spec { get; } = DirectToolHistoryMinimumTests.Spec();
        internal int Calls { get; private set; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The rich path is required.");
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { Calls++; return new(new ToolResultBlock(context.ToolUse.ToolUseId, "declined", ToolResultStatus.Error)); }
    }
    private sealed class RecordingStore : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; private set; }
        internal int Writes { get; private set; }
        internal Exception? SaveError { get; init; }
        internal CancellationToken WriteToken { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Writes++;
            WriteToken = cancellationToken;
            Snapshot = snapshot with { Messages = snapshot.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray() };
            if (SaveError is not null) throw SaveError;
            return default;
        }
    }
    private sealed class BlockingStream : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        private bool _first;
        internal CancellationToken OperationToken { get; private set; }
        internal int Disposals { get; private set; }
        internal TaskCompletionSource<bool> ReleaseMoveNext { get; } = Signal();
        internal TaskCompletionSource<bool> DisposeEntered { get; } = Signal();
        internal TaskCompletionSource<bool> ReleaseDispose { get; } = Signal();
        public IToolStreamEvent Current => new ToolStreamEvent("working");
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) { OperationToken = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            if (!_first) { _first = true; return true; }
            var canceled = Signal();
            using var registration = OperationToken.Register(() => canceled.TrySetCanceled(OperationToken));
            await await Task.WhenAny(ReleaseMoveNext.Task, canceled.Task);
            return false;
        }
        public async ValueTask DisposeAsync() { Disposals++; DisposeEntered.TrySetResult(true); await ReleaseDispose.Task; }
    }
}
