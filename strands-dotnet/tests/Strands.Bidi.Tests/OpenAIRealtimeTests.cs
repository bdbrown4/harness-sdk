using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class OpenAIRealtimeTests
{
    [Fact]
    public async Task NativeWebSocketAuthenticatesAndSendsThePinnedSessionAndTextInput()
    {
        await using var server = new WebSocketServer();
        await using var model = Model(server);
        await model.StartAsync(new() { SystemPrompt = "Be precise." });
        var peer = await Within(server.Connection);
        Assert.Equal("Bearer fixture-key", peer.Headers["Authorization"]);
        Assert.Contains("model=fixture-model", peer.RequestLine);
        var update = await Within(peer.ReceiveAsync());
        Assert.Equal("session.update", update.GetProperty("type").GetString());
        var session = update.GetProperty("session");
        Assert.Equal("Be precise.", session.GetProperty("instructions").GetString());
        Assert.Equal(24000, session.GetProperty("audio").GetProperty("input").GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("alloy", session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
        await model.SendAsync(new BidiMessage([new TextBlock("hello")]));
        var item = await Within(peer.ReceiveAsync());
        Assert.Equal("hello", item.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("response.create", (await Within(peer.ReceiveAsync())).GetProperty("type").GetString());
    }

    [Fact]
    public async Task FragmentedNativeTextAudioAndUsageKeepTheirIndependentBoundaries()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        var output = new List<IBidiOutputEvent>();
        var receiving = ReadResponseAsync();
        await peer.SendAsync("""{"type":"response.created","response":{"id":"r"}}""");
        var text = """{"type":"response.output_text.delta","response_id":"r","item_id":"i","content_index":0,"delta":"雪"}""";
        var cut = System.Text.Encoding.UTF8.GetByteCount(text.Substring(0, text.IndexOf('雪'))) + 1;
        await peer.SendAsync(text, cut);
        await peer.SendAsync("""{"type":"response.output_audio.delta","response_id":"r","delta":"AQID"}""");
        await peer.SendAsync("""{"type":"response.done","response":{"id":"r","status":"completed","output":[],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}""");
        await Within(receiving);
        Assert.IsType<BidiConnectionStartEvent>(output[0]);
        Assert.Equal("雪", Assert.Single(output.OfType<BidiTextDeltaEvent>()).Delta);
        Assert.Equal("AQID", Assert.Single(output.OfType<BidiAudioDeltaEvent>()).Audio);
        Assert.Single(output.OfType<BidiTextStartEvent>()); Assert.Single(output.OfType<BidiTextStopEvent>());
        Assert.Single(output.OfType<BidiAudioStartEvent>()); Assert.Single(output.OfType<BidiAudioStopEvent>());
        Assert.Equal(new Usage(3, 2, ReportedTotalTokens: 5), Assert.Single(output.OfType<BidiUsageEvent>()).Usage);

        async Task<bool> ReadResponseAsync()
        {
            await foreach (var value in model.ReceiveAsync()) { output.Add(value); if (value is BidiResponseStopEvent) return true; }
            return false;
        }
    }

    [Fact]
    public async Task AModelCanStopAndStartAgainButCannotStartTwoConnectionsAtOnce()
    {
        await using var first = new WebSocketServer(); await using var model = Model(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.SendAsync(new BidiMessage([new TextBlock("before")])));
        await model.StartAsync(); var peer = await Within(first.Connection); await Within(peer.ReceiveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.StartAsync());
        await model.StopAsync();
        await using var second = new WebSocketServer();
        model.UpdateConfig(options => options with { Endpoint = second.Endpoint });
        await model.StartAsync(); var next = await Within(second.Connection); await Within(next.ReceiveAsync());
    }

    private static OpenAIRealtimeModel Model(WebSocketServer server) => new(new()
    {
        ModelId = "fixture-model",
        TranscriptionModelId = null,
        ApiKey = "fixture-key",
        Endpoint = server.Endpoint,
        CloseTimeout = TimeSpan.FromMilliseconds(100)
    });
}
