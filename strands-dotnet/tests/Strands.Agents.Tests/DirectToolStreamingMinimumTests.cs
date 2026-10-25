using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class DirectToolStreamingMinimumTests
{
    [Fact]
    public async Task ProgressIsObservableBeforeCompletionWithoutModelOrHistoryEffects()
    {
        var release = Signal();
        var marker = new object();
        var state = new Dictionary<string, object?> { ["marker"] = marker };
        var model = new NoModel();
        var calls = 0;
        var disposals = 0;
        CancellationToken bodyToken = default;
        CancellationToken contextToken = default;
        var tool = Tool((_, context, token) => Body(context, token));
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using var cancellation = new CancellationTokenSource();
        await using var iterator = agent.StreamToolAsync(Call(), state, cancellation.Token).GetAsyncEnumerator();
        Task<bool>? pendingMove = null;
        try
        {
            pendingMove = iterator.MoveNextAsync().AsTask();
            Assert.True(await Within(pendingMove));
            Assert.Equal("working", Assert.IsType<ToolStreamEvent>(iterator.Current).Data);
            Assert.Equal(1, calls);
            Assert.False(release.Task.IsCompleted);
            Assert.Equal(bodyToken, contextToken);
            Assert.Equal(0, model.Calls);
            Assert.Empty(agent.Messages);
            state["marker"] = new object();
            release.TrySetResult(true);
            pendingMove = iterator.MoveNextAsync().AsTask();
            Assert.True(await Within(pendingMove));
            Assert.Equal("done", Assert.IsType<ToolStreamResultEvent>(iterator.Current).Result.Content);
            pendingMove = iterator.MoveNextAsync().AsTask();
            Assert.False(await Within(pendingMove));
            Assert.Equal(1, disposals);
        }
        finally
        {
            release.TrySetResult(true);
            if (pendingMove is not null) await pendingMove;
        }

        async IAsyncEnumerable<IToolStreamEvent> Body(ToolContext context, [EnumeratorCancellation] CancellationToken token)
        {
            calls++;
            bodyToken = token;
            contextToken = context.CancelSignal;
            try
            {
                yield return new ToolStreamEvent("working");
                await release.Task;
                token.ThrowIfCancellationRequested();
                Assert.Same(marker, context.InvocationState["marker"]);
                yield return new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, "done"));
            }
            finally { disposals++; }
        }
    }

    [Fact]
    public async Task NativeHooksNormalizeCorrelationAndPreserveDetachedRichJson()
    {
        var order = new List<string>();
        var hooks = new HookRegistry();
        var model = new NoModel();
        hooks.Register<BeforeToolCallEvent>((value, _) =>
        {
            order.Add("before");
            value.ToolUse = value.ToolUse with { ToolUseId = "forged-before", Input = Json("{\"value\":7}") };
            return default;
        });
        hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            order.Add("after");
            value.Result = value.Result with { ToolUseId = "forged-after" };
            return default;
        });
        var tool = Tool((input, context, _) =>
        {
            Assert.Equal(7, input.GetProperty("value").GetInt32());
            Assert.Equal("call", context.ToolUse.ToolUseId);
            using var document = JsonDocument.Parse("{\"value\":42}");
            return Items([new ToolStreamEvent(document), new ToolStreamResultEvent(new(context.ToolUse.ToolUseId,
                [new TextBlock("body"), new JsonBlock(document.RootElement)], structuredContent: document.RootElement) { Metadata = document.RootElement })]);
        });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call())));

        Assert.Equal(2, events.Count);
        Assert.Equal(42, Assert.IsType<JsonElement>(Assert.IsType<ToolStreamEvent>(events[0]).Data).GetProperty("value").GetInt32());
        var terminal = Assert.IsType<ToolStreamResultEvent>(events[1]).Result;
        Assert.Equal("call", terminal.ToolUseId);
        Assert.Equal("body", terminal.Content);
        Assert.Equal(42, terminal.StructuredContent!.Value.GetProperty("value").GetInt32());
        Assert.Equal(42, terminal.Metadata!.Value.GetProperty("value").GetInt32());
        Assert.Equal(42, Assert.IsType<JsonBlock>(terminal.ContentBlocks[1]).Json.GetProperty("value").GetInt32());
        Assert.Equal(["before", "after"], order);
        Assert.Empty(agent.Messages);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task PreCanceledOperationRetainsCallerTokenAndRunsNoToolCallbacks()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callbacks = 0;
        var hooks = new HookRegistry();
        var model = new NoModel();
        hooks.Register<BeforeToolCallEvent>((_, _) => { callbacks++; return default; });
        hooks.Register<AfterToolCallEvent>((_, _) => { callbacks++; return default; });
        var tool = Tool((_, _, _) => { callbacks++; return Items([]); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(agent.StreamToolAsync(Call(), cancellationToken: cancellation.Token)));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, model.Calls);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task EarlyCloseJoinsBlockedIteratorDisposalBeforeReleasingAdmission()
    {
        var stream = new BlockingDisposalStream();
        var model = new NoModel();
        var tool = Tool((_, context, token) =>
        {
            Assert.Equal(token, context.CancelSignal);
            return stream;
        });
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        IAsyncEnumerator<IToolStreamEvent>? iterator = null;
        Task? close = null;
        try
        {
            iterator = agent.StreamToolAsync(Call()).GetAsyncEnumerator();
            Assert.True(await Within(iterator.MoveNextAsync().AsTask()));
            Assert.IsType<ToolStreamEvent>(iterator.Current);
            close = iterator.DisposeAsync().AsTask();
            await Within(stream.DisposeEntered.Task);
            Assert.False(close.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(agent.ShutdownAsync);
            Assert.Equal(0, model.Calls);
            stream.ReleaseDispose.TrySetResult(true);
            await Within(close);
            Assert.Equal(1, stream.Disposals);
            Assert.True(stream.OperationToken.IsCancellationRequested);
            await Within(agent.ShutdownAsync());
            Assert.Empty(agent.Messages);
        }
        finally
        {
            stream.ReleaseMoveNext.TrySetResult(true);
            stream.ReleaseDispose.TrySetResult(true);
            if (close is not null) await Within(close);
            else if (iterator is not null) await Within(iterator.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task BodyFailureAndIndependentIteratorCleanupRemainOnOneErrorTerminal()
    {
        var primary = new ApplicationException("direct stream body");
        var cleanup = new IOException("direct stream dispose");
        var stream = new FailingStream(primary, cleanup);
        var model = new NoModel();
        var tool = Tool((_, _, _) => stream);
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call())));

        var terminal = Assert.IsType<ToolStreamResultEvent>(Assert.Single(events)).Result;
        Assert.Equal(ToolResultStatus.Error, terminal.Status);
        Assert.Equal("call", terminal.ToolUseId);
        Assert.Same(primary, terminal.Error);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, stream.Disposals);
        Assert.Equal(0, model.Calls);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task ExistingInvokeToolConsumesProgressAndKeepsNativeNoHistoryBehavior()
    {
        var hooks = new HookRegistry();
        var order = new List<string>();
        var model = new NoModel();
        hooks.Register<BeforeToolCallEvent>((_, _) => { order.Add("before"); return default; });
        hooks.Register<AfterToolCallEvent>((_, _) => { order.Add("after"); return default; });
        var tool = Tool((_, context, _) => Items([new ToolStreamEvent("working"), new ToolStreamResultEvent(new(context.ToolUse.ToolUseId, "done"))]));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var result = await Within(agent.InvokeToolAsync(Call()).AsTask());

        Assert.Equal("call", result.ToolUseId);
        Assert.Equal("done", result.Content);
        Assert.Equal(["before", "after"], order);
        Assert.Empty(agent.Messages);
        Assert.Equal(0, model.Calls);
    }

    private static ToolUseBlock Call() => new("call", "streamer", Json("{}"));
    private static StreamingFunctionTool Tool(Func<JsonElement, ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> callback)
        => new(new("streamer", "Reports direct progress", Json("{\"type\":\"object\"}")), callback);
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<List<IToolStreamEvent>> CollectAsync(IAsyncEnumerable<IToolStreamEvent> source)
    {
        var events = new List<IToolStreamEvent>();
        await foreach (var item in source) events.Add(item);
        return events;
    }
    private static async Task Within(Task task)
    {
        using var watchdog = new CancellationTokenSource();
        var timeout = Task.Delay(TimeSpan.FromSeconds(5), watchdog.Token);
        try
        {
            Assert.Same(task, await Task.WhenAny(task, timeout));
            await task;
        }
        finally
        {
            watchdog.Cancel();
            try { await timeout; }
            catch (OperationCanceledException error) when (error.CancellationToken == watchdog.Token) { }
        }
    }
    private static async Task<T> Within<T>(Task<T> task)
    {
        await Within((Task)task);
        return await task;
    }
    private static async IAsyncEnumerable<IToolStreamEvent> Items(IEnumerable<IToolStreamEvent> events)
    {
        foreach (var item in events) yield return item;
        await Task.CompletedTask;
    }

    private sealed class NoModel : IModel
    {
        public int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Direct tools must not invoke the model."); }
    }

    private sealed class BlockingDisposalStream : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        private bool _started;
        public TaskCompletionSource<bool> ReleaseMoveNext { get; } = Signal();
        public TaskCompletionSource<bool> DisposeEntered { get; } = Signal();
        public TaskCompletionSource<bool> ReleaseDispose { get; } = Signal();
        public CancellationToken OperationToken { get; private set; }
        public int Disposals { get; private set; }
        public IToolStreamEvent Current => new ToolStreamEvent("working");
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { OperationToken = cancellationToken; return this; }
        public async ValueTask<bool> MoveNextAsync()
        {
            if (!_started) { _started = true; return true; }
            var canceled = Signal();
            using var registration = OperationToken.Register(() => canceled.TrySetCanceled(OperationToken));
            await await Task.WhenAny(ReleaseMoveNext.Task, canceled.Task);
            return false;
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            DisposeEntered.TrySetResult(true);
            await ReleaseDispose.Task;
        }
    }

    private sealed class FailingStream(Exception primary, Exception cleanup) : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        public int Disposals { get; private set; }
        public IToolStreamEvent Current => throw new InvalidOperationException("No current item after body failure.");
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => throw primary;
        public ValueTask DisposeAsync() { Disposals++; throw cleanup; }
    }
}
