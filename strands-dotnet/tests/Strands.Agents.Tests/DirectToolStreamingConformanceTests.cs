using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class DirectToolStreamingConformanceTests
{
    [Fact]
    public async Task CallerCancellationWhileReaderWaitsJoinsHeldIteratorDisposal()
    {
        using var caller = new CancellationTokenSource();
        var stream = new ControlledStream("call", progressFirst: false, holdDispose: true);
        var model = new NoModel();
        await using var agent = AgentFor(stream, model);
        var iterator = agent.StreamToolAsync(Call(), cancellationToken: caller.Token).GetAsyncEnumerator();
        Task<Exception?>? move = null;
        try
        {
            move = CaptureAsync(iterator.MoveNextAsync().AsTask());
            await Within(stream.Waiting.Task);
            caller.Cancel();
            await Within(stream.DisposeEntered.Task);
            Assert.False(move.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(agent.ShutdownAsync);
            stream.ReleaseDispose.TrySetResult(true);
            var error = Assert.IsAssignableFrom<OperationCanceledException>(await Within(move));
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.True(stream.OperationToken.IsCancellationRequested);
            Assert.Equal(1, stream.Disposals);
            await Within(agent.ShutdownAsync());
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            stream.ReleaseAll();
            if (move is not null) await Within(move);
            await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task CallerCancellationRetainsTheOwnedProducerFailureAndIndependentCleanup()
    {
        using var caller = new CancellationTokenSource();
        var marker = new object();
        var cleanup = new IOException("independent producer cleanup");
        OperationCanceledException? producerFailure = null;
        var stream = new ControlledStream("call", progressFirst: false);
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((_, _) =>
        {
            caller.Cancel();
            producerFailure = new OperationCanceledException("owned producer cancellation", stream.OperationToken);
            producerFailure.Data["marker"] = marker;
            producerFailure.Data["Strands.CleanupErrors"] = new List<Exception> { cleanup };
            throw producerFailure;
        });
        var model = new NoModel();
        await using var agent = AgentFor(stream, model, hooks);
        var iterator = agent.StreamToolAsync(Call(), cancellationToken: caller.Token).GetAsyncEnumerator();
        Task<Exception?>? move = null;
        try
        {
            move = CaptureAsync(iterator.MoveNextAsync().AsTask());
            await Within(stream.Waiting.Task);
            stream.ReleaseMoveNext.TrySetResult(true);
            var error = Assert.IsAssignableFrom<OperationCanceledException>(await Within(move));
            Assert.Equal(caller.Token, error.CancellationToken);
            var original = Assert.IsType<OperationCanceledException>(producerFailure);
            Assert.Equal(stream.OperationToken, original.CancellationToken);
            Assert.Same(marker, original.Data["marker"]);
            Assert.Same(cleanup, Assert.Single(CleanupErrors(original)));
            if (ReferenceEquals(original, error.InnerException))
            {
                Assert.Same(marker, error.Data["marker"]);
                Assert.Same(original.Data["Strands.CleanupErrors"], error.Data["Strands.CleanupErrors"]);
                Assert.Same(cleanup, Assert.Single(CleanupErrors(error)));
            }
            else Assert.Same(original, Assert.Single(CleanupErrors(error)));
            Assert.True(stream.OperationToken.IsCancellationRequested);
            Assert.Equal(1, stream.Disposals);
            await Within(agent.ShutdownAsync());
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            stream.ReleaseAll();
            if (move is not null) await Within(move);
            await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task EnumeratorOnlyPreCancellationRunsNoHooksOrBody()
    {
        using var enumerator = new CancellationTokenSource();
        enumerator.Cancel();
        var callbacks = 0;
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((_, _) => { callbacks++; return default; });
        hooks.Register<AfterToolCallEvent>((_, _) => { callbacks++; return default; });
        var model = new NoModel();
        var tool = Tool((_, context, _) => { callbacks++; return Items([Result(context, "unused")]); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Within(CollectAsync(agent.StreamToolAsync(Call()), enumerator.Token)));

        Assert.Equal(enumerator.Token, error.CancellationToken);
        Assert.Equal(0, callbacks);
        AssertNoModelOrHistory(agent, model);
    }

    [Fact]
    public Task DistinctMethodAndEnumeratorTokensBothObserveMethodCancellation()
        => AssertMixedCancellationAsync(cancelMethod: true);

    [Fact]
    public Task DistinctMethodAndEnumeratorTokensBothObserveEnumeratorCancellation()
        => AssertMixedCancellationAsync(cancelMethod: false);

    [Fact]
    public async Task OwnedEarlyCloseRetainsExactIndependentIteratorCleanupAndData()
    {
        var marker = new object();
        var cleanup = new IOException("independent direct iterator cleanup");
        cleanup.Data["marker"] = marker;
        var stream = new ControlledStream("call", disposeFailure: cleanup);
        var model = new NoModel();
        await using var agent = AgentFor(stream, model);
        var iterator = agent.StreamToolAsync(Call()).GetAsyncEnumerator();
        Task<Exception?>? close = null;
        try
        {
            Assert.True(await Within(iterator.MoveNextAsync().AsTask()));
            close = CaptureAsync(iterator.DisposeAsync().AsTask());
            var error = Assert.IsAssignableFrom<OperationCanceledException>(await Within(close));
            Assert.Same(cleanup, Assert.Single(CleanupErrors(error)));
            Assert.Same(marker, cleanup.Data["marker"]);
            Assert.True(stream.OperationToken.IsCancellationRequested);
            Assert.Equal(1, stream.Disposals);
            await Within(agent.ShutdownAsync());
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            stream.ReleaseAll();
            if (close is not null) await Within(close);
            else await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task OwnedEarlyCloseRetainsExactAfterHookFailureAsCancellationCleanup()
    {
        var marker = new object();
        var afterFailure = new ApplicationException("independent after hook");
        afterFailure.Data["marker"] = marker;
        var afterCalls = 0;
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((value, token) =>
        {
            afterCalls++;
            Assert.False(token.CanBeCanceled);
            Assert.IsAssignableFrom<OperationCanceledException>(value.Result.Error);
            throw afterFailure;
        });
        var stream = new ControlledStream("call");
        var model = new NoModel();
        await using var agent = AgentFor(stream, model, hooks);
        var iterator = agent.StreamToolAsync(Call()).GetAsyncEnumerator();
        Task<Exception?>? close = null;
        try
        {
            Assert.True(await Within(iterator.MoveNextAsync().AsTask()));
            close = CaptureAsync(iterator.DisposeAsync().AsTask());
            var error = Assert.IsAssignableFrom<OperationCanceledException>(await Within(close));
            Assert.Same(afterFailure, Assert.Single(CleanupErrors(error)));
            Assert.Same(marker, afterFailure.Data["marker"]);
            Assert.Equal(1, afterCalls);
            Assert.Equal(1, stream.Disposals);
            await Within(agent.ShutdownAsync());
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            stream.ReleaseAll();
            if (close is not null) await Within(close);
            else await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackDoesNotReleaseCloseBeforeProducerJoins()
    {
        var callbackFailure = new ApplicationException("tool cancellation callback");
        var stream = new ControlledStream("call", holdDispose: true, cancellationFailure: callbackFailure);
        var model = new NoModel();
        await using var agent = AgentFor(stream, model);
        var iterator = agent.StreamToolAsync(Call()).GetAsyncEnumerator();
        Task<Exception?>? close = null;
        try
        {
            Assert.True(await Within(iterator.MoveNextAsync().AsTask()));
            close = CaptureAsync(iterator.DisposeAsync().AsTask());
            await Within(stream.DisposeEntered.Task);
            Assert.False(close.IsCompleted);
            Assert.Equal(1, stream.CancellationCallbacks);
            Assert.True(stream.OperationToken.IsCancellationRequested);
            await Assert.ThrowsAsync<InvalidOperationException>(agent.ShutdownAsync);
            stream.ReleaseDispose.TrySetResult(true);
            var aggregate = Assert.IsType<AggregateException>(await Within(close));
            Assert.Same(callbackFailure, Assert.Single(aggregate.InnerExceptions));
            Assert.Equal(1, stream.Disposals);
            await Within(agent.ShutdownAsync());
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            stream.ReleaseAll();
            if (close is not null) await Within(close);
            else await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task ConcurrentDirectStreamsKeepCancellationResultsAndAdmissionIndependent()
    {
        var first = new ControlledStream("first");
        var second = new ControlledStream("second");
        var model = new NoModel();
        var tool = Tool((_, context, _) => context.ToolUse.ToolUseId == "first" ? first : second);
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        var a = agent.StreamToolAsync(Call("first")).GetAsyncEnumerator();
        var b = agent.StreamToolAsync(Call("second")).GetAsyncEnumerator();
        Task<Exception?>? closeA = null;
        Task<bool>? moveB = null;
        try
        {
            Assert.True(await Within(a.MoveNextAsync().AsTask()));
            Assert.True(await Within(b.MoveNextAsync().AsTask()));
            Assert.NotEqual(first.OperationToken, second.OperationToken);
            closeA = CaptureAsync(a.DisposeAsync().AsTask());
            Assert.Null(await Within(closeA));
            Assert.True(first.OperationToken.IsCancellationRequested);
            Assert.False(second.OperationToken.IsCancellationRequested);
            await Assert.ThrowsAsync<InvalidOperationException>(agent.ShutdownAsync);
            moveB = b.MoveNextAsync().AsTask();
            await Within(second.Waiting.Task);
            second.ReleaseMoveNext.TrySetResult(true);
            Assert.True(await Within(moveB));
            var terminal = Assert.IsType<ToolStreamResultEvent>(b.Current).Result;
            Assert.Equal("second", terminal.ToolUseId);
            Assert.Equal(ToolResultStatus.Success, terminal.Status);
            moveB = b.MoveNextAsync().AsTask();
            Assert.False(await Within(moveB));
            Assert.Equal(1, first.Disposals);
            Assert.Equal(1, second.Disposals);
            await Within(agent.ShutdownAsync());
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            first.ReleaseAll();
            second.ReleaseAll();
            if (closeA is not null) await Within(closeA);
            else await Within(CaptureAsync(a.DisposeAsync().AsTask()));
            if (moveB is not null) await Within(CaptureAsync(moveB));
            await Within(CaptureAsync(b.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task HookRequestedRetryStreamsEachAttemptButPublishesOnlyAcceptedTerminal()
    {
        var bodies = 0;
        var beforeCalls = 0;
        var afterCalls = 0;
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((_, _) => { beforeCalls++; return default; });
        hooks.Register<AfterToolCallEvent>((value, _) => { afterCalls++; value.Retry = afterCalls == 1; return default; });
        var tool = Tool((_, context, _) =>
        {
            var attempt = ++bodies;
            return Items([new ToolStreamEvent(attempt), Result(context, "result-" + attempt)]);
        });
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call())));

        Assert.Equal(3, events.Count);
        Assert.Equal(1, Assert.IsType<int>(Assert.IsType<ToolStreamEvent>(events[0]).Data));
        Assert.Equal(2, Assert.IsType<int>(Assert.IsType<ToolStreamEvent>(events[1]).Data));
        Assert.Equal("result-2", Assert.IsType<ToolStreamResultEvent>(events[2]).Result.Content);
        Assert.Equal(2, bodies);
        Assert.Equal(2, beforeCalls);
        Assert.Equal(2, afterCalls);
        AssertNoModelOrHistory(agent, model);
    }

    [Fact]
    public async Task OrdinaryAfterHookFailureRemainsTheExactOriginalException()
    {
        var marker = new object();
        var failure = new ApplicationException("ordinary after failure");
        failure.Data["marker"] = marker;
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((_, _) => throw failure);
        var disposals = 0;
        var tool = Tool((_, context, _) => Body(context));
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var error = await Within(CaptureAsync(CollectAsync(agent.StreamToolAsync(Call()))));

        Assert.Same(failure, error);
        Assert.Same(marker, failure.Data["marker"]);
        Assert.Null(failure.Data["Strands.CleanupErrors"]);
        Assert.Equal(1, disposals);
        await Within(agent.ShutdownAsync());
        AssertNoModelOrHistory(agent, model);

        async IAsyncEnumerable<IToolStreamEvent> Body(ToolContext context)
        {
            try { yield return Result(context, "done"); await Task.CompletedTask; }
            finally { disposals++; }
        }
    }

    [Fact]
    public async Task NonStreamingToolProducesOneTerminalWithTheDirectContextAndNoModel()
    {
        var marker = new object();
        var state = new Dictionary<string, object?> { ["marker"] = marker };
        var calls = 0;
        var tool = new FunctionTool(new("streamer", "Valid direct function", Json("{\"type\":\"object\",\"required\":[\"value\"],\"properties\":{\"value\":{\"type\":\"integer\"}}}")), (input, context, token) =>
        {
            calls++;
            Assert.Equal(5, input.GetProperty("value").GetInt32());
            Assert.Equal(token, context.CancelSignal);
            Assert.NotSame(state, context.InvocationState);
            Assert.Same(marker, context.InvocationState["marker"]);
            return new ValueTask<string>("plain");
        });
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call(input: Json("{\"value\":5}")), state)));

        var terminal = Assert.IsType<ToolStreamResultEvent>(Assert.Single(events)).Result;
        Assert.Equal("plain", terminal.Content);
        Assert.Equal(ToolResultStatus.Success, terminal.Status);
        Assert.Equal("call", terminal.ToolUseId);
        Assert.Equal(1, calls);
        AssertNoModelOrHistory(agent, model);
    }

    [Fact]
    public async Task SchemaInvalidNonStreamingInputProducesErrorWithoutCallingBodyOrModel()
    {
        var calls = 0;
        var beforeCalls = 0;
        var afterCalls = 0;
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((_, _) => { beforeCalls++; return default; });
        hooks.Register<AfterToolCallEvent>((_, _) => { afterCalls++; return default; });
        var tool = new FunctionTool(new("streamer", "Validated direct function", Json("{\"type\":\"object\",\"required\":[\"value\"],\"properties\":{\"value\":{\"type\":\"integer\"}}}")), (_, _, _) => { calls++; return new ValueTask<string>("must not run"); });
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call(input: Json("{\"value\":\"wrong\"}")))));

        var terminal = Assert.IsType<ToolStreamResultEvent>(Assert.Single(events)).Result;
        Assert.Equal(ToolResultStatus.Error, terminal.Status);
        Assert.Equal("call", terminal.ToolUseId);
        Assert.Contains("schema", terminal.Content);
        Assert.Equal(0, calls);
        Assert.Equal(1, beforeCalls);
        Assert.Equal(1, afterCalls);
        AssertNoModelOrHistory(agent, model);
    }

    [Fact]
    public async Task RetiredDirectContextCannotInterruptOrBorrowTheNextCallsCancellationScope()
    {
        ToolContext? retired = null;
        ToolContext? active = null;
        var second = new ControlledStream("second");
        var tool = Tool((_, context, _) =>
        {
            if (context.ToolUse.ToolUseId == "first") { retired = context; return Items([Result(context, "first")]); }
            active = context;
            return second;
        });
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        var initial = await Within(CollectAsync(agent.StreamToolAsync(Call("first"))));
        Assert.Equal("first", Assert.IsType<ToolStreamResultEvent>(Assert.Single(initial)).Result.Content);
        var old = Assert.IsType<ToolContext>(retired);
        var oldToken = old.CancelSignal;
        var iterator = agent.StreamToolAsync(Call("second")).GetAsyncEnumerator();
        Task<Exception?>? close = null;
        try
        {
            Assert.True(await Within(iterator.MoveNextAsync().AsTask()));
            var current = Assert.IsType<ToolContext>(active);
            Assert.NotEqual(oldToken, current.CancelSignal);
            Assert.Throws<InvalidOperationException>(() => old.Interrupt("expired", response: Json("true")));
            Assert.False(oldToken.IsCancellationRequested);
            Assert.False(current.CancelSignal.IsCancellationRequested);
            close = CaptureAsync(iterator.DisposeAsync().AsTask());
            Assert.Null(await Within(close));
            Assert.True(current.CancelSignal.IsCancellationRequested);
            Assert.Equal(oldToken, old.CancelSignal);
            Assert.False(old.CancelSignal.IsCancellationRequested);
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            second.ReleaseAll();
            if (close is not null) await Within(close);
            else await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task StateIsCapturedAtFirstMoveAndRemainsDetachedDuringProgress()
    {
        var initial = new object();
        var atFirstMove = new object();
        var later = new object();
        var state = new Dictionary<string, object?> { ["marker"] = initial };
        var release = Signal();
        ToolContext? captured = null;
        var tool = Tool((_, context, _) => Body(context));
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        var source = agent.StreamToolAsync(Call(), state);
        Assert.Null(captured);
        state["marker"] = atFirstMove;
        var iterator = source.GetAsyncEnumerator();
        Task<bool>? move = null;
        try
        {
            move = iterator.MoveNextAsync().AsTask();
            Assert.True(await Within(move));
            var context = Assert.IsType<ToolContext>(captured);
            Assert.NotSame(state, context.InvocationState);
            Assert.Same(atFirstMove, context.InvocationState["marker"]);
            state["marker"] = later;
            state["added"] = true;
            release.TrySetResult(true);
            move = iterator.MoveNextAsync().AsTask();
            Assert.True(await Within(move));
            Assert.Equal("done", Assert.IsType<ToolStreamResultEvent>(iterator.Current).Result.Content);
            Assert.Same(atFirstMove, context.InvocationState["marker"]);
            Assert.False(context.InvocationState.ContainsKey("added"));
            move = iterator.MoveNextAsync().AsTask();
            Assert.False(await Within(move));
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            release.TrySetResult(true);
            if (move is not null) await Within(CaptureAsync(move));
            await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }

        async IAsyncEnumerable<IToolStreamEvent> Body(ToolContext context)
        {
            captured = context;
            yield return new ToolStreamEvent("captured");
            await release.Task;
            yield return Result(context, "done");
        }
    }

    [Fact]
    public async Task IndependentUnrequestedBodyCancellationRemainsAnErrorTerminal()
    {
        using var independent = new CancellationTokenSource();
        var marker = new object();
        var failure = new OperationCanceledException("independent body failure", independent.Token);
        failure.Data["marker"] = marker;
        var stream = new FailingStream(failure);
        var tool = Tool((_, _, _) => stream);
        var model = new NoModel();
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });

        var events = await Within(CollectAsync(agent.StreamToolAsync(Call())));

        var terminal = Assert.IsType<ToolStreamResultEvent>(Assert.Single(events)).Result;
        Assert.Equal(ToolResultStatus.Error, terminal.Status);
        Assert.Same(failure, terminal.Error);
        Assert.Same(marker, failure.Data["marker"]);
        Assert.False(independent.IsCancellationRequested);
        Assert.Equal(1, stream.Disposals);
        AssertNoModelOrHistory(agent, model);
    }

    private static async Task AssertMixedCancellationAsync(bool cancelMethod)
    {
        using var method = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        var stream = new ControlledStream("call");
        ToolContext? captured = null;
        var model = new NoModel();
        var tool = Tool((_, context, token) => { captured = context; Assert.Equal(token, context.CancelSignal); return stream; });
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        var iterator = agent.StreamToolAsync(Call(), cancellationToken: method.Token).GetAsyncEnumerator(enumeration.Token);
        Task<Exception?>? move = null;
        try
        {
            Assert.True(await Within(iterator.MoveNextAsync().AsTask()));
            move = CaptureAsync(iterator.MoveNextAsync().AsTask());
            await Within(stream.Waiting.Task);
            if (cancelMethod) method.Cancel(); else enumeration.Cancel();
            var error = Assert.IsAssignableFrom<OperationCanceledException>(await Within(move));
            Assert.True(error.CancellationToken.IsCancellationRequested);
            Assert.Equal(cancelMethod, method.IsCancellationRequested);
            Assert.Equal(!cancelMethod, enumeration.IsCancellationRequested);
            Assert.True(stream.OperationToken.IsCancellationRequested);
            Assert.Equal(stream.OperationToken, Assert.IsType<ToolContext>(captured).CancelSignal);
            Assert.Equal(1, stream.Disposals);
            AssertNoModelOrHistory(agent, model);
        }
        finally
        {
            stream.ReleaseAll();
            if (move is not null) await Within(move);
            await Within(CaptureAsync(iterator.DisposeAsync().AsTask()));
        }
    }

    private static Agent AgentFor(ControlledStream stream, NoModel model, HookRegistry? hooks = null)
        => new(new() { Model = model, Tools = [Tool((_, context, token) => { Assert.Equal(token, context.CancelSignal); return stream; })], Hooks = hooks ?? new HookRegistry() });
    private static ToolUseBlock Call(string id = "call", JsonElement? input = null) => new(id, "streamer", input ?? Json("{}"));
    private static StreamingFunctionTool Tool(Func<JsonElement, ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> callback)
        => new(new("streamer", "Direct streaming conformance", Json("{\"type\":\"object\"}")), callback);
    private static ToolStreamResultEvent Result(ToolContext context, string text) => new(new(context.ToolUse.ToolUseId, text));
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static List<Exception> CleanupErrors(Exception error) => Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]);
    private static void AssertNoModelOrHistory(Agent agent, NoModel model) { Assert.Equal(0, model.Calls); Assert.Empty(agent.Messages); }
    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception error) { return error; }
    }
    private static async Task<List<IToolStreamEvent>> CollectAsync(IAsyncEnumerable<IToolStreamEvent> source, CancellationToken token = default)
    {
        var events = new List<IToolStreamEvent>();
        await using var iterator = source.GetAsyncEnumerator(token);
        while (await iterator.MoveNextAsync()) events.Add(iterator.Current);
        return events;
    }
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
        { Calls++; throw new InvalidOperationException("No model call is permitted by direct streaming tests."); }
    }

    private sealed class ControlledStream : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        private readonly string _id;
        private readonly bool _progressFirst;
        private readonly Exception? _disposeFailure;
        private readonly Exception? _cancellationFailure;
        private readonly TaskCompletionSource<bool> _cancellationReached = Signal();
        private CancellationTokenRegistration _registration;
        private bool _progressed;
        private bool _terminal;
        internal ControlledStream(string id, bool progressFirst = true, bool holdDispose = false, Exception? disposeFailure = null, Exception? cancellationFailure = null)
        {
            _id = id; _progressFirst = progressFirst; _disposeFailure = disposeFailure; _cancellationFailure = cancellationFailure;
            if (!holdDispose) ReleaseDispose.TrySetResult(true);
        }
        internal TaskCompletionSource<bool> Waiting { get; } = Signal();
        internal TaskCompletionSource<bool> ReleaseMoveNext { get; } = Signal();
        internal TaskCompletionSource<bool> DisposeEntered { get; } = Signal();
        internal TaskCompletionSource<bool> ReleaseDispose { get; } = Signal();
        internal CancellationToken OperationToken { get; private set; }
        internal int Disposals { get; private set; }
        internal int CancellationCallbacks { get; private set; }
        public IToolStreamEvent Current { get; private set; } = null!;
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            OperationToken = cancellationToken;
            _registration = cancellationToken.Register(() =>
            {
                CancellationCallbacks++;
                _cancellationReached.TrySetResult(true);
                if (_cancellationFailure is not null) throw _cancellationFailure;
            });
            return this;
        }
        public async ValueTask<bool> MoveNextAsync()
        {
            if (_progressFirst && !_progressed) { _progressed = true; Current = new ToolStreamEvent(_id); return true; }
            if (_terminal) return false;
            Waiting.TrySetResult(true);
            await Task.WhenAny(ReleaseMoveNext.Task, _cancellationReached.Task);
            OperationToken.ThrowIfCancellationRequested();
            _terminal = true;
            Current = new ToolStreamResultEvent(new(_id, "done"));
            return true;
        }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            DisposeEntered.TrySetResult(true);
            try { await ReleaseDispose.Task; }
            finally { _registration.Dispose(); }
            if (_disposeFailure is not null) throw _disposeFailure;
        }
        internal void ReleaseAll() { ReleaseMoveNext.TrySetResult(true); ReleaseDispose.TrySetResult(true); }
    }

    private sealed class FailingStream(Exception failure) : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        internal int Disposals { get; private set; }
        public IToolStreamEvent Current => throw new InvalidOperationException("No current item after failure.");
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => throw failure;
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
