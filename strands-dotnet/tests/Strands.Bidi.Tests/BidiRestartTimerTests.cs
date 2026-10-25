using Strands.Agents.Bidi;

namespace Strands.Bidi.Tests;

public sealed class BidiRestartTimerTests
{
    [Fact]
    public async Task WarningAndDeadlineUseTheDeclaredOffsets()
    {
        var sleeps = new List<TimeSpan>(); var warnings = new List<TimeSpan>();
        await BidiRestartTimer.RunAsync(TimeSpan.FromSeconds(420), TimeSpan.FromSeconds(30),
            (remaining, _) => { warnings.Add(remaining); return Task.CompletedTask; },
            (duration, _) => { sleeps.Add(duration); return Task.CompletedTask; }, default);
        Assert.Equal(new[] { TimeSpan.FromSeconds(390), TimeSpan.FromSeconds(30) }, sleeps);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(warnings));
    }

    [Fact]
    public async Task TheDeadlineCountsDownWhileWarningDeliveryIsBackpressured()
    {
        var warning = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sleeps = 0; var warned = false;
        var task = BidiRestartTimer.RunAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10),
            (_, _) => { warned = true; return warning.Task; }, (_, _) => ++sleeps == 1 ? Task.CompletedTask : deadline.Task, default);
        Assert.True(warned); Assert.Equal(2, sleeps); Assert.False(task.IsCompleted);
        deadline.SetResult(true); Assert.False(task.IsCompleted);
        warning.SetResult(true); await task;
    }

    [Fact]
    public async Task CancellingADormantTimerPropagatesTheExactCallerToken()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BidiRestartTimer.RunAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10),
            (_, _) => throw new InvalidOperationException("No warning after cancellation."), Task.Delay, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task WarningFailureJoinsAndCancelsTheOutstandingDeadline()
    {
        var failure = new IOException("warning failure"); var calls = 0;
        var joined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = await Assert.ThrowsAsync<IOException>(() => BidiRestartTimer.RunAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10),
            (_, _) => throw failure, Delay, default));
        Assert.Same(failure, error); Assert.True(joined.Task.IsCompleted);
        async Task Delay(TimeSpan duration, CancellationToken token)
        {
            if (++calls == 1) return;
            try { await Task.Delay(Timeout.Infinite, token); } finally { joined.TrySetResult(true); }
        }
    }
}
