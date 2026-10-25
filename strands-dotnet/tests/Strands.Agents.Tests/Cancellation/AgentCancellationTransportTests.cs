using System.Runtime.CompilerServices;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationTransportTests
{
    [Fact]
    public async Task LogicalCancellationWaitsForPhysicalIteratorCleanupBeforeTheAfterBoundary()
    {
        var model = new CleanupModel(); var after = 0;
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { after++; Assert.Equal(1, model.Disposals); Assert.Equal(StopReasons.Cancelled, value.Result!.StopReason); return default; });
        var invocation = agent.InvokeAsync("go");
        await Wait(model.Entered.Task);
        agent.Cancel();
        try
        {
            await Wait(model.Cleaning.Task);
            Assert.False(invocation.IsCompleted);
            Assert.Equal(0, after);
        }
        finally { model.ReleaseCleanup.TrySetResult(true); }
        await Wait(invocation);
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal(1, after);
    }

    [Fact]
    public async Task IndependentIteratorCleanupFailureCannotBecomeASuccessfulCancelledResult()
    {
        var cleanup = new IOException("native iterator cleanup failed");
        var model = new DisposalFailureModel(cleanup); Exception? observed = null;
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { observed = value.Error; return default; });
        var invocation = agent.InvokeAsync("go");
        await Wait(model.Entered.Task);
        agent.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Same(error, observed);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, model.Disposals);
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Fact]
    public async Task UnrequestedProviderCancellationKeepsItsIdentityAndDoesNotPoisonReuse()
    {
        using var unrelated = new CancellationTokenSource();
        var failure = new OperationCanceledException("provider timeout", unrelated.Token);
        await using var agent = new Agent(new() { Model = new UnrequestedFailure(failure) });
        Assert.Same(failure, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeAsync("first")));
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
        Assert.Equal("fresh response", (await agent.InvokeAsync("second")).ToString());
    }

    private sealed class CleanupModel : IModel
    {
        internal readonly TaskCompletionSource<bool> Entered = Gate(), Cleaning = Gate(), ReleaseCleanup = Gate();
        internal int Disposals;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                yield return new ModelMessageStartEvent();
            }
            finally
            {
                Cleaning.TrySetResult(true);
                await ReleaseCleanup.Task.ConfigureAwait(false);
                Interlocked.Increment(ref Disposals);
            }
        }
    }

    private sealed class DisposalFailureModel(Exception cleanup) : IModel
    {
        internal readonly TaskCompletionSource<bool> Entered = Gate();
        internal int Disposals;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => new Reader(this, cleanup, cancellationToken);
        private sealed class Reader(DisposalFailureModel owner, Exception cleanup, CancellationToken token) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
        {
            public ModelStreamEvent Current => new ModelMessageStartEvent();
            public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
            public async ValueTask<bool> MoveNextAsync() { owner.Entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return false; }
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposals); throw cleanup; }
        }
    }

    private sealed class UnrequestedFailure(Exception failure) : IModel
    {
        private int _requests;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (Interlocked.Increment(ref _requests) == 1) throw failure;
            foreach (var value in Text("fresh response")) { cancellationToken.ThrowIfCancellationRequested(); yield return value; }
        }
    }
}
