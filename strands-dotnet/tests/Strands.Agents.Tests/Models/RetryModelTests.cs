using System.Runtime.CompilerServices;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class RetryModelTests
{
    [Fact]
    public async Task RetriesThrottlingBeforeOutputWithoutDuplicatingUsage()
    {
        var calls = 0;
        var model = new DelegateModel((_, _) => ++calls == 1 ? Fail(new ModelThrottledException("busy")) : Success());
        var retry = new RetryModel(model, new() { Backoff = new ConstantBackoff(TimeSpan.Zero) });
        var events = await ReadAsync(retry);
        Assert.Equal(2, calls);
        Assert.Single(events.OfType<ModelMetadataEvent>());
    }

    [Fact]
    public async Task DoesNotReplayAfterAnyEventEscapes()
    {
        var calls = 0;
        var failure = new ModelThrottledException("partial");
        var retry = new RetryModel(new DelegateModel((_, _) => { calls++; return Partial(failure); }), new() { Backoff = new ConstantBackoff(TimeSpan.Zero) });
        Assert.Same(failure, await Assert.ThrowsAsync<ModelThrottledException>(() => ReadAsync(retry)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustionPreservesOriginalFailureAndExactAttemptBudget()
    {
        var calls = 0;
        var failure = new ModelThrottledException("busy");
        var retry = new RetryModel(new DelegateModel((_, _) => { calls++; return Fail(failure); }), new() { MaxAttempts = 3, Backoff = new ConstantBackoff(TimeSpan.Zero) });
        Assert.Same(failure, await Assert.ThrowsAsync<ModelThrottledException>(() => ReadAsync(retry)));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CancellationDuringBackoffPreventsNextAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var retry = new RetryModel(new DelegateModel((_, _) => { calls++; cancellation.Cancel(); return Fail(new ModelThrottledException("busy")); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(retry, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DefaultPolicyDoesNotRetryOrdinaryFailures()
    {
        var calls = 0;
        var error = new ModelException("permanent");
        var retry = new RetryModel(new DelegateModel((_, _) => { calls++; return Fail(error); }));
        Assert.Same(error, await Assert.ThrowsAsync<ModelException>(() => ReadAsync(retry)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CustomPolicyCannotRetryCancellation()
    {
        var calls = 0;
        var retry = new RetryModel(new DelegateModel((_, _) => { calls++; return Fail(new OperationCanceledException()); }), new() { IsRetryable = _ => true, Backoff = new ConstantBackoff(TimeSpan.Zero) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(retry));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetryBudgetIsIndependentForConcurrentRequests()
    {
        var calls = 0;
        var retry = new RetryModel(new DelegateModel((_, _) => { Interlocked.Increment(ref calls); return Fail(new ModelThrottledException("busy")); }), new() { MaxAttempts = 2, Backoff = new ConstantBackoff(TimeSpan.Zero) });
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => await Assert.ThrowsAsync<ModelThrottledException>(() => ReadAsync(retry))));
        Assert.Equal(4, results.Length);
        Assert.Equal(8, calls);
    }

    [Fact]
    public void RejectsInvalidBudgetsAndBackoffs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryModel(new DelegateModel((_, _) => Success()), new() { MaxAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstantBackoff(TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoff(multiplier: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinearBackoff().GetDelay(new(0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task PrimaryFailureRetainsSecondaryDisposalError()
    {
        var primary = new ModelThrottledException("primary");
        var cleanup = new InvalidOperationException("dispose");
        var retry = new RetryModel(new DelegateModel((_, _) => new BrokenEnumerator(primary, cleanup)), new() { MaxAttempts = 1 });
        Assert.Same(primary, await Assert.ThrowsAsync<ModelThrottledException>(() => ReadAsync(retry)));
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task SynchronousProviderFailureCanRetryBeforeEnumeratorExists()
    {
        var calls = 0;
        var retry = new RetryModel(new DelegateModel((_, _) => ++calls == 1 ? throw new ModelThrottledException("create") : Success()), new() { Backoff = new ConstantBackoff(TimeSpan.Zero) });
        await ReadAsync(retry);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(BackoffJitter.Full)]
    [InlineData(BackoffJitter.Equal)]
    [InlineData(BackoffJitter.Decorrelated)]
    public void JitterNeverExceedsMaximumEvenWhenBaseExceedsCap(BackoffJitter jitter)
    {
        var backoff = new ExponentialBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), jitter: jitter);
        for (var attempt = 1; attempt < 100; attempt++)
            Assert.InRange(backoff.GetDelay(new(attempt, TimeSpan.Zero, TimeSpan.FromSeconds(4))), TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(1, 4000)]
    [InlineData(2, 8000)]
    [InlineData(20, 240000)]
    public void ExponentialBackoffIsBounded(int attempt, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), new ExponentialBackoff(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(240), jitter: BackoffJitter.None).GetDelay(new(attempt, TimeSpan.Zero)));

    internal static async Task<List<ModelStreamEvent>> ReadAsync(IModel model, CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(new([Message.User("go")]), cancellationToken)) events.Add(item);
        return events;
    }

    internal sealed class DelegateModel(Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>> callback) : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => callback(request, cancellationToken);
    }

    private sealed class BrokenEnumerator(Exception primary, Exception cleanup) : IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        public ModelStreamEvent Current => throw new InvalidOperationException("No current event.");
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => new(Task.FromException<bool>(primary));
        public ValueTask DisposeAsync() => new(Task.FromException(cleanup));
    }

    internal static async IAsyncEnumerable<ModelStreamEvent> Fail(Exception failure)
    {
        await Task.Yield();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        yield break;
    }

    private static async IAsyncEnumerable<ModelStreamEvent> Partial(Exception failure)
    {
        yield return new ModelMessageStartEvent();
        await foreach (var item in Fail(failure)) yield return item;
    }

    internal static async IAsyncEnumerable<ModelStreamEvent> Success([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelMessageStartEvent();
        yield return new ModelContentBlockStartEvent(0);
        yield return new ModelContentBlockDeltaEvent(0, Text: "done");
        yield return new ModelContentBlockStopEvent(0);
        yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        yield return new ModelMetadataEvent(new(2, 3));
        await Task.Yield();
    }
}
