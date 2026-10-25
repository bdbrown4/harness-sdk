using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.BidiScheduleTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRestartConcurrencyTests
{
    [Fact]
    public async Task AnObservedToolBatchAlreadyHasItsCompletePendingHistoryPair()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FunctionTool(new("effect", "Perform an effect", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, token) =>
        { await release.Task; token.ThrowIfCancellationRequested(); return "result"; });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "effect", JsonSerializer.SerializeToElement(new { }))]));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        try
        {
            await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator();
            do { Assert.True(await Within(NextAsync())); } while (receiver.Current is not BidiToolUseBlocksEvent);
            var history = agent.Messages; Assert.Equal(2, history.Count);
            Assert.Equal("call", Assert.IsType<ToolUseBlock>(Assert.Single(history[0].Content)).ToolUseId);
            Assert.Equal("call", Assert.IsType<ToolResultBlock>(Assert.Single(history[1].Content)).ToolUseId);
            release.TrySetResult(true); await Within(model.Results.Task); await agent.StopAsync();
            async Task<bool> NextAsync() => await receiver.MoveNextAsync();
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task StoppingAnInProgressSwapCancelsAndJoinsItWithoutPublishingAnotherConnection()
    {
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { RestartGate = reconnect.Task };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); await Within(model.RestartEntered.Task);
        var completed = NextAsync(); await Within(StopAsync()); Assert.False(await Within(completed));
        reconnect.TrySetResult(true); Assert.Equal(1, model.Starts); Assert.Equal(1, model.Stops); Assert.False(model.Reconnected.Task.IsCompleted);
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
        async Task<bool> StopAsync() { await agent.StopAsync(); return true; }
    }

    [Fact]
    public async Task AFailedSwapUnblocksQueuedSendsWithTheSameFailureWithoutRecordingUnsentInput()
    {
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var failure = new IOException("restart failure");
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { RestartGate = reconnect.Task, RestartError = failure };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); await Within(model.RestartEntered.Task);
        var send = agent.SendAsync(new BidiMessage([new TextBlock("unsent")])); reconnect.TrySetResult(true);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => send)); Assert.Same(failure, await Assert.ThrowsAsync<IOException>(NextAsync));
        Assert.Empty(agent.Messages); Assert.Empty(model.Inputs); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task PartialTextIsFinalizedBeforeTheRestartHistoryIsCaptured()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        model.Current.Emit(new BidiTextStartEvent("partial")); model.Current.Emit(new BidiTextDeltaEvent("retained", "partial"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.True(await Within(NextAsync()));
        model.Current.Emit(new BidiConnectionTimeoutException("deadline")); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        await Within(model.Reconnected.Task); var replay = Assert.Single(model.Sessions[1].Messages); Assert.Equal("retained", replay.Text);
        Assert.Equal("incomplete", replay.Metadata!.Value.GetProperty("custom").GetProperty("bidi").GetProperty("status").GetString()); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task StoppingWithABackpressuredWarningJoinsTheTimerAndPreventsALateSwap()
    {
        var clock = new Clock();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        model.Current.Emit(new BidiResponseStopEvent("fills output"));
        (await Within(clock.NextAsync())).Complete(); var deadline = await Within(clock.NextAsync());
        await Within(StopAsync()); deadline.Complete(); Assert.Equal(0, model.Restarts); Assert.Equal(1, model.Starts); Assert.Equal(1, model.Stops);
        async Task<bool> StopAsync() { await agent.StopAsync(); return true; }
    }

    [Fact]
    public async Task AToolCompletedDuringTheSwapSendsItsRealResultOnceAfterReconnection()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new Dictionary<string, object?> { ["caller"] = "shared" }; var effects = 0;
        var tool = new FunctionTool(new("effect", "Perform an effect", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, context, _) =>
        { Assert.Same(state, context.InvocationState); effects++; entered.TrySetResult(true); await release.Task; return "actual result"; });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = new RestartModel { RestartGate = reconnect.Task };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync(state);
        try
        {
            model.Current.Emit(new BidiToolUseBlocksEvent([new("call", "effect", JsonSerializer.SerializeToElement(new { }))]));
            await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiToolUseBlocksEvent>(receiver.Current);
            await Within(entered.Task); model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
            Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current); await Within(model.RestartEntered.Task);
            release.TrySetResult(true); Assert.True(await Within(NextAsync())); Assert.IsType<ToolResultEvent>(receiver.Current);
            Assert.Empty(model.Inputs); reconnect.TrySetResult(true);
            var result = Assert.IsType<ToolResultBlock>(Assert.Single((await Within(model.Results.Task)).Content));
            Assert.Equal("actual result", result.Content); Assert.Equal("call", result.ToolUseId); Assert.Equal(1, effects); Assert.Single(state);
            Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>());
            Assert.Equal("actual result", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Content);
            await agent.StopAsync();
            async Task<bool> NextAsync() => await receiver.MoveNextAsync();
        }
        finally { release.TrySetResult(true); reconnect.TrySetResult(true); }
    }

    [Fact]
    public async Task AQueuedUserSendCancelsWithoutSendingOrAppendingHistoryDuringTheSwap()
    {
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { RestartGate = reconnect.Task };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        try
        {
            model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
            await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); await Within(model.RestartEntered.Task);
            using var cancellation = new CancellationTokenSource();
            var sending = agent.SendAsync(new BidiMessage([new TextBlock("unsent")]), cancellation.Token); cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending); Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Empty(model.Inputs); Assert.Empty(agent.Messages); reconnect.TrySetResult(true); await Within(model.Reconnected.Task); await agent.StopAsync();
            async Task<bool> NextAsync() => await receiver.MoveNextAsync();
        }
        finally { reconnect.TrySetResult(true); }
    }

    [Fact]
    public async Task SupersededOutputCannotMutateHistoryOrExecuteToolsOnTheNewConnection()
    {
        var reconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var clock = new Clock(); var effects = 0;
        var tool = new FunctionTool(new("effect", "Perform an effect", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { effects++; return new ValueTask<string>("unexpected"); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = new RestartModel { RestartGate = reconnect.Task, Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        try
        {
            var old = model.Current;
            (await Within(clock.NextAsync())).Complete();
            await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
            (await Within(clock.NextAsync())).Complete(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current); await Within(model.RestartEntered.Task);
            old.Emit(new BidiToolUseBlocksEvent([new("stale", "effect", JsonSerializer.SerializeToElement(new { }))]));
            reconnect.TrySetResult(true); await Within(model.Reconnected.Task);
            model.Current.Emit(new BidiTextStartEvent("fresh")); model.Current.Emit(new BidiTextDeltaEvent("fresh text", "fresh")); model.Current.Emit(new BidiTextStopEvent("fresh"));
            do { Assert.True(await Within(NextAsync())); Assert.IsNotType<BidiToolUseBlocksEvent>(receiver.Current); } while (receiver.Current is not BidiTextBlockEvent);
            Assert.Equal(0, effects); Assert.Equal("fresh text", Assert.Single(agent.Messages).Text); await agent.StopAsync();
            async Task<bool> NextAsync() => await receiver.MoveNextAsync();
        }
        finally { reconnect.TrySetResult(true); }
    }
}
