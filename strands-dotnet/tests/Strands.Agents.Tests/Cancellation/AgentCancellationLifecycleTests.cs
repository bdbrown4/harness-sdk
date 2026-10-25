using System.Runtime.CompilerServices;
using System.Text.Json;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationLifecycleTests
{
    [Fact]
    public async Task IdleCancellationIsIgnoredAndOldContextsKeepTheirOwnSignal()
    {
        var contexts = new List<ToolContext>();
        var model = new CancellationTestModel(Call(("first", "capture", "{}")), Text("one"), Call(("second", "capture", "{}")), Text("two"));
        var tool = Tool("capture", (_, context, _) =>
        {
            Assert.Equal(context.Agent.CancelSignal, context.CancelSignal);
            Assert.True(context.CancelSignal.CanBeCanceled);
            contexts.Add(context);
            return new("done");
        });
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        agent.Cancel("idle request");
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("one", (await agent.InvokeAsync("first")).ToString());
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        agent.Cancel();
        Assert.Equal("two", (await agent.InvokeAsync("second")).ToString());
        Assert.NotEqual(contexts[0].CancelSignal, contexts[1].CancelSignal);
        Assert.False(contexts[0].CancelSignal.IsCancellationRequested);
        Assert.False(contexts[1].CancelSignal.IsCancellationRequested);
    }

    [Fact]
    public async Task DirectToolContextCapturesItsOwnBorrowedOperationSignal()
    {
        using var caller = new CancellationTokenSource(); ToolContext? captured = null;
        var tool = Tool("direct", (_, context, _) => { captured = context; context.Agent.Cancel(); return new("direct result"); });
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Text("must not run")), Tools = [tool] });
        var result = await agent.InvokeToolAsync(new("direct-call", "direct", JsonSerializer.SerializeToElement(new { })), cancellationToken: caller.Token);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal(caller.Token, captured!.CancelSignal);
        Assert.False(caller.IsCancellationRequested);
        caller.Cancel();
        Assert.True(captured.CancelSignal.IsCancellationRequested);
    }

    [Fact]
    public async Task BeforeModelCanCancelCooperativelyWithoutChangingTheBorrowedCallerToken()
    {
        using var caller = new CancellationTokenSource();
        var model = new CancellationTestModel(Text("unexpected"));
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<BeforeModelCallEvent>((_, token) =>
        {
            Assert.Equal(caller.Token, token);
            agent.Cancel("stopped before IO");
            Assert.True(agent.CancelSignal.IsCancellationRequested);
            Assert.False(token.IsCancellationRequested);
            return default;
        });
        var result = await agent.InvokeAsync("go", cancellationToken: caller.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal("stopped before IO", result.Message!.Text);
        Assert.False(caller.IsCancellationRequested);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeTheInputCommitDoesNotAppendANewPrompt(bool external)
    {
        using var caller = new CancellationTokenSource();
        var model = new CancellationTestModel(Text("must not run"));
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<BeforeInvocationEvent>((_, token) =>
        {
            Assert.Equal(caller.Token, token);
            if (external) caller.Cancel(); else agent.Cancel();
            return default;
        });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("not accepted", cancellationToken: caller.Token)).StopReason);
        Assert.Empty(agent.Messages);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest non-null request")]
    public async Task DeferredRequestsUseTheLastNonNullTextIncludingEmpty(string text)
    {
        var model = new CancellationTestModel(Call(("stop", "stop", "{}")));
        var tool = Tool("stop", (_, context, _) =>
        {
            context.Agent.Cancel("replaced", true);
            context.Agent.Cancel(text, true);
            context.Agent.Cancel(afterCurrentTools: true);
            return new("accepted");
        });
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(text, Assert.IsType<TextBlock>(Assert.Single(result.Message!.Content)).Text);
        Assert.Single(agent.Messages, message => message.Role == Role.Assistant && message.Content.OfType<TextBlock>().Any());
    }

    [Fact]
    public async Task DeferredCancellationWithoutAToolBatchRetainsNaturalCompletion()
    {
        var model = new CancellationTestModel(Text("natural response"));
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { agent.Cancel("not committed", true); return default; });
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("natural response", result.ToString());
        Assert.DoesNotContain(agent.Messages, message => message.Text == "not committed");
    }

    [Fact]
    public async Task ImmediateRequestOverridesDeferredAndSkipsUnstartedSequentialPeers()
    {
        var peers = 0;
        var model = new CancellationTestModel(Call(("stop", "stop", "{}"), ("peer", "peer", "{}")));
        var stop = Tool("stop", (_, context, _) => { context.Agent.Cancel("deferred", true); context.Agent.Cancel("immediate"); return new("accepted"); });
        var peer = Tool("peer", (_, _, _) => { peers++; return new("unexpected"); });
        await using var agent = new Agent(new() { Model = model, Tools = [stop, peer], ToolExecution = ToolExecutionStrategy.Sequential });
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal("immediate", result.Message!.Text);
        Assert.Equal(0, peers);
        var results = agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "stop", "peer" }, results.Select(value => value.ToolUseId));
    }

    [Fact]
    public async Task DeferredConcurrentPeersKeepTheirTokensLiveUntilTheirBodiesFinish()
    {
        var peerEntered = Gate(); var release = Gate(); var stopped = Gate();
        var model = new CancellationTestModel(Call(("stop", "stop", "{}"), ("peer", "peer", "{}")));
        var stop = Tool("stop", async (_, context, token) =>
        {
            await peerEntered.Task;
            context.Agent.Cancel(afterCurrentTools: true);
            stopped.TrySetResult(true);
            await release.Task;
            Assert.False(token.IsCancellationRequested);
            return "stop completed";
        });
        var peer = Tool("peer", async (_, context, token) =>
        {
            peerEntered.TrySetResult(true);
            await release.Task;
            Assert.False(token.IsCancellationRequested);
            Assert.False(context.CancelSignal.IsCancellationRequested);
            return "peer completed";
        });
        await using var agent = new Agent(new() { Model = model, Tools = [stop, peer], ToolExecution = ToolExecutionStrategy.Concurrent });
        var invocation = agent.InvokeAsync("go");
        try { await Wait(stopped.Task); Assert.False(invocation.IsCompleted); }
        finally { release.TrySetResult(true); }
        await Wait(invocation);
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal(2, agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Count());
    }

    [Fact]
    public async Task PublicCancelDoesNotPoisonInitialization()
    {
        var entered = Gate(); var release = Gate();
        using var caller = new CancellationTokenSource();
        var plugin = new InitializingPlugin(async (agent, token) => { Assert.Equal(caller.Token, token); entered.TrySetResult(true); await release.Task; });
        var model = new CancellationTestModel(Text("initialized"));
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin] });
        var invocation = agent.InvokeAsync("go", cancellationToken: caller.Token);
        try { await Wait(entered.Task); agent.Cancel("ignored during initialization"); Assert.Equal(CancellationToken.None, agent.CancelSignal); }
        finally { release.TrySetResult(true); }
        await Wait(invocation);
        Assert.Equal(StopReasons.EndTurn, (await invocation).StopReason);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelAndCloseStillCommitsExplicitTextAndReleasesTheInvocation()
    {
        var model = new CancellationTestModel(Text("partial"), Text("reused"));
        await using var agent = new Agent(new() { Model = model });
        var after = 0;
        agent.Hooks.Register<AfterInvocationEvent>((_, _) => { after++; return default; });
        await using (var stream = agent.StreamAsync("go").GetAsyncEnumerator())
        {
            while (await stream.MoveNextAsync())
                if (stream.Current is ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent }) { agent.Cancel("cancelled while closing"); break; }
        }
        Assert.Equal("cancelled while closing", agent.Messages.Last().Text);
        Assert.Equal(1, after);
        Assert.Equal("reused", (await agent.InvokeAsync("again")).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationObserverCanJoinTheInvocationWithoutBlockingScopeDisposal(bool external)
    {
        var entered = Gate(); var release = Gate(); var observerEntered = Gate();
        using var caller = new CancellationTokenSource();
        var model = new WaitingModel(entered, release);
        await using var agent = new Agent(new() { Model = model });
        var invocation = agent.InvokeAsync("go", cancellationToken: caller.Token);
        await Wait(entered.Task);
        var token = agent.CancelSignal;
        var callbackFinished = Gate();
        using var registration = token.Register(() => { observerEntered.TrySetResult(true); invocation.GetAwaiter().GetResult(); callbackFinished.TrySetResult(true); });
        var cancel = Task.Run(() => { if (external) caller.Cancel(); else agent.Cancel(); });
        try { await Wait(observerEntered.Task); }
        finally { release.TrySetResult(true); }
        await Wait(invocation);
        await Wait(cancel);
        await Wait(callbackFinished.Task);
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(1, model.Disposals);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal(external, caller.IsCancellationRequested);
        using var stillBorrowed = caller.Token.Register(() => { });
    }

    [Fact]
    public async Task ReentrantAndConcurrentCancelRequestsDoNotDisposeAnInFlightSource()
    {
        var entered = Gate(); var release = Gate(); var callbacks = 0;
        await using var agent = new Agent(new() { Model = new WaitingModel(entered, release) });
        var invocation = agent.InvokeAsync("go");
        await Wait(entered.Task);
        var token = agent.CancelSignal;
        using var registration = token.Register(() => { Interlocked.Increment(ref callbacks); agent.Cancel("observer request"); });
        try
        {
            await Wait(Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => agent.Cancel()))));
            Assert.Equal(1, callbacks);
        }
        finally { release.TrySetResult(true); }
        await Wait(invocation);
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal("observer request", (await invocation).Message!.Text);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Fact]
    public async Task ThrowingCancellationObserversRetainBclCallerCausalityAndDoNotLeakTheScope()
    {
        var failure = new IOException("observer failed");
        var entered = Gate(); var release = Gate();
        var model = new WaitingModel(entered, release);
        await using var agent = new Agent(new() { Model = model });
        var invocation = agent.InvokeAsync("go");
        await Wait(entered.Task);
        using var registration = agent.CancelSignal.Register(() => throw failure);
        try
        {
            var error = Assert.Throws<AggregateException>(() => agent.Cancel());
            Assert.Contains(failure, error.InnerExceptions);
        }
        finally { release.TrySetResult(true); }
        await Wait(invocation);
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        agent.Cancel();
    }

    [Fact]
    public async Task CancelledPassCannotStartAHookRequestedContinuationOrRepeatItsAcceptedBody()
    {
        var effects = 0;
        var model = new CancellationTestModel(Call(("call", "save", "{}")), Text("fresh invocation"));
        var tool = Tool("save", (_, context, _) => { effects++; context.Agent.Cancel(); return new("saved"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        using (agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("do not run this continuation"); return default; }))
            Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("go")).StopReason);
        Assert.Single(model.Requests);
        Assert.Equal("fresh invocation", (await agent.InvokeAsync("again")).ToString());
        Assert.Equal(1, effects);
    }

    private sealed class InitializingPlugin(Func<Agent, CancellationToken, Task> initialize) : IPlugin
    {
        public string Name => "initialization gate";
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => await initialize(agent, cancellationToken);
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class WaitingModel(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release) : IModel
    {
        internal int Disposals;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                entered.TrySetResult(true);
                await release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                // A later-registered observer can delay propagation to a composed transport token.
                // Yield a valid protocol start so the agent's own signal check can accept cancellation.
                yield return new ModelMessageStartEvent();
            }
            finally { Interlocked.Increment(ref Disposals); }
        }
    }
}
