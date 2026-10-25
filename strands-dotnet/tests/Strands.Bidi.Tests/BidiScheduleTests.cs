using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiScheduleTests
{
    [Fact]
    public async Task ADeclaredDeadlineWarnsTenSecondsBeforeTheScheduledSwap()
    {
        var clock = new Clock();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        var lead = await Within(clock.NextAsync()); Assert.Equal(TimeSpan.FromSeconds(50), lead.Duration); lead.Complete();
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<BidiConnectionWarningEvent>(receiver.Current).TimeLeft);
        var remaining = await Within(clock.NextAsync()); Assert.Equal(TimeSpan.FromSeconds(10), remaining.Duration); remaining.Complete();
        Assert.True(await Within(NextAsync())); var restart = Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        Assert.Equal(BidiRestartReason.Scheduled, restart.Reason); Assert.Null(restart.TimeoutError); Assert.False(restart.TurnInterrupted);
        await Within(model.Reconnected.Task); Assert.Equal(1, model.Restarts); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task AWarningStillPrecedesTheSwapWhenItsDeadlineElapsedBeforeReceiving()
    {
        var clock = new Clock(true);
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        (await Within(clock.NextAsync())).Complete(); (await Within(clock.NextAsync())).Complete();
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        Assert.IsType<BidiConnectionWarningEvent>(receiver.Current);
        Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        await Within(model.Reconnected.Task); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledSwapsWaitForTheTurnOrReportTheForcedInterruption(bool force)
    {
        var clock = new Clock();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        await agent.SendAsync(new BidiMessage([new TextBlock("awaiting a response")]));
        model.Current.Emit(new BidiResponseStartEvent("response"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiResponseStartEvent>(receiver.Current);
        (await Within(clock.NextAsync())).Complete(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionWarningEvent>(receiver.Current);
        (await Within(clock.NextAsync())).Complete();
        var boundary = await Within(clock.NextAsync()); Assert.Equal(TimeSpan.FromSeconds(10), boundary.Duration); Assert.Equal(0, model.Restarts);
        if (force) boundary.Complete();
        else
        {
            model.Current.Emit(new BidiResponseStopEvent("response"));
            Assert.True(await Within(NextAsync())); Assert.IsType<BidiResponseStopEvent>(receiver.Current);
        }
        Assert.True(await Within(NextAsync())); Assert.Equal(force, Assert.IsType<BidiConnectionRestartEvent>(receiver.Current).TurnInterrupted);
        await Within(model.Reconnected.Task); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonpositiveDeadlineDisablesOnlyTheProactiveTimer(int seconds)
    {
        var clock = new Clock();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(seconds) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        Assert.Equal(0, clock.Requests); model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        await Within(model.Reconnected.Task); Assert.Equal(1, model.Restarts); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    internal sealed class Clock(bool inline = false)
    {
        private readonly Generation _requests = new();
        internal int Requests;
        internal Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (duration == TimeSpan.Zero) return Task.CompletedTask;
            Interlocked.Increment(ref Requests);
            var request = new Delay(duration, token, inline); _requests.Emit(request); return request.WaitAsync();
        }
        internal async Task<Delay> NextAsync() => (Delay)(await _requests.ReadAsync(default))!;
    }
    internal sealed class Delay(TimeSpan duration, CancellationToken token, bool inline)
    {
        private readonly TaskCompletionSource<bool> _completion = new(inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan Duration { get; } = duration;
        internal void Complete() => _completion.TrySetResult(true);
        internal async Task WaitAsync()
        {
            using var registration = token.Register(() => _completion.TrySetCanceled());
            await _completion.Task;
        }
    }
}
