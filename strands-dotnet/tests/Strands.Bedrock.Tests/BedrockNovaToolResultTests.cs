using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockNovaToolResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task OfficialNovaWireNormalizesBothJsonResultChannelsOnlyWhenEnabled(bool streaming, bool normalization)
    {
        const string raw = """{"key": "value", "number": 42, "snow": "雪"}""";
        const string structuredRaw = """[true, null, {"other": "value"}]""";
        using var json = JsonDocument.Parse(raw);
        using var structured = JsonDocument.Parse(structuredRaw);
        var toolInput = JsonSerializer.SerializeToElement(new { query = "input stays JSON" });
        var result = new ToolResultBlock("call-nova", [new TextBlock("ordinary text"), new JsonBlock(json.RootElement)],
            ToolResultStatus.Error, structured.RootElement);
        var request = new ModelRequest([
            new Message(Role.Assistant, [new ToolUseBlock("call-nova", "lookup", toolInput)]),
            new Message(Role.User, [result])
        ], Tools: [new ToolSpec("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }))]);
        await using var server = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = "us.amazon.nova-pro-v1:0",
            Streaming = streaming,
            ConvertNovaToolResultJsonToText = normalization,
            IncludeToolResultStatus = true,
            RequestTimeout = TimeSpan.FromSeconds(15)
        });
        var answer = new StringBuilder();
        await foreach (var item in model.StreamAsync(request))
            if (item is ModelContentBlockDeltaEvent { Text: { } text }) answer.Append(text);
        await server.Completion;
        var sent = Assert.Single(server.Requests);
        var projected = sent.Body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("toolResult");
        var content = projected.GetProperty("content").EnumerateArray().ToArray();
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Phase = "b04-outbound-wire",
            Streaming = streaming,
            Normalization = normalization,
            sent.Signed,
            RequestCount = server.Requests.Length,
            sent.Target,
            Body = sent.Body,
            JsonResultBlocks = content.Count(block => block.TryGetProperty("json", out _)),
            TextResultBlocks = content.Count(block => block.TryGetProperty("text", out _))
        }));
        Assert.Equal("summary", answer.ToString());
        Assert.True(sent.Signed);
        Assert.EndsWith(streaming ? "/converse-stream" : "/converse", sent.Target);
        Assert.Equal("call-nova", projected.GetProperty("toolUseId").GetString());
        Assert.Equal("error", projected.GetProperty("status").GetString());
        Assert.Equal("input stays JSON", sent.Body.GetProperty("messages")[0].GetProperty("content")[0]
            .GetProperty("toolUse").GetProperty("input").GetProperty("query").GetString());
        Assert.Equal("lookup", sent.Body.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
        Assert.Collection(content,
            block => Assert.Equal("ordinary text", block.GetProperty("text").GetString()),
            block => CheckJson(block, raw),
            block => CheckJson(block, structuredRaw));
        Assert.Equal(raw, Assert.IsType<JsonBlock>(result.ContentBlocks[1]).Json.GetRawText());
        Assert.Equal(structuredRaw, result.StructuredContent!.Value.GetRawText());
        Assert.Equal("ordinary text", result.Content);
        void CheckJson(JsonElement block, string original)
        {
            using var expected = JsonDocument.Parse(original);
            if (normalization)
            {
                Assert.False(block.TryGetProperty("json", out _));
                Assert.True(block.TryGetProperty("text", out var nativeText));
                Assert.Equal(original, nativeText.GetString());
                using var parsed = JsonDocument.Parse(nativeText.GetString()!);
                Assert.Equal(expected.RootElement.ValueKind, parsed.RootElement.ValueKind);
            }
            else
            {
                Assert.True(block.TryGetProperty("json", out var nativeJson));
                Assert.False(block.TryGetProperty("text", out _));
                Assert.Equal(expected.RootElement.ValueKind, nativeJson.ValueKind);
                if (nativeJson.ValueKind == JsonValueKind.Object) Assert.Equal("value", nativeJson.GetProperty("key").GetString());
                else Assert.Equal(JsonValueKind.True, nativeJson[0].ValueKind);
            }
        }
    }
}
