using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockExplicitEmptyToolResultTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("stream", false)]
    [InlineData("nonstream", false)]
    [InlineData("count", false)]
    [InlineData("stream", true)]
    [InlineData("nonstream", true)]
    [InlineData("count", true)]
    public async Task ExplicitEmptyContentDoesNotInventDisplayText(string operation, bool error)
    {
        var result = new Strands.Agents.ToolResultBlock("call", "DISPLAY_MUST_NOT_BECOME_WIRE", error ? ToolResultStatus.Error : ToolResultStatus.Success)
        { HasExplicitContent = true, Metadata = JsonSerializer.SerializeToElement(new { localOnlyMetadata = true }) };

        var sent = await SendAsync(result, operation);

        Assert.Empty(sent.GetProperty("content").EnumerateArray());
        Assert.Equal("call", sent.GetProperty("toolUseId").GetString());
        Assert.Equal(error ? "error" : "success", sent.GetProperty("status").GetString());
        Assert.DoesNotContain("DISPLAY_MUST_NOT_BECOME_WIRE", sent.GetRawText());
        Assert.DoesNotContain("localOnlyMetadata", sent.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFallbackRetainsItsTextAndStatus(bool error)
    {
        var result = new Strands.Agents.ToolResultBlock("call", "legacy-display", error ? ToolResultStatus.Error : ToolResultStatus.Success);

        var sent = await SendAsync(result, "stream");

        Assert.Equal("legacy-display", Assert.Single(sent.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(error ? "error" : "success", sent.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("stream", "null")]
    [InlineData("nonstream", "null")]
    [InlineData("count", "null")]
    [InlineData("stream", "{\"value\":7}")]
    [InlineData("nonstream", "{\"value\":7}")]
    [InlineData("count", "{\"value\":7}")]
    public async Task ExplicitEmptyContentRetainsIndependentStructuredJson(string operation, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Strands.Agents.ToolResultBlock("call", "DISPLAY_MUST_NOT_BECOME_WIRE")
        { HasExplicitContent = true, StructuredContent = document.RootElement.Clone() };

        var sent = await SendAsync(result, operation);

        var content = Assert.Single(sent.GetProperty("content").EnumerateArray());
        Assert.True(JsonElement.DeepEquals(document.RootElement, content.GetProperty("json")));
        Assert.DoesNotContain("DISPLAY_MUST_NOT_BECOME_WIRE", sent.GetRawText());
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("nonstream")]
    [InlineData("count")]
    public async Task ExplicitNonemptyContentRetainsItsOrderedRichValues(string operation)
    {
        var result = new Strands.Agents.ToolResultBlock("call", "DISPLAY_MUST_NOT_BECOME_WIRE")
        { HasExplicitContent = true, ContentBlocks = [new TextBlock("first\n\u03b1"), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 7 }))] };

        var sent = await SendAsync(result, operation);

        var content = sent.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("first\n\u03b1", content[0].GetProperty("text").GetString());
        Assert.Equal(7, content[1].GetProperty("json").GetProperty("value").GetInt32());
        Assert.DoesNotContain("DISPLAY_MUST_NOT_BECOME_WIRE", sent.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitEmptyContentKeepsTheExistingNovaJsonProjection(bool convert)
    {
        var result = new Strands.Agents.ToolResultBlock("call", "DISPLAY_MUST_NOT_BECOME_WIRE")
        { HasExplicitContent = true, StructuredContent = JsonSerializer.SerializeToElement(new { value = 7 }) };

        var sent = await SendAsync(result, "stream", novaJsonText: convert);

        var content = Assert.Single(sent.GetProperty("content").EnumerateArray());
        if (convert) Assert.Equal("{\"value\":7}", content.GetProperty("text").GetString());
        else Assert.Equal(7, content.GetProperty("json").GetProperty("value").GetInt32());
        Assert.DoesNotContain("DISPLAY_MUST_NOT_BECOME_WIRE", sent.GetRawText());
    }

    private async Task<JsonElement> SendAsync(Strands.Agents.ToolResultBlock result, string operation, bool? novaJsonText = null)
    {
        var request = new ModelRequest([
            Message.User("question"),
            new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [result])]);
        await using var wire = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = novaJsonText is null ? "opaque-model" : "amazon.nova-fixture",
            ConvertNovaToolResultJsonToText = novaJsonText ?? false,
            Streaming = operation == "stream",
            IncludeToolResultStatus = true,
            UseNativeTokenCount = operation == "count",
            TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw,
            RequestTimeout = TimeSpan.FromSeconds(15)
        });
        if (operation == "count") Assert.Equal(9, await model.CountTokensAsync(request));
        else
        {
            var ended = false;
            await foreach (var item in model.StreamAsync(request))
                if (item is ModelMessageStopEvent { StopReason: StopReasons.EndTurn }) ended = true;
            Assert.True(ended);
        }
        await wire.Completion;
        var sent = Assert.Single(wire.Requests);
        Assert.True(sent.Signed);
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "bedrock-explicit-empty-wire", Operation = operation, sent.Target, sent.Signed, sent.Body }));
        var messages = operation == "count" ? sent.Body.GetProperty("input").GetProperty("converse").GetProperty("messages") : sent.Body.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("content")[0].GetProperty("toolResult").Clone();
    }
}
