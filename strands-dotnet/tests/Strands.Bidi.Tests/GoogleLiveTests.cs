using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveTests
{
    [Fact]
    public async Task SetupWaitsForAcknowledgementAndSeedsTextHistoryBeforeLiveInput()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var starting = model.StartAsync(new() { SystemPrompt = "Be precise.", Messages = [Message.User("earlier")] });
        var peer = await Within(server.Connection);
        Assert.Contains("key=fixture-key", peer.RequestLine);
        var setup = (await Within(peer.ReceiveAsync())).GetProperty("setup");
        Assert.Equal("models/live-fixture", setup.GetProperty("model").GetString());
        Assert.Equal("AUDIO", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.Equal("Be precise.", setup.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.True(setup.GetProperty("historyConfig").GetProperty("initialHistoryInClientContent").GetBoolean());
        Assert.False(starting.IsCompleted);
        await peer.SendAsync("""{"setupComplete":{}}"""); await starting;
        var seeded = (await Within(peer.ReceiveAsync())).GetProperty("clientContent");
        Assert.Equal("earlier", seeded.GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.True(seeded.GetProperty("turnComplete").GetBoolean());
        await model.SendAsync(new BidiMessage([new TextBlock("hello")]));
        Assert.Equal("hello", (await Within(peer.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task NativeFragmentedReasoningTextAudioAndUsageCloseAtActualTurnCompletion()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        var peer = await StartAsync(model, server);
        var values = new List<IBidiOutputEvent>(); var reading = ReadAsync();
        await peer.SendAsync("""{"serverContent":{"modelTurn":{"parts":[{"text":"think","thought":true},{"text":"雪"},{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AQID"}}]}},"usageMetadata":{"promptTokenCount":3,"responseTokenCount":2,"totalTokenCount":5}}""", 51);
        await peer.SendAsync("""{"serverContent":{"generationComplete":true}}""");
        await peer.SendAsync("""{"serverContent":{"turnComplete":true}}""");
        await Within(reading);
        Assert.Equal("think", Assert.Single(values.OfType<BidiReasoningDeltaEvent>()).Delta);
        Assert.Equal("雪", Assert.Single(values.OfType<BidiTextDeltaEvent>()).Delta);
        Assert.Equal("AQID", Assert.Single(values.OfType<BidiAudioDeltaEvent>()).Audio);
        Assert.Single(values.OfType<BidiAudioStopEvent>()); Assert.Single(values.OfType<BidiResponseStopEvent>());
        Assert.Equal(new Usage(3, 2, ReportedTotalTokens: 5), Assert.Single(values.OfType<BidiUsageEvent>()).Usage);
        async Task<bool> ReadAsync() { await foreach (var value in model.ReceiveAsync()) { values.Add(value); if (value is BidiResponseStopEvent) return true; } return false; }
    }

    [Fact]
    public async Task NativeToolBatchKeepsNamesAndJsonResultWhilePcmUsesConfiguredInputRate()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server, 48000);
        var peer = await StartAsync(model, server);
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("""{"toolCall":{"functionCalls":[{"id":"call","name":"calculate","args":{"n":2}}]}}""");
        BidiToolUseBlocksEvent? calls = null;
        while (calls is null && await reader.MoveNextAsync()) calls = reader.Current as BidiToolUseBlocksEvent;
        Assert.Equal("calculate", Assert.Single(calls!.ToolUses).Name);
        await model.SendAsync(new BidiMessage([new ToolResultBlock("call", [new JsonBlock(JsonSerializer.SerializeToElement(new { answer = 5 }))])]));
        var response = (await Within(peer.ReceiveAsync())).GetProperty("toolResponse").GetProperty("functionResponses")[0];
        Assert.Equal("call", response.GetProperty("id").GetString()); Assert.Equal("calculate", response.GetProperty("name").GetString());
        Assert.Equal(5, response.GetProperty("response").GetProperty("json").GetProperty("answer").GetInt32());
        await model.SendAsync(new BidiAudioDelta([1, 2]));
        var audio = (await Within(peer.ReceiveAsync())).GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=48000", audio.GetProperty("mimeType").GetString()); Assert.Equal("AQI=", audio.GetProperty("data").GetString());
    }

    internal static GoogleGeminiLiveModel Model(WebSocketServer server, int rate = 16000) => new(new() { ModelId = "live-fixture", ApiKey = "fixture-key", Endpoint = server.Endpoint, InputSampleRate = rate, CloseTimeout = TimeSpan.FromMilliseconds(100) });
    internal static async Task<Peer> StartAsync(GoogleGeminiLiveModel model, WebSocketServer server)
    {
        var starting = model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await peer.SendAsync("""{"setupComplete":{}}"""); await starting; return peer;
    }
}
