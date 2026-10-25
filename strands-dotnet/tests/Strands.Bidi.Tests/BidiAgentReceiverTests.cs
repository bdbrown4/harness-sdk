using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiAgentReceiverTests
{
    [Fact]
    public async Task SequentialReceiversOnACompletedConversationEndWithoutWaitingForNewEvents()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        await foreach (var item in agent.ReceiveAsync()) { }
        using var safety = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var count = 0;
        for (var attempt = 0; attempt < 3; attempt++)
            await foreach (var item in agent.ReceiveAsync(safety.Token)) count++;
        Assert.Equal(0, count); await agent.StopAsync();
    }

    [Fact]
    public async Task ACancelingToolCannotQueueANewConversationBehindStop()
    {
        BidiAgent? agent = null; Task? restarting = null;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FunctionTool(new("wait", "Wait until stopped", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, token) =>
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var callback = token.Register(() => { restarting = agent!.StartAsync(); cancelled.TrySetResult(true); }); entered.TrySetResult(true);
            // Await this callback itself: Framework may inline a Task.Delay cancellation continuation,
            // whose using cleanup could otherwise unregister our callback before it is invoked.
            await cancelled.Task; token.ThrowIfCancellationRequested(); return "unexpected";
        });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "wait", JsonSerializer.SerializeToElement(new { }))]));
        await using (agent = new BidiAgent(new() { Model = model, ToolAgent = host }))
        {
            await agent.StartAsync();
            await using var reader = agent.ReceiveAsync().GetAsyncEnumerator(); await reader.MoveNextAsync(); await reader.MoveNextAsync();
            await Within(entered.Task); await agent.StopAsync(); Assert.NotNull(restarting);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarting);
            Assert.Equal(1, model.Starts); Assert.Equal(1, model.Stops);
        }
    }
}
