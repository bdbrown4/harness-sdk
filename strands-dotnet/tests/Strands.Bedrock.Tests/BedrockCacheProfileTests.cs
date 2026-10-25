using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockCacheProfileTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, "sections")]
    [InlineData(false, "sections")]
    [InlineData(true, "ascii-auto")]
    [InlineData(false, "ascii-auto")]
    [InlineData(true, "legacy")]
    [InlineData(false, "legacy")]
    public async Task OfficialCacheProfileResolvesIndependentSectionsAndPreservesLegacyWire(bool streaming, string profile)
    {
        BedrockCacheOptions? cache = profile switch
        {
            "sections" => new() { Strategy = BedrockCacheStrategy.Anthropic, Ttl = "1h", ToolsTtl = "1h", SystemPromptTtl = "5m", Messages = false },
            "ascii-auto" => new() { Strategy = BedrockCacheStrategy.AutoIgnoreCase, Ttl = "1h" },
            _ => null
        };
        var boundary = new CachePointBlock("5m");
        var message = new Message(Role.User, [new TextBlock("stable"), boundary, new TextBlock("dynamic")]);
        var request = new ModelRequest([message], "instructions", [new ToolSpec("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }))])
        { DynamicTrailingBlocks = 1 };
        await using var server = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = profile == "sections" ? "arn:aws:bedrock:us-east-1:123:application-inference-profile/opaque-cache" : "us.ANTHROPIC.CLAUDE-test",
            Streaming = streaming,
            Cache = cache,
            CacheSystemPrompt = profile == "legacy",
            CacheTools = profile == "legacy",
            CacheLastMessage = profile == "legacy",
            CacheTtl = "1h",
            RequestTimeout = TimeSpan.FromSeconds(15)
        });
        var answer = new StringBuilder();
        await foreach (var item in model.StreamAsync(request))
            if (item is ModelContentBlockDeltaEvent { Text: { } text }) answer.Append(text);
        await server.Completion;
        var sent = Assert.Single(server.Requests);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Phase = "b06-outbound-wire",
            Streaming = streaming,
            Profile = profile,
            sent.Signed,
            RequestCount = server.Requests.Length,
            sent.Target,
            Body = sent.Body,
            Answer = answer.ToString()
        }));
        Assert.Equal("summary", answer.ToString());
        Assert.True(sent.Signed);
        Assert.EndsWith(streaming ? "/converse-stream" : "/converse", sent.Target);
        CheckPoint(sent.Body.GetProperty("system"), profile == "sections" ? "5m" : "1h");
        CheckPoint(sent.Body.GetProperty("toolConfig").GetProperty("tools"), "1h");
        var content = sent.Body.GetProperty("messages")[0].GetProperty("content");
        CheckPoint(content, profile == "sections" ? "5m" : "1h");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("dynamic", content[2].GetProperty("text").GetString());
        Assert.Equal("5m", boundary.Ttl);
        Assert.Same(boundary, message.Content[1]);
        Assert.Equal("lookup", sent.Body.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
    }

    private static void CheckPoint(JsonElement blocks, string ttl)
    {
        var block = Assert.Single(blocks.EnumerateArray(), item => item.TryGetProperty("cachePoint", out _));
        Assert.Equal("default", block.GetProperty("cachePoint").GetProperty("type").GetString());
        Assert.Equal(ttl, block.GetProperty("cachePoint").GetProperty("ttl").GetString());
    }
}
