namespace Strands.Agents.Tests.EventParity;

public sealed class EventLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AModelObserverFailureKeepsItsIdentityWhenIteratorAndAfterCleanupFail(bool afterFails)
    {
        var primary = new IOException("observer failed"); var disposal = new IOException("iterator cleanup failed"); var afterError = new IOException("after failed");
        var model = new TrackedModel(disposal); var hooks = new HookRegistry(); var after = 0;
        hooks.Register<ModelStreamUpdateEvent>((_, _) => throw primary);
        hooks.Register<AfterModelCallEvent>((value, _) => { Assert.Same(primary, value.Error); if (afterFails) throw afterError; return default; });
        hooks.Register<AfterInvocationEvent>((value, _) => { after++; Assert.Same(primary, value.Error); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question")));
        var cleanup = Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]);
        Assert.Contains(disposal, cleanup); Assert.Equal(afterFails ? 2 : 1, cleanup.Count); if (afterFails) Assert.Contains(afterError, cleanup);
        Assert.Equal(1, model.Disposals); Assert.Equal(1, after);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("message")]
    [InlineData("after")]
    public async Task EarlyClosureUnwindsOnceWithoutAHiddenContinuationOrFinalResultHook(string boundary)
    {
        var model = new TrackedModel(); var hooks = new HookRegistry(); var after = 0; var results = 0;
        hooks.Register<AfterInvocationEvent>((value, _) => { if (++after == 1 && value.Result is not null) value.Resume = new InvocationContinuation("hidden"); return default; });
        hooks.Register<AgentResultEvent>((_, _) => { results++; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var stopped = false;
        await foreach (var item in agent.StreamAsync("question"))
            if (boundary == "block" && item is ContentBlockEvent || boundary == "message" && item is ModelMessageEvent || boundary == "after" && item is AfterInvocationEvent)
            { stopped = true; break; }
        Assert.True(stopped); Assert.Equal(1, model.Calls); Assert.Equal(1, model.Disposals); Assert.Equal(1, after); Assert.Equal(0, results);
        Assert.Equal("answer", (await agent.InvokeAsync(null)).ToString()); Assert.Equal(2, model.Calls); Assert.Equal(2, after); Assert.Equal(1, results);
    }

    [Fact]
    public async Task PublicCancelFromAStreamHookUsesItsSeparateSignalAndPreventsACompletedBlockOrMessage()
    {
        var model = new TrackedModel(); var hooks = new HookRegistry(); var blocks = 0; var messages = 0; CancellationToken saved = default;
        var registration = hooks.Register<ModelStreamUpdateEvent>((value, token) => { Assert.Equal(CancellationToken.None, token); saved = value.Agent!.CancelSignal; value.Agent.Cancel(); Assert.True(saved.IsCancellationRequested); return default; });
        hooks.Register<ContentBlockEvent>((_, _) => { blocks++; return default; });
        hooks.Register<ModelMessageEvent>((_, _) => { messages++; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("question")).StopReason);
        Assert.Equal(0, blocks); Assert.Equal(0, messages); Assert.Equal(1, model.Disposals);
        registration.Dispose(); Assert.Equal("answer", (await agent.InvokeAsync(null)).ToString()); Assert.True(saved.IsCancellationRequested);
    }

    [Fact]
    public async Task CallerCancellationCanInterruptANewModelCallbackUsingTheOriginalToken()
    {
        var model = new TrackedModel(); var hooks = new HookRegistry(); using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.Register<ModelStreamUpdateEvent>(async (_, token) => { Assert.Equal(caller.Token, token); entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var invocation = agent.InvokeAsync("question", cancellationToken: caller.Token);
        try { Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000))); }
        finally { caller.Cancel(); }
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason); Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task AFinalResultHookFailureDoesNotRepeatAfterCleanupAndReleasesInvocationAdmission()
    {
        var failure = new IOException("result observer failed"); var hooks = new HookRegistry(); var after = 0; var model = new TrackedModel();
        hooks.Register<AfterInvocationEvent>((_, _) => { after++; return default; });
        var registration = hooks.Register<AgentResultEvent>((_, _) => throw failure);
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"))); Assert.Equal(1, after); Assert.Equal(1, model.Disposals);
        registration.Dispose(); Assert.Equal("answer", (await agent.InvokeAsync(null)).ToString()); Assert.Equal(2, after); Assert.Equal(2, model.Disposals);
    }

    private sealed class TrackedModel(Exception? cleanup = null) : IModel
    {
        internal int Calls, Disposals;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) { Calls++; return new Stream(this, cancellationToken); }
        private sealed class Stream(TrackedModel owner, CancellationToken token) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            private int _index = -1;
            private readonly ModelStreamEvent[] _events = EventTestModel.Text("answer");
            public ModelStreamEvent Current => _events[_index];
            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public ValueTask<bool> MoveNextAsync() { token.ThrowIfCancellationRequested(); return new(++_index < _events.Length); }
            public ValueTask DisposeAsync() { owner.Disposals++; if (owner.Cleanup is { } failure) throw failure; return default; }
        }
        private Exception? Cleanup => cleanup;
    }
}
