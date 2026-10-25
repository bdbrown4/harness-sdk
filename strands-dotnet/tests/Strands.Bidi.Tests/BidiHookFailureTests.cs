using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.BidiScheduleTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiHookFailureTests
{
    [Fact]
    public async Task ScheduledHooksUseTheScheduledReasonWithoutInventingATimeout()
    {
        var clock = new Clock(); var seen = new List<string>();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync);
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>((value, _) => { Assert.Equal(BidiRestartReason.Scheduled, value.Reason); Assert.Null(value.TimeoutError); seen.Add("before"); return default; });
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>((value, _) => { Assert.Equal(BidiRestartReason.Scheduled, value.Reason); Assert.Null(value.Error); seen.Add("after"); return default; });
        await agent.StartAsync(); (await Within(clock.NextAsync())).Complete();
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        (await Within(clock.NextAsync())).Complete(); Assert.True(await Within(NextAsync())); await Within(model.Reconnected.Task);
        model.Current.Close(); Assert.False(await Within(NextAsync())); Assert.Equal(new[] { "before", "after" }, seen); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task ABlockedBeforeFailureReleasesAnAlreadyQueuedUserSend()
    {
        var entered = Signal(); var release = Signal(); var failure = new IOException("before failed");
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>(async (_, _) => { entered.TrySetResult(true); await release.Task; throw failure; });
        await agent.StartAsync(); model.Current.Emit(new BidiConnectionTimeoutException("timeout"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); await Within(entered.Task);
        var sending = agent.SendAsync(new BidiMessage([new TextBlock("must not escape")])); Assert.False(sending.IsCompleted);
        release.TrySetResult(true);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(NextAsync)); Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => sending));
        Assert.Empty(model.Inputs); Assert.Equal(0, model.Restarts); Assert.Empty(agent.Messages); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task RestartCallbacksCannotAwaitTheirOwnSendOrLifecycle()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); var callbacks = 0;
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>(async (_, _) => await CheckAsync());
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>(async (_, _) => await CheckAsync());
        await agent.StartAsync(); model.Current.Emit(new BidiConnectionTimeoutException("timeout"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); await Within(model.Reconnected.Task);
        model.Current.Close(); Assert.False(await Within(NextAsync())); Assert.Equal(2, callbacks); await agent.StopAsync();
        async Task CheckAsync()
        {
            callbacks++;
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.SendAsync(new BidiMessage([new TextBlock("self-send")])));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StopAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StartAsync());
            Assert.Throws<InvalidOperationException>(() => { agent.DisposeAsync(); });
        }
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task AHookCannotAwaitTheOutputItsOwnCallbackMustProduce()
    {
        var entered = Signal(); var attempted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiResponseStopHookEvent>(async (_, token) =>
        {
            entered.TrySetResult(true);
            try { await using var ownOutput = agent.ReceiveAsync(token).GetAsyncEnumerator(token); await ownOutput.MoveNextAsync(); attempted.TrySetResult(null); }
            catch (Exception error) { attempted.TrySetResult(error); }
        });
        await agent.StartAsync(); model.Current.Emit(new BidiResponseStopEvent("response")); await Within(entered.Task);
        await agent.StopAsync();
        Assert.IsType<InvalidOperationException>(await Within(attempted.Task));
    }

    [Fact]
    public async Task CancelingABeforeHookStopsTheConversationWithoutAttemptingTheSwap()
    {
        var entered = Signal(); var canceled = Signal(); var after = 0; var stopped = 0;
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>(async (_, token) =>
        { entered.TrySetResult(true); using var registration = token.Register(() => canceled.TrySetResult(true)); await canceled.Task; token.ThrowIfCancellationRequested(); });
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>((_, _) => { after++; return default; });
        agent.Hooks.Register<BidiAgentStopEvent>((_, token) => { Assert.False(token.CanBeCanceled); stopped++; return default; });
        await agent.StartAsync(); model.Current.Emit(new BidiConnectionTimeoutException("timeout"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); await Within(entered.Task);
        await agent.StopAsync(); Assert.True(canceled.Task.IsCompleted); Assert.Equal(0, model.Restarts); Assert.Equal(0, after); Assert.Equal(1, stopped);
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task OrdinaryHookFailureStopsLaterCallbacksAndRemainsTheReceiveFailure()
    {
        var failure = new IOException("response hook"); var later = 0;
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiResponseStopHookEvent>((_, _) => throw failure, -1);
        agent.Hooks.Register<BidiResponseStopHookEvent>((_, _) => { later++; return default; });
        await agent.StartAsync(); model.Current.Emit(new BidiResponseStopEvent("response"));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in agent.ReceiveAsync()) { } }));
        Assert.Equal(0, later); await agent.StopAsync();
    }

    [Fact]
    public async Task ASuccessfulSwapDoesNotReleaseSendsUntilItsAfterHookFinishes()
    {
        var entered = Signal(); var release = Signal(); var failure = new IOException("after failed");
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>(async (value, _) => { Assert.Null(value.Error); entered.TrySetResult(true); await release.Task; throw failure; });
        await agent.StartAsync(); model.Current.Emit(new BidiConnectionTimeoutException("timeout"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); await Within(entered.Task);
        var sending = agent.SendAsync(new BidiMessage([new TextBlock("queued")])); Assert.False(sending.IsCompleted); release.TrySetResult(true);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(NextAsync)); Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => sending));
        Assert.Equal(1, model.Restarts); Assert.Empty(model.Inputs); Assert.Empty(agent.Messages); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
