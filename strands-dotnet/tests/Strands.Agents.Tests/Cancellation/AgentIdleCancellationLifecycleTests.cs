using System.Runtime.CompilerServices;
using System.Text.Json;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentIdleCancellationLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingCancellationDoesNotCancelInitializationOrTheBorrowedCaller(bool beforeAdmission)
    {
        var entered = Gate(); var release = Gate();
        using var caller = new CancellationTokenSource();
        var plugin = new Initializer(async (agent, token) =>
        {
            Assert.Equal(caller.Token, token);
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(CancellationToken.None, agent.CancelSignal);
            entered.TrySetResult(true);
            await release.Task;
            Assert.False(token.IsCancellationRequested);
        });
        var model = new CancellationTestModel(Text("following invocation"));
        await using var agent = new Agent(new() { Model = model, Plugins = [plugin], IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        if (beforeAdmission) agent.Cancel("queued");
        var invocation = agent.InvokeAsync("not accepted", cancellationToken: caller.Token);
        try
        {
            await Wait(entered.Task);
            if (!beforeAdmission) agent.Cancel("queued");
            Assert.Equal(CancellationToken.None, agent.CancelSignal);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("concurrent"));
        }
        finally { release.TrySetResult(true); }
        await Wait(invocation);
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal("queued", (await invocation).ToString());
        Assert.Empty(model.Requests);
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("following invocation", (await agent.InvokeAsync("again")).ToString());
    }

    [Fact]
    public async Task FailedInitializationClearsTheAdmittedRequestAndPreservesItsPrimaryFailure()
    {
        var failure = new IOException("initialization failure");
        var plugin = new Initializer((agent, _) => { agent.Cancel("not persistent"); throw failure; });
        await using var agent = new Agent(new() { Model = new CancellationTestModel(), Plugins = [plugin], IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("before admission");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("go")));
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Empty(agent.Messages);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("again")));
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Fact]
    public async Task InvalidInterruptPreflightSettlesAnAdmissionWithoutPoisoningTheNextInvocation()
    {
        var model = new CancellationTestModel(Text("valid"));
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("discard on admitted error");
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync(null, new() { InterruptResponses = [new("unknown", JsonSerializer.SerializeToElement(true))] }));
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("valid", (await agent.InvokeAsync("go")).ToString());
    }

    [Fact]
    public async Task InputValidationBeforeAdmissionLeavesAnIdleRequestPending()
    {
        await using var agent = new Agent(new() { Model = new CancellationTestModel(), IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("retained");
        var response = new InterruptResponse("duplicate", JsonSerializer.SerializeToElement(true));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync(null, new() { InterruptResponses = [response, response] }));
        Assert.True(agent.CancelSignal.IsCancellationRequested);
        Assert.Equal("retained", (await agent.InvokeAsync("go")).ToString());
    }

    [Fact]
    public async Task AlreadyCancelledCallerSettlesThePendingRequestWithoutInitializing()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var initializes = 0;
        var model = new CancellationTestModel(Text("next"));
        await using var agent = new Agent(new() { Model = model, Plugins = [new Initializer((_, _) => { initializes++; return Task.CompletedTask; })], IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("pending");
        var result = await agent.InvokeAsync("not accepted", cancellationToken: caller.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(0, initializes);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("next", (await agent.InvokeAsync("next")).ToString());
        Assert.Equal(1, initializes);
    }

    [Fact]
    public async Task ChildInheritsThePolicyButNeverThePendingRequestOrMessage()
    {
        var model = new CancellationTestModel(Text("child"), Text("child after own cancellation"));
        await using var parent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        parent.Cancel("parent only");
        await using var child = parent.CreateChild();
        Assert.Equal(CancellationToken.None, child.CancelSignal);
        Assert.Equal("child", (await child.InvokeAsync("child input")).ToString());
        Assert.True(parent.CancelSignal.IsCancellationRequested);
        Assert.Equal("parent only", (await parent.InvokeAsync("parent input")).ToString());
        child.Cancel("child only");
        Assert.Equal(CancellationToken.None, parent.CancelSignal);
        Assert.Equal("child only", (await child.InvokeAsync("cancelled child")).ToString());
        Assert.Equal("child after own cancellation", (await child.InvokeAsync("again")).ToString());
    }

    [Fact]
    public async Task CancelledPassCannotResumeAndDoesNotPoisonAnIndependentInvocation()
    {
        var model = new CancellationTestModel(Text("fresh"));
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        using (agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("forbidden continuation"); return default; }))
        {
            agent.Cancel("stop");
            Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("go", new() { ResetLimitsOnHookResume = true })).StopReason);
        }
        Assert.Empty(model.Requests);
        Assert.Equal("fresh", (await agent.InvokeAsync("fresh")).ToString());
    }

    [Fact]
    public async Task ClosingAtTheAfterBoundarySettlesTheRequestWithoutAHiddenContinuation()
    {
        var model = new CancellationTestModel(Text("fresh"));
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("stop");
        using (agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("not run"); return default; }))
        {
            await using var iterator = agent.StreamAsync("go").GetAsyncEnumerator();
            while (await iterator.MoveNextAsync()) if (iterator.Current is AfterInvocationEvent) break;
        }
        Assert.Empty(model.Requests);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("fresh", (await agent.InvokeAsync("fresh")).ToString());
    }

    [Fact]
    public async Task CancellationAfterAnAcceptedResultDoesNotBecomeANewIdleRequest()
    {
        var model = new CancellationTestModel(Text("first"), Text("second"));
        await using var agent = new Agent(new() { Model = model, IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        using (agent.Hooks.Register<AgentResultEvent>((_, _) => { agent.Cancel("late"); return default; }))
            Assert.Equal("first", (await agent.InvokeAsync("first")).ToString());
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("second", (await agent.InvokeAsync("second")).ToString());
        Assert.DoesNotContain(agent.Messages, value => value.Text == "late");
    }

    [Fact]
    public async Task ReentrantCancellationObserversRunOutsideTheOwnerAndScopeLocks()
    {
        var entered = Gate(); var release = Gate();
        using var caller = new CancellationTokenSource();
        await using var agent = new Agent(new() { Model = new WaitingModel(entered, release), IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        var invocation = agent.InvokeAsync("go", cancellationToken: caller.Token);
        await Wait(entered.Task);
        var signal = agent.CancelSignal;
        using var registration = signal.Register(() =>
        {
            var observer = Task.Run(() => { Assert.True(agent.CancelSignal.IsCancellationRequested); agent.Cancel("observer"); });
            Assert.True(observer.Wait(TimeSpan.FromSeconds(5)), "Cancellation held an owner/scope lock across a callback.");
            observer.GetAwaiter().GetResult();
        });
        try { agent.Cancel(); }
        finally { release.TrySetResult(true); }
        await Wait(invocation);
        Assert.Equal("observer", (await invocation).ToString());
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.True(signal.IsCancellationRequested);
    }

    [Fact]
    public async Task ShutdownAndDisposalDoNotAcceptNewIdleRequests()
    {
        var agent = new Agent(new() { Model = new CancellationTestModel(), IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation });
        agent.Cancel("before shutdown");
        await agent.ShutdownAsync();
        agent.Cancel("after shutdown");
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        await agent.DisposeAsync();
        agent.Cancel("after disposal");
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    private sealed class Initializer(Func<Agent, CancellationToken, Task> initialize) : IPlugin
    {
        public string Name => "idle cancellation initialization";
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => await initialize(agent, cancellationToken);
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class WaitingModel(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            entered.TrySetResult(true);
            await release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
        }
    }
}
