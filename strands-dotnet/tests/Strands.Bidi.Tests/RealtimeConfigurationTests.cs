using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.RealtimeProtocolTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class RealtimeConfigurationTests
{
    [Fact]
    public async Task ParamsOverrideRecursivelyAndPublishedJsonIsDetached()
    {
        await using var server = new WebSocketServer();
        OpenAIRealtimeModel model;
        using (var parameters = JsonDocument.Parse("""{"instructions":"native override","audio":{"output":{"voice":"coral"},"input":{"transcription":null}}}"""))
            model = new(Options(server) with { Voice = "alloy", Params = parameters.RootElement });
        await using (model)
        {
            await model.StartAsync(new() { SystemPrompt = "direct" }); var peer = await Within(server.Connection);
            var session = (await Within(peer.ReceiveAsync())).GetProperty("session");
            Assert.Equal("native override", session.GetProperty("instructions").GetString());
            Assert.Equal("coral", session.GetProperty("audio").GetProperty("output").GetProperty("voice").GetString());
            Assert.Equal(24000, session.GetProperty("audio").GetProperty("output").GetProperty("format").GetProperty("rate").GetInt32());
            Assert.Equal(JsonValueKind.Null, session.GetProperty("audio").GetProperty("input").GetProperty("transcription").ValueKind);
            Assert.Equal("native override", model.GetConfig().Params!.Value.GetProperty("instructions").GetString());
        }
    }

    [Theory]
    [InlineData("{\"audio\":{\"input\":{\"format\":{\"rate\":16000}}}}")]
    [InlineData("{\"audio\":{\"output\":{\"format\":{\"type\":\"audio/pcmu\"}}}}")]
    [InlineData("{\"audio\":{\"input\":{\"turn_detection\":null}}}")]
    [InlineData("{\"audio\":{\"input\":{\"turn_detection\":{\"create_response\":false}}}}")]
    [InlineData("{\"audio\":{\"input\":{\"turn_detection\":{\"interrupt_response\":false}}}}")]
    public async Task InvalidConfigurationUpdateLeavesThePriorModelUsable(string json)
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        using var parameters = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { Params = parameters.RootElement }));
        Assert.Null(model.GetConfig().Params);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
    }

    [Fact]
    public async Task InputMessagesAndAudioOwnTheirData()
    {
        var content = new List<ContentBlock> { new TextBlock("original") };
        var message = new BidiMessage(content); content[0] = new TextBlock("changed");
        var bytes = new byte[] { 1, 2 }; var audio = new BidiAudioDelta(bytes); bytes[0] = 9;
        var exported = audio.GetBytes(); exported[0] = 7;
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await model.SendAsync(message); var item = await Within(peer.ReceiveAsync()); await Within(peer.ReceiveAsync());
        Assert.Equal("original", item.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
        await model.SendAsync(audio);
        Assert.Equal("AQI=", (await Within(peer.ReceiveAsync())).GetProperty("audio").GetString());
    }

    [Fact]
    public async Task AnOversizedLaterToolResultCannotPartiallyTransmitTheBatch()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server, 1024);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await Assert.ThrowsAsync<ModelException>(() => model.SendAsync(new BidiMessage([new ToolResultBlock("first", "ok"), new ToolResultBlock("second", new string('x', 2000))])));
        await model.SendAsync(new BidiMessage([new TextBlock("valid")]));
        var item = await Within(peer.ReceiveAsync());
        Assert.Equal("message", item.GetProperty("item").GetProperty("type").GetString());
    }

    [Fact]
    public async Task InvalidRestartHistoryDoesNotDestroyTheHealthyConnection()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        await model.StartAsync(); var peer = await Within(server.Connection); await Within(peer.ReceiveAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => model.RestartAsync(new() { Messages = [new(Role.User, [new ImageBlock("image/png", new Uri("https://example.org/image"))])] }));
        await model.SendAsync(new BidiMessage([new TextBlock("still connected")]));
        Assert.Equal("still connected", (await Within(peer.ReceiveAsync())).GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString());
    }
}
