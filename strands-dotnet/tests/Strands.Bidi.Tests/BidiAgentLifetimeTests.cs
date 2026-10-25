using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiAgentLifetimeTests
{
    [Fact]
    public async Task CancellationAfterStopAdmissionCannotSkipClosingTheBorrowedModel()
    {
        using var cancellation = new CancellationTokenSource();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel { OnReceive = token => token.Register(cancellation.Cancel) };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.StopAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(1, model.Stops);
    }

    [Fact]
    public async Task DisposalDuringStartCannotPublishASuccessfulConversationOrLeakTheConnection()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel { StartGate = release.Task };
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var starting = agent.StartAsync(); await Within(model.StartEntered.Task);
        var disposal = agent.DisposeAsync().AsTask(); release.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await disposal; Assert.Equal(1, model.Stops); Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public async Task StopJoinsRunningToolsAndRetainsTheCompletedSiblingInItsOriginalPair()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quick = new FunctionTool(Spec("quick"), (_, _, _) => new ValueTask<string>("committed effect"));
        var slow = new FunctionTool(Spec("slow"), async (_, _, token) =>
        { entered.TrySetResult(true); try { await Task.Delay(Timeout.Infinite, token); return "unexpected"; } finally { finished.TrySetResult(true); } });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [quick, slow] });
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("a", "quick", JsonSerializer.SerializeToElement(new { })), new("b", "slow", JsonSerializer.SerializeToElement(new { }))]));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        do { Assert.True(await reader.MoveNextAsync()); } while (reader.Current is not ToolResultEvent);
        await Within(entered.Task); await agent.StopAsync(); await Within(finished.Task);
        var history = agent.Messages; Assert.Equal(2, history.Count);
        Assert.Equal(new[] { "a", "b" }, history[0].Content.Cast<ToolUseBlock>().Select(call => call.ToolUseId));
        var results = history[1].Content.Cast<ToolResultBlock>().ToArray();
        Assert.Equal("committed effect", results[0].Content); Assert.Equal(ToolResultStatus.Success, results[0].Status);
        Assert.Equal("b", results[1].ToolUseId); Assert.Equal(ToolResultStatus.Error, results[1].Status);
        Assert.False(model.Results.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposalAttemptsAllTransferredResourcesAndRetainsTheFirstFailure()
    {
        var stop = new IOException("stop primary"); var hostError = new IOException("host cleanup"); var modelError = new IOException("model cleanup");
        var host = new Agent(new() { Model = new NoCallsModel() });
        host.Hooks.Register<ShutdownEvent>((_, _) => throw hostError);
        var model = new ScriptedModel { StopError = stop, DisposeError = modelError };
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host, OwnsModel = true, OwnsToolAgent = true }); await agent.StartAsync();
        var error = await Assert.ThrowsAsync<IOException>(async () => await agent.DisposeAsync());
        Assert.Same(stop, error); Assert.Equal(1, model.Stops); Assert.Equal(1, model.Disposals);
        var cleanup = Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]);
        Assert.Contains(hostError, cleanup); Assert.Contains(modelError, cleanup);
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(async () => await agent.DisposeAsync()));
        Assert.Equal(1, model.Disposals);
    }

    private static ToolSpec Spec(string name) => new(name, "Lifetime test tool", JsonSerializer.SerializeToElement(new { type = "object" }));
}
