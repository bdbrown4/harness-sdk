using System.Runtime.CompilerServices;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationTimingCounterTests
{
    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly, false)]
    [InlineData(ExplicitCancellationTiming.NativeEarly, true)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, true)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, true)]
    public async Task IndependentCounterCancellationRemainsAnUnavailableProjectionAfterAnExplicitRequest(ExplicitCancellationTiming timing, bool tokenless)
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        independent.Cancel();
        var model = new CounterModel(_ => throw new OperationCanceledException(tokenless ? CancellationToken.None : independent.Token));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input", cancellationToken: caller.Token)).StopReason);
        var late = timing != ExplicitCancellationTiming.NativeEarly;
        Assert.Equal(late ? 1 : 0, model.Counts);
        Assert.Equal(late ? 1 : 0, model.ProviderEntries);
        if (late) Assert.Equal(caller.Token, model.CountToken);
        Assert.False(caller.IsCancellationRequested);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task ExactOwnedScopeCounterCancellationDoesNotFallThroughToTheProvider(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource();
        CancellationToken owned = default;
        var model = new CounterModel(_ => throw new OperationCanceledException(owned));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { owned = agent.CancelSignal; agent.Cancel(); return default; });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input", cancellationToken: caller.Token)).StopReason);
        Assert.True(owned.IsCancellationRequested);
        Assert.Equal(timing == ExplicitCancellationTiming.NativeEarly ? 0 : 1, model.Counts);
        Assert.Equal(0, model.ProviderEntries);
        Assert.False(caller.IsCancellationRequested);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task HardCallerCancellationDuringProjectionDoesNotFallThroughEvenWithAnUncorrelatedException(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource();
        var model = new CounterModel(_ => { caller.Cancel(); throw new OperationCanceledException(); });
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("input", cancellationToken: caller.Token)).StopReason);
        Assert.Equal(1, model.Counts);
        Assert.Equal(0, model.ProviderEntries);
        Assert.True(caller.IsCancellationRequested);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task AnUnavailableIndependentProjectionStillAllowsAnUncancelledInvocation(ExplicitCancellationTiming timing)
    {
        using var independent = new CancellationTokenSource();
        independent.Cancel();
        var model = new CounterModel(_ => throw new OperationCanceledException(independent.Token));
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        Assert.Equal("answer", (await agent.InvokeAsync("input")).ToString());
        Assert.Equal(1, model.Counts);
        Assert.Equal(1, model.ProviderEntries);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task UnavailableProjectionCannotHideItsIndependentCleanupFailure(ExplicitCancellationTiming timing)
    {
        var cleanup = new IOException("counter resource cleanup");
        var primary = new OperationCanceledException("independent counter cancellation");
        primary.Data["Strands.CleanupErrors"] = new List<Exception> { cleanup };
        var model = new CounterModel(_ => throw primary);
        Exception? observed = null;
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        agent.Hooks.Register<AfterModelCallEvent>((value, _) => { observed = value.Error; return default; });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeAsync("input"));
        Assert.Same(primary, error);
        Assert.Same(primary, observed);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, model.Counts);
        Assert.Equal(0, model.ProviderEntries);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task UnrequestedOptionalProjectionKeepsTheExistingFallbackEvenWithDiagnostics(ExplicitCancellationTiming timing)
    {
        var unavailable = new OperationCanceledException("unavailable optional projection");
        unavailable.Data["Strands.CleanupErrors"] = new List<Exception> { new IOException("optional projection diagnostic") };
        var model = new CounterModel(_ => throw unavailable);
        await using var agent = new Agent(new() { Model = model, ExplicitCancellationTiming = timing });
        Assert.Equal("answer", (await agent.InvokeAsync("input")).ToString());
        Assert.Equal(1, model.Counts);
        Assert.Equal(1, model.ProviderEntries);
    }

    private sealed class CounterModel(Func<CancellationToken, long> count) : IModel, IModelTokenCounter
    {
        internal int Counts, ProviderEntries;
        internal CancellationToken CountToken;
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Counts++; CountToken = cancellationToken; return new(count(cancellationToken)); }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ProviderEntries++;
            foreach (var value in Text("answer"))
            { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); }
        }
    }
}
