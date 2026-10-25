using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRunRealtimeTests
{
    [Fact]
    public async Task NativeRealtimeRunUsesRealLoopbackWebSocketsAndJoinsCancellationCleanup()
    {
        using var cancellation = new CancellationTokenSource(); var inputs = 0; var stopped = 0; var completed = 0;
        await using var server = new WebSocketServer();
        await using var model = new OpenAIRealtimeModel(new() { ModelId = "offline", TranscriptionModelId = null, ApiKey = "offline-key", Endpoint = server.Endpoint });
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new BidiInputStream(async token =>
        {
            if (Interlocked.Increment(ref inputs) == 1) return new BidiMessage([new TextBlock("hello")]);
            await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable");
        }, stop: token => { Assert.False(token.CanBeCanceled); stopped++; return default; });
        var output = new BidiOutputStream((value, _) => { if (value is BidiResponseStopEvent) { completed++; cancellation.Cancel(); } return default; });
        var run = agent.RunAsync([input], [output], cancellationToken: cancellation.Token);
        try
        {
            var peer = await Within(server.Connection);
            Assert.Equal("session.update", (await Within(peer.ReceiveAsync())).GetProperty("type").GetString());
            Assert.Equal("hello", (await Within(peer.ReceiveAsync())).GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("response.create", (await Within(peer.ReceiveAsync())).GetProperty("type").GetString());
            await peer.SendAsync("""{"type":"response.created","response":{"id":"run"}}""");
            await peer.SendAsync("""{"type":"response.done","response":{"id":"run","status":"completed","output":[]}}""");
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(1, completed); Assert.Equal(1, stopped); Assert.Equal("hello", Assert.Single(agent.Messages).Text);
        }
        finally { cancellation.Cancel(); try { await run; } catch (OperationCanceledException) { } }
    }
}
