using System.Runtime.CompilerServices;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationTimingLifecycleTests
{
    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly, false)]
    [InlineData(ExplicitCancellationTiming.NativeEarly, true)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, true)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, true)]
    public async Task OriginalCallerCancellationAlwaysStopsBeforeProviderEntry(ExplicitCancellationTiming timing, bool atModel)
    {
        using var caller = new CancellationTokenSource();
        var model = new CancellationTestModel(Text("not run"));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        if (atModel) agent.Hooks.Register<BeforeModelCallEvent>((value, _) => { caller.Cancel(); value.CancelReason = "denied"; return default; });
        else agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { caller.Cancel(); value.CancelReason = "denied"; return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input", cancellationToken: caller.Token)).StopReason);
        Assert.Empty(model.Requests);
        Assert.Equal(atModel ? 1 : 0, agent.Messages.Count);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task InitializationRetainsItsHardCallerAndCannotExposeAnEnabledPassSignal(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource();
        var entered = Gate(); var release = Gate();
        CancellationToken initializationToken = default;
        var model = new CancellationTestModel(Text("not run"));
        var plugin = new Initializer(async (agent, token) =>
        {
            initializationToken = token;
            Assert.Equal(CancellationToken.None, agent.CancelSignal);
            entered.TrySetResult(true);
            await release.Task;
            token.ThrowIfCancellationRequested();
        });
        await using var agent = new Agent(new()
        {
            Model = model,
            Plugins = [plugin],
            ExplicitCancellationTiming = timing,
            IdleCancellationBehavior = IdleCancellationBehavior.CancelNextInvocation
        });
        var invocation = agent.InvokeAsync("not accepted", cancellationToken: caller.Token);
        try
        {
            await Wait(entered.Task);
            agent.Cancel("pending owned request");
            Assert.Equal(caller.Token, initializationToken);
            Assert.False(initializationToken.IsCancellationRequested);
            caller.Cancel();
        }
        finally { release.TrySetResult(true); }
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.User);
        Assert.Equal("pending owned request", Assert.Single(agent.Messages).Text);
        Assert.Empty(model.Requests);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task PreparationMayUseCallerTokenButActualProviderKeepsTheOwnedCancelledSignal(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource();
        var model = new CountingModel();
        CancellationToken owned = default;
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { owned = agent.CancelSignal; agent.Cancel(); return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input", cancellationToken: caller.Token)).StopReason);
        Assert.False(caller.IsCancellationRequested);
        if (timing == ExplicitCancellationTiming.NativeEarly)
        {
            Assert.Equal(0, model.Counts);
            Assert.Equal(0, model.Entries);
        }
        else
        {
            Assert.Equal(1, model.Counts);
            Assert.Equal(caller.Token, model.CountToken);
            Assert.Equal(1, model.Entries);
            // Async iterator composition can derive a linked provider token; the original caller stays independent.
            Assert.True(owned.IsCancellationRequested);
            Assert.NotEqual(caller.Token, model.StreamToken);
            Assert.True(model.StreamToken.IsCancellationRequested);
        }
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task AProviderChunkObtainedAfterARequestIsDiscardedAndPhysicalCleanupIsJoined(ExplicitCancellationTiming timing)
    {
        var entered = Gate(); var release = Gate();
        var model = new RawModel(entered, release);
        var updates = 0;
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        agent.Hooks.Register<ModelStreamUpdateEvent>((_, _) => { updates++; return default; });
        var invocation = agent.InvokeAsync("input");
        try
        {
            await Wait(entered.Task);
            Assert.False(invocation.IsCompleted);
            Assert.Equal(0, updates);
        }
        finally { release.TrySetResult(true); }
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal(1, model.Disposals);
        Assert.Equal(0, updates);
        Assert.Single(agent.Messages);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task ProviderCleanupCannotBeHiddenByAnOwnedCancellation(ExplicitCancellationTiming timing)
    {
        var cleanup = new IOException("provider iterator cleanup");
        var model = new RawModel(Gate(), Released(), cleanup);
        Exception? observed = null; CancellationToken owned = default;
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { owned = agent.CancelSignal; agent.Cancel(); return default; });
        agent.Hooks.Register<AfterModelCallEvent>((value, _) => { observed = value.Error; return default; });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeAsync("input"));
        Assert.Same(observed, error);
        Assert.Equal(owned, error.CancellationToken);
        Assert.Equal(new Exception[] { cleanup }, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, model.Disposals);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task RequestPreventsHookResumeEvenWhenTheAcceptedAnswerHasANaturalStop(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Text("accepted"), Text("independent"));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        using (agent.Hooks.Register<AfterModelCallEvent>((_, _) => { agent.Cancel(); return default; }))
        using (agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("not resumed"); return default; }))
        {
            var result = await agent.InvokeAsync("input", new() { ResetLimitsOnHookResume = true });
            Assert.Equal(timing == ExplicitCancellationTiming.NativeEarly ? StopReasons.Cancelled : StopReasons.EndTurn, result.StopReason);
        }
        Assert.Single(model.Requests);
        Assert.Equal("independent", (await agent.InvokeAsync("independent input")).ToString());
        Assert.DoesNotContain(agent.Messages, message => message.Text == "not resumed");
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task ClosingAtAfterInvocationNeverExecutesAHiddenContinuation(ExplicitCancellationTiming timing)
    {
        var model = new CancellationTestModel(Text("accepted"), Text("independent"));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        using (agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("not resumed"); return default; }))
        {
            await using var iterator = agent.StreamAsync("input").GetAsyncEnumerator();
            while (await iterator.MoveNextAsync()) if (iterator.Current is AfterInvocationEvent) break;
        }
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Single(model.Requests);
        Assert.Equal("independent", (await agent.InvokeAsync("independent input")).ToString());
        Assert.DoesNotContain(agent.Messages, message => message.Text == "not resumed");
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task CallerCancellationAfterAnAcceptedOrdinaryAnswerStillWins(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource();
        var model = new CancellationTestModel(Text("accepted"));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<AfterModelCallEvent>((_, _) => { caller.Cancel(); return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input", cancellationToken: caller.Token)).StopReason);
        Assert.Equal("accepted", agent.Messages.Last().Text);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task PausedOutcomeCannotHideIndependentCleanupFailure(ExplicitCancellationTiming timing)
    {
        var cleanup = new IOException("interrupt callback cleanup");
        var tool = Tool("save", (_, _, _) => new("not run"));
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Call(("save", "save", "{}"))), Tools = [tool], ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeToolsEvent>((value, _) =>
        {
            agent.Cancel();
            try { value.Interrupt("approval"); }
            catch (InterruptException interrupted) { interrupted.Data["Strands.CleanupErrors"] = new List<Exception> { cleanup }; throw; }
            return default;
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeAsync("input"));
        var interrupted = Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]).OfType<InterruptException>());
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(interrupted.Data["Strands.CleanupErrors"])));
        Assert.Equal("approval", Assert.Single(agent.InterruptState!.Interrupts).Name);
    }

    private static TaskCompletionSource<bool> Released() { var gate = Gate(); gate.SetResult(true); return gate; }

    private sealed class Initializer(Func<Agent, CancellationToken, Task> initialize) : IPlugin
    {
        public string Name => "timing initializer";
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => await initialize(agent, cancellationToken);
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class CountingModel : IModel, IModelTokenCounter
    {
        internal int Counts, Entries;
        internal CancellationToken CountToken, StreamToken;
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Counts++; CountToken = cancellationToken; cancellationToken.ThrowIfCancellationRequested(); return new(1L); }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Entries++; StreamToken = cancellationToken; await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent(); }
    }

    private sealed class RawModel(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release, Exception? cleanup = null) : IModel, IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        internal int Disposals;
        public ModelStreamEvent Current => new ModelMessageStartEvent();
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => this;
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(true);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            entered.TrySetResult(true);
            await release.Task;
            if (cleanup is not null) throw cleanup;
        }
    }
}
