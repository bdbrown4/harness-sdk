using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiScheduleTests;
using static Strands.Bidi.Tests.RealtimeProtocolTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRealtimeRestartTests
{
    [Fact]
    public async Task NativeReconnectReplaysThePendingCallWithoutFabricatingItsResult()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var effects = 0; var clock = new Clock();
        var tool = new FunctionTool(new("lookup", "Look up a value", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, _, _) =>
        { effects++; entered.TrySetResult(true); await release.Task; return "real result"; });
        await using var first = new WebSocketServer(); await using var second = new WebSocketServer();
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = new OpenAIRealtimeModel(Options(first) with { Connection = new() { RestartAfter = TimeSpan.FromSeconds(60) } });
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        try
        {
            var old = await Within(first.Connection); await Within(old.ReceiveAsync());
            await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionStartEvent>(receiver.Current);
            await old.SendAsync("{\"type\":\"response.created\",\"response\":{\"id\":\"response\"}}");
            Assert.True(await Within(NextAsync())); Assert.IsType<BidiResponseStartEvent>(receiver.Current);
            await old.SendAsync("{\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\",\"status\":\"completed\",\"call_id\":\"call\",\"name\":\"lookup\",\"arguments\":\"{}\"}}");
            Assert.True(await Within(NextAsync())); Assert.IsType<BidiToolUseBlocksEvent>(receiver.Current); await Within(entered.Task);
            await old.SendAsync("{\"type\":\"response.done\",\"response\":{\"id\":\"response\",\"status\":\"completed\",\"output\":[]}}");
            Assert.True(await Within(NextAsync())); Assert.IsType<BidiResponseStopEvent>(receiver.Current);
            model.UpdateConfig(options => options with { Endpoint = second.Endpoint });
            (await Within(clock.NextAsync())).Complete(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionWarningEvent>(receiver.Current);
            (await Within(clock.NextAsync())).Complete(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
            var next = await Within(second.Connection);
            Assert.Equal("session.update", (await Within(next.ReceiveAsync())).GetProperty("type").GetString());
            var replayed = (await Within(next.ReceiveAsync())).GetProperty("item");
            Assert.Equal("function_call", replayed.GetProperty("type").GetString()); Assert.Equal("call", replayed.GetProperty("call_id").GetString());
            var anchor = (await Within(next.ReceiveAsync())).GetProperty("item");
            Assert.Equal("message", anchor.GetProperty("type").GetString()); Assert.Equal("system", anchor.GetProperty("role").GetString());
            Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionStartEvent>(receiver.Current);
            release.TrySetResult(true); Assert.True(await Within(NextAsync())); Assert.IsType<ToolResultEvent>(receiver.Current);
            var actual = (await Within(next.ReceiveAsync())).GetProperty("item");
            Assert.Equal("function_call_output", actual.GetProperty("type").GetString()); Assert.Equal("call", actual.GetProperty("call_id").GetString()); Assert.Contains("real result", actual.GetProperty("output").GetString());
            Assert.Equal("response.create", (await Within(next.ReceiveAsync())).GetProperty("type").GetString());
            Assert.Equal(1, effects); Assert.Equal("real result", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Content);
            await agent.StopAsync();
            async Task<bool> NextAsync() => await receiver.MoveNextAsync();
        }
        finally { release.TrySetResult(true); }
    }
}
