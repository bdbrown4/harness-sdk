using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockSystemPromptGuardMinimumTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OfficialStreamKeepsOrderedTextGuardQualifiersAndCachePoint()
    {
        var request = new ModelRequest([Message.User("question")])
        {
            SystemPromptContent = [new TextBlock("instructions"), new GuardContentBlock("selected", qualifiers: [GuardQualifier.Query, GuardQualifier.GroundingSource]), new CachePointBlock("caller")]
        };
        var body = await SendAsync(request, "stream");
        var system = body.GetProperty("system");
        Assert.Equal(3, system.GetArrayLength());
        Assert.Equal("instructions", system[0].GetProperty("text").GetString());
        var text = system[1].GetProperty("guardContent").GetProperty("text");
        Assert.Equal("selected", text.GetProperty("text").GetString());
        Assert.Equal(new[] { "query", "grounding_source" }, text.GetProperty("qualifiers").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("caller", system[2].GetProperty("cachePoint").GetProperty("ttl").GetString());
        Assert.Equal("instructions", request.SystemPrompt);
    }

    [Fact]
    public async Task OfficialNonstreamMapsInlineGuardImage()
    {
        var request = new ModelRequest([Message.User("question")])
        {
            SystemPromptContent = [new GuardContentBlock(new ImageBlock("image/png", "AAEC"))]
        };
        var body = await SendAsync(request, "nonstream");
        var image = Assert.Single(body.GetProperty("system").EnumerateArray()).GetProperty("guardContent").GetProperty("image");
        Assert.Equal("png", image.GetProperty("format").GetString());
        Assert.Equal("AAEC", image.GetProperty("source").GetProperty("bytes").GetString());
        Assert.Null(request.SystemPrompt);
    }

    [Fact]
    public async Task OfficialCountTokensUsesTheSystemGuardGraph()
    {
        var request = new ModelRequest([Message.User("question")])
        {
            SystemPromptContent = [new GuardContentBlock("selected count", qualifiers: [GuardQualifier.GuardContent])]
        };
        var body = await SendAsync(request, "count");
        var text = Assert.Single(body.GetProperty("input").GetProperty("converse").GetProperty("system").EnumerateArray()).GetProperty("guardContent").GetProperty("text");
        Assert.Equal("selected count", text.GetProperty("text").GetString());
        Assert.Equal("guard_content", Assert.Single(text.GetProperty("qualifiers").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task LegacyStringStreamWireAndLocalEstimateRemainControls()
    {
        var request = new ModelRequest([Message.User("question")], "legacy instructions");
        var body = await SendAsync(request, "stream");
        Assert.Equal("legacy instructions", Assert.Single(body.GetProperty("system").EnumerateArray()).GetProperty("text").GetString());
        await using var unusedWire = new BedrockRecoveryWireFixture();
        using var client = unusedWire.CreateClient();
        using var model = new BedrockModel(client, Options("local"));
        Assert.Equal(("legacy instructions".Length + 3L) / 4 + ("question".Length + 3L) / 4, await model.CountTokensAsync(request));
        await unusedWire.Completion;
        Assert.Empty(unusedWire.Requests);
        Assert.Null(request.SystemPromptContent);
    }

    private async Task<JsonElement> SendAsync(ModelRequest request, string operation)
    {
        await using var wire = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(operation));
        if (operation == "count") Assert.Equal(9, await model.CountTokensAsync(request));
        else
        {
            var text = new StringBuilder();
            await foreach (var item in model.StreamAsync(request))
                if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
            Assert.Equal("summary", text.ToString());
        }
        await wire.Completion;
        var sent = Assert.Single(wire.Requests);
        Assert.True(sent.Signed);
        Assert.EndsWith(operation switch { "stream" => "/converse-stream", "count" => "/count-tokens", _ => "/converse" }, sent.Target);
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "b07-stage3-minimum-wire", Operation = operation, sent.Target, sent.Signed, sent.Body }));
        return sent.Body.Clone();
    }

    private static BedrockModelOptions Options(string operation) => new()
    {
        ModelId = "opaque-model",
        Streaming = operation == "stream",
        UseNativeTokenCount = operation == "count",
        TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw,
        RequestTimeout = TimeSpan.FromSeconds(15)
    };
}
