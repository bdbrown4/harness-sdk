using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using static Strands.Bidi.Tests.GoogleLiveTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class GoogleLiveAdversarialTests
{
    [Fact]
    public async Task NativeStructuredSchemaConvertsTypeEnumsWithoutChangingPropertyNamesOrValues()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"type":{"type":"string","enum":["object"]},"rows":{"type":"array","items":{"type":"number"}}},"required":["type"]}""");
        var starting = model.StartAsync(new() { Tools = [new ToolSpec("structured", "Structured", schema.RootElement)] });
        var peer = await Within(server.Connection); var parameters = (await Within(peer.ReceiveAsync())).GetProperty("setup").GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parameters");
        try
        {
            Assert.Equal("OBJECT", parameters.GetProperty("type").GetString());
            var properties = parameters.GetProperty("properties"); Assert.Equal("STRING", properties.GetProperty("type").GetProperty("type").GetString());
            Assert.Equal("object", properties.GetProperty("type").GetProperty("enum")[0].GetString()); Assert.Equal("NUMBER", properties.GetProperty("rows").GetProperty("items").GetProperty("type").GetString());
        }
        finally { await peer.SendAsync("""{"setupComplete":{}}"""); await starting; }
    }

    [Fact]
    public async Task AnInvalidCombinedMessageCannotReserveAnUnpublishedToolCall()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        await using (var reader = model.ReceiveAsync().GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            await peer.SendAsync("""{"toolCall":{"functionCalls":[{"id":"c","name":"n","args":{}}]},"usageMetadata":{"totalTokenCount":-1}}""");
            await Assert.ThrowsAsync<ModelException>(async () => await reader.MoveNextAsync());
        }
        await using var next = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await next.MoveNextAsync());
        await peer.SendAsync("""{"toolCall":{"functionCalls":[{"id":"c","name":"n","args":{}}]}}""");
        Assert.True(await next.MoveNextAsync()); Assert.IsType<BidiResponseStartEvent>(next.Current);
        Assert.True(await next.MoveNextAsync()); Assert.Equal("c", Assert.Single(Assert.IsType<BidiToolUseBlocksEvent>(next.Current).ToolUses).ToolUseId);
    }

    [Fact]
    public async Task OutputAudioCannotBeRelabeledAsADifferentSampleRate()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=48000","data":"AAA="}}]}}}""");
        await Assert.ThrowsAsync<ModelException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task SetupWireConfigRecursivelyMergesVoiceParamsAndOwnsToolSchemas()
    {
        await using var server = new WebSocketServer();
        using var extra = JsonDocument.Parse("""{"generationConfig":{"responseModalities":["TEXT"],"speechConfig":{"languageCode":"en-US"}},"inputAudioTranscription":null}""");
        await using var model = new GoogleGeminiLiveModel(new() { ModelId = "models/m", ApiKey = "key", Voice = "Aoede", Params = extra.RootElement, Endpoint = server.Endpoint }); extra.Dispose();
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"n":{"type":"integer"}}}""");
        var starting = model.StartAsync(new() { Tools = [new ToolSpec("calc", "Calculate", schema.RootElement)] });
        schema.Dispose(); var peer = await Within(server.Connection); var setup = (await Within(peer.ReceiveAsync())).GetProperty("setup");
        var generation = setup.GetProperty("generationConfig"); Assert.Equal("TEXT", generation.GetProperty("responseModalities")[0].GetString());
        var speech = generation.GetProperty("speechConfig"); Assert.Equal("Aoede", speech.GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        Assert.Equal("en-US", speech.GetProperty("languageCode").GetString()); Assert.Equal(JsonValueKind.Null, setup.GetProperty("inputAudioTranscription").ValueKind);
        Assert.Equal("INTEGER", setup.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parameters").GetProperty("properties").GetProperty("n").GetProperty("type").GetString());
        await peer.SendAsync("""{"setupComplete":{}}"""); await starting;
    }

    [Fact]
    public async Task ARejectedToolResultBatchDoesNotSendOrConsumeItsValidSibling()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("""{"toolCall":{"functionCalls":[{"id":"a","name":"one","args":{}},{"id":"b","name":"two","args":{}}]}}""");
        while (await reader.MoveNextAsync()) if (reader.Current is BidiToolUseBlocksEvent) break;
        await Assert.ThrowsAsync<ArgumentException>(() => model.SendAsync(new BidiMessage([new ToolResultBlock("a", "ok"), new ToolResultBlock("missing", "bad")])));
        await model.SendAsync(new BidiMessage([new ToolResultBlock("a", "first"), new ToolResultBlock("b", "second")]));
        var responses = (await Within(peer.ReceiveAsync())).GetProperty("toolResponse").GetProperty("functionResponses"); Assert.Equal(2, responses.GetArrayLength());
        Assert.Equal("first", responses[0].GetProperty("response").GetProperty("text").GetString()); Assert.Equal("two", responses[1].GetProperty("name").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => model.SendAsync(new BidiMessage([new ToolResultBlock("a", "again")])));
    }

    [Fact]
    public async Task EventBudgetsApplyBeforeWritesAndDuringFragmentedReads()
    {
        await using var server = new WebSocketServer(); await using var model = new GoogleGeminiLiveModel(new() { ModelId = "m", ApiKey = "k", Endpoint = server.Endpoint, MaximumEventBytes = 600 }); var peer = await StartAsync(model, server);
        await Assert.ThrowsAsync<ModelException>(() => model.SendAsync(new BidiMessage([new TextBlock(new string('x', 700))])));
        await model.SendAsync(new BidiMessage([new TextBlock("valid")])); Assert.Equal("valid", (await Within(peer.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        await using var reader = model.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        await peer.SendAsync("{\"unknown\":\"" + new string('x', 700) + "\"}", 300); await Assert.ThrowsAsync<ModelException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task InlineImagesUseTheNativePartAndRemoteImageSourcesAreRejected()
    {
        await using var server = new WebSocketServer(); await using var model = Model(server); var peer = await StartAsync(model, server);
        await model.SendAsync(new BidiMessage([new ImageBlock("image/png", "AQI=")]));
        var part = (await Within(peer.ReceiveAsync())).GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("inlineData"); Assert.Equal("image/png", part.GetProperty("mimeType").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => model.SendAsync(new BidiMessage([new ImageBlock("image/png", new S3Location("s3://bucket/key"))])));
    }
}
