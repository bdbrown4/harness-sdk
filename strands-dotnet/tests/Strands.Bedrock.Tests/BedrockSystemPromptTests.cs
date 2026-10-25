using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockSystemPromptTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task OfficialTypedSystemKeepsOrderedCallerBoundaryAndLegacyStringWire(bool streaming, bool structured)
    {
        var point = new CachePointBlock("caller-owned");
        var content = new List<ContentBlock> { new TextBlock("first 雪"), point, new TextBlock("second") };
        var request = new ModelRequest([Message.User("question")], structured ? "ignored legacy text" : "legacy instructions",
            [new("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }))])
        { SystemPromptContent = structured ? content : null };
        await using var wire = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = "opaque-model",
            Streaming = streaming,
            RequestTimeout = TimeSpan.FromSeconds(15),
            Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h", ToolsTtl = "5m", SystemPromptTtl = "1h" }
        });
        var answer = new StringBuilder();
        await foreach (var value in model.StreamAsync(request)) if (value is ModelContentBlockDeltaEvent { Text: { } text }) answer.Append(text);
        await wire.Completion;
        var sent = Assert.Single(wire.Requests);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Phase = "b07-minimum-wire",
            Streaming = streaming,
            Structured = structured,
            sent.Signed,
            sent.Target,
            sent.Body,
            RequestCount = wire.Requests.Length,
            Answer = answer.ToString()
        }));
        Assert.True(sent.Signed);
        Assert.Equal("summary", answer.ToString());
        Assert.EndsWith(streaming ? "/converse-stream" : "/converse", sent.Target);
        var system = sent.Body.GetProperty("system");
        Assert.Equal(structured ? 3 : 2, system.GetArrayLength());
        Assert.Equal(structured ? "first 雪" : "legacy instructions", system[0].GetProperty("text").GetString());
        var checkpoint = system[1].GetProperty("cachePoint");
        Assert.Equal("default", checkpoint.GetProperty("type").GetString());
        Assert.Equal(structured ? "caller-owned" : "1h", checkpoint.GetProperty("ttl").GetString());
        if (structured)
        {
            Assert.Equal("second", system[2].GetProperty("text").GetString());
            Assert.Equal("first 雪\nsecond", request.SystemPrompt);
            Assert.Same(point, content[1]);
            Assert.Equal("caller-owned", point.Ttl);
        }
        else Assert.Null(request.SystemPromptContent);
        Assert.Equal("5m", sent.Body.GetProperty("toolConfig").GetProperty("tools")[1].GetProperty("cachePoint").GetProperty("ttl").GetString());
    }
}
