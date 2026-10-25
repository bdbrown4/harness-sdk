using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockNovaToolResultConformanceTests
{
    [Theory]
    [InlineData("amazon.nova-pro-v1:0", true)]
    [InlineData("us.amazon.nova-lite-v1:0", true)]
    [InlineData("amazon.nova-micro-v1:0", true)]
    [InlineData("amazon.nova", true)]
    [InlineData("arn:aws:bedrock:region:account:inference-profile/amazon.nova-pro-v1:0", true)]
    [InlineData("prefix.amazon.novax", true)]
    [InlineData("AMAZON.NOVA-pro-v1:0", false)]
    [InlineData("Amazon.nova-pro-v1:0", false)]
    [InlineData("nova-pro", false)]
    [InlineData("amazon.titan-text-v1", false)]
    [InlineData("us.anthropic.claude-model", false)]
    [InlineData(" amazon.nova-pro ", true)]
    [InlineData("AMAZON.amazon.nova", true)]
    [InlineData("客户/amazon.nova-pro", true)]
    public void PinnedCaseSensitiveSubstringPreservesRawModelIdentity(string modelId, bool selected)
    {
        using var mapper = new BedrockRequestMapper();
        var json = JsonSerializer.SerializeToElement(new { value = "original" });
        var request = Request(new("call", [new JsonBlock(json)]));
        var mapped = mapper.Map(request, new() { ModelId = modelId, ConvertNovaToolResultJsonToText = true });
        var block = Assert.Single(mapped.Messages[0].Content[0].ToolResult.Content);
        Assert.Equal(modelId, mapped.ModelId);
        if (selected) Assert.Equal(json.GetRawText(), block.Text);
        else
        {
            Assert.Null(block.Text);
            Assert.Equal("original", block.Json.AsDictionary()["value"].AsString());
        }
    }

    [Theory]
    [InlineData("{\"value\": 3}")]
    [InlineData("{}")]
    [InlineData("[1, true, null, {\"nested\": [2]}]")]
    [InlineData("[]")]
    [InlineData("\"café 雪 😀\"")]
    [InlineData("\"\\u96ea\\n\\t\"")]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("-0")]
    [InlineData("9007199254740993")]
    [InlineData("1e+06")]
    [InlineData("1.2300")]
    [InlineData("{ \"duplicate\" : 1, \"duplicate\" : 2, \"雪\" : \"\\u96ea\" }")]
    public void NativeRawJsonTextPreservesLexicalNumericAndUnicodeAuthority(string raw)
    {
        using var parsed = JsonDocument.Parse(raw);
        var result = new ToolResultBlock("call", [new JsonBlock(parsed.RootElement)]);
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(Request(result), Options());
        var block = Assert.Single(mapped.Messages[0].Content[0].ToolResult.Content);
        Assert.Equal(raw, block.Text);
        using var roundtrip = JsonDocument.Parse(block.Text);
        Assert.Equal(parsed.RootElement.ValueKind, roundtrip.RootElement.ValueKind);
        Assert.Equal(raw, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RichMediaTextAndSeparateStructuredContentKeepOriginalPositions(bool structured)
    {
        var value = JsonSerializer.SerializeToElement(new { value = 7 });
        var result = new ToolResultBlock("rich", [new TextBlock("{\"already\":\"text\"}"), new JsonBlock(value),
            new ImageBlock("image/png", "AQID"), new DocumentBlock("application/pdf", "BAUG", "report"),
            new VideoBlock("video/mp4", "BwgJ"), new JsonBlock(value)], structuredContent: structured ? value : null);
        using var mapper = new BedrockRequestMapper();
        var projected = mapper.Map(Request(result), Options()).Messages[0].Content[0].ToolResult;
        Assert.Equal(structured ? 7 : 6, projected.Content.Count);
        Assert.Equal("{\"already\":\"text\"}", projected.Content[0].Text);
        Assert.Equal(value.GetRawText(), projected.Content[1].Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, projected.Content[2].Image.Source.Bytes.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, projected.Content[3].Document.Source.Bytes.ToArray());
        Assert.Equal("report", projected.Content[3].Document.Name);
        Assert.Equal(new byte[] { 7, 8, 9 }, projected.Content[4].Video.Source.Bytes.ToArray());
        Assert.Equal(value.GetRawText(), projected.Content[5].Text);
        if (structured) Assert.Equal(value.GetRawText(), projected.Content[6].Text);
        Assert.Equal(6, result.ContentBlocks.Count);
        Assert.Equal("{\"already\":\"text\"}", result.Content);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void IndependentNativeStatusPolicyIsPreserved(int policy)
    {
        var json = JsonSerializer.SerializeToElement(new { value = 3 });
        var result = new ToolResultBlock("call", [new JsonBlock(json)], ToolResultStatus.Error);
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(Request(result), Options() with { IncludeToolResultStatus = policy < 0 ? null : policy == 1 });
        var projected = mapped.Messages[0].Content[0].ToolResult;
        Assert.Equal(policy == 1 ? "error" : null, projected.Status?.Value);
        Assert.Equal("call", projected.ToolUseId);
        Assert.Equal(json.GetRawText(), Assert.Single(projected.Content).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryFallbackAndNativeToolChoiceNoneRemainIndependent(bool none)
    {
        var json = JsonSerializer.SerializeToElement(new { value = 1 });
        var request = Request(new("call", [new JsonBlock(json)])) with { ToolChoice = none ? new(ToolChoiceMode.None) : null };
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(request, Options());
        if (none) Assert.Null(mapped.ToolConfig);
        else Assert.Equal("noop", Assert.Single(mapped.ToolConfig.Tools).ToolSpec.Name);
        Assert.Equal(json.GetRawText(), Assert.Single(mapped.Messages[0].Content[0].ToolResult.Content).Text);
    }

    [Fact]
    public void DefaultAndOldOptionsJsonRetainBothOriginalJsonChannels()
    {
        var options = JsonSerializer.Deserialize<BedrockModelOptions>("""{"ModelId":"amazon.nova-pro-v1:0"}""")!;
        Assert.False(options.ConvertNovaToolResultJsonToText);
        var json = JsonSerializer.SerializeToElement(new { value = 2 });
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(Request(new("call", [new JsonBlock(json)], structuredContent: json)), options);
        Assert.Collection(mapped.Messages[0].Content[0].ToolResult.Content,
            block => Assert.Equal(2, block.Json.AsDictionary()["value"].AsInt()),
            block => Assert.Equal(2, block.Json.AsDictionary()["value"].AsInt()));
    }

    [Fact]
    public void EmptyRichContentKeepsTextFallbackBeforeStructuredProjection()
    {
        var json = JsonSerializer.SerializeToElement(new { value = 2 });
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(Request(new("call", "fallback") { StructuredContent = json }), Options());
        Assert.Collection(mapped.Messages[0].Content[0].ToolResult.Content,
            block => Assert.Equal("fallback", block.Text), block => Assert.Equal(json.GetRawText(), block.Text));
    }

    [Fact]
    public void TopLevelConversationJsonDoesNotBecomeNewNativeContent()
    {
        using var mapper = new BedrockRequestMapper();
        var error = Assert.Throws<ModelException>(() => mapper.Map(new([new(Role.User, [new JsonBlock(JsonSerializer.SerializeToElement(new { value = 1 }))])]), Options()));
        Assert.Contains("conversation content 'JsonBlock'", error.Message);
    }

    [Fact]
    public void UnsupportedRichResultContentRetainsNativeFailure()
    {
        using var mapper = new BedrockRequestMapper();
        var error = Assert.Throws<ModelException>(() => mapper.Map(Request(new("call", [new ResourceLinkBlock("https://example.invalid/resource", "resource")])), Options()));
        Assert.Contains("tool result content 'ResourceLinkBlock'", error.Message);
    }

    private static ModelRequest Request(ToolResultBlock result) => new([new(Role.User, [result])]);
    private static BedrockModelOptions Options() => new() { ModelId = "amazon.nova-pro-v1:0", ConvertNovaToolResultJsonToText = true };
}
