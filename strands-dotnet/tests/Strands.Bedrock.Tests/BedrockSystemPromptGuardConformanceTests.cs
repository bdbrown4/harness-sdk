using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockSystemPromptGuardConformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("stream", "")]
    [InlineData("nonstream", "雪😀e\u0301\nline")]
    [InlineData("count", "count selected")]
    public async Task OfficialOperationsKeepMixedSystemOrderAndDuplicateQualifierOrder(string operation, string selected)
    {
        var qualifiers = new[] { GuardQualifier.Query, GuardQualifier.Query, GuardQualifier.GroundingSource, GuardQualifier.GuardContent };
        var request = new ModelRequest([Message.User("question")])
        {
            SystemPromptContent = [new TextBlock("first"), new GuardContentBlock(selected, qualifiers: qualifiers), new CachePointBlock("caller"), new TextBlock("last")]
        };
        var body = await SendAsync(request, operation);
        var system = System(body, operation);
        Assert.Equal(4, system.GetArrayLength());
        Assert.Equal("first", system[0].GetProperty("text").GetString());
        Assert.Equal("last", system[3].GetProperty("text").GetString());
        Assert.Equal("caller", system[2].GetProperty("cachePoint").GetProperty("ttl").GetString());
        var text = system[1].GetProperty("guardContent").GetProperty("text");
        Assert.Equal(selected, text.GetProperty("text").GetString());
        Assert.Equal(new[] { "query", "query", "grounding_source", "guard_content" }, text.GetProperty("qualifiers").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("first\nlast", request.SystemPrompt);
        Assert.Equal(qualifiers, Assert.IsType<GuardContentBlock>(request.SystemPromptContent![1]).Qualifiers);
    }

    [Theory]
    [InlineData("stream", "image/png", "png")]
    [InlineData("stream", "image/jpeg", "jpeg")]
    [InlineData("nonstream", "image/png", "png")]
    [InlineData("nonstream", "image/jpeg", "jpeg")]
    [InlineData("count", "image/png", "png")]
    [InlineData("count", "image/jpeg", "jpeg")]
    public async Task OfficialOperationsPreserveInlineGuardImageBytes(string operation, string media, string format)
    {
        var request = new ModelRequest([Message.User("question")]) { SystemPromptContent = [new GuardContentBlock(new ImageBlock(media, "AAEC/f7/"))] };
        var body = await SendAsync(request, operation);
        var image = Assert.Single(System(body, operation).EnumerateArray()).GetProperty("guardContent").GetProperty("image");
        Assert.Equal(format, image.GetProperty("format").GetString());
        Assert.Equal("AAEC/f7/", image.GetProperty("source").GetProperty("bytes").GetString());
        Assert.Null(request.SystemPrompt);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("nonstream")]
    [InlineData("count")]
    public async Task OfficialSerializerEmptyQualifierPresenceIsExplicitlyCharacterized(string operation)
    {
        var request = new ModelRequest([Message.User("question")]) { SystemPromptContent = [new GuardContentBlock("selected")] };
        var body = await SendAsync(request, operation);
        var text = System(body, operation)[0].GetProperty("guardContent").GetProperty("text");
        Assert.Equal("selected", text.GetProperty("text").GetString());
        var present = text.TryGetProperty("qualifiers", out var qualifiers);
        if (present) Assert.Equal(0, qualifiers.GetArrayLength());
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "official-empty-qualifiers", Operation = operation, QualifiersPresent = present, PinnedTsAlwaysEmitsEmptyArray = true, ExactArrayPresenceParityClaim = false }));
        Assert.Empty(Assert.IsType<GuardContentBlock>(Assert.Single(request.SystemPromptContent!)).Qualifiers);
    }

    [Fact]
    public void GuardMapperOwnsIndependentImageStreamsAndDisposesOnlyItsSources()
    {
        var image = new ImageBlock("image/png", "AAEC");
        var request = new ModelRequest([]) { SystemPromptContent = [new GuardContentBlock(image)] };
        var first = new BedrockRequestMapper();
        using var second = new BedrockRequestMapper();
        try
        {
            var one = first.Map(request, new()).System[0].GuardContent.Image.Source.Bytes;
            var two = second.Map(request, new()).System[0].GuardContent.Image.Source.Bytes;
            Assert.NotSame(one, two);
            Assert.Equal(new byte[] { 0, 1, 2 }, one.ToArray());
            first.Dispose();
            Assert.False(one.CanRead);
            Assert.True(two.CanRead);
            Assert.Equal(new byte[] { 0, 1, 2 }, two.ToArray());
            Assert.Equal("AAEC", image.Data);
        }
        finally { first.Dispose(); }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("caller", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("caller", true)]
    public void CallerCacheBoundaryStandsDownAutomaticSystemPointEvenWhenSectionDisabled(string? ttl, bool disabled)
    {
        var request = new ModelRequest([]) { SystemPromptContent = [new GuardContentBlock("selected"), new CachePointBlock(ttl), new TextBlock("last")] };
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(request, new() { Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, SystemPrompt = !disabled, SystemPromptTtl = "automatic" } });
        Assert.Equal(3, native.System.Count);
        Assert.NotNull(native.System[0].GuardContent);
        Assert.Equal(disabled || !string.IsNullOrEmpty(ttl) ? ttl : "automatic", Assert.Single(native.System, block => block.CachePoint is not null).CachePoint.Ttl);
        Assert.Equal(ttl, Assert.IsType<CachePointBlock>(request.SystemPromptContent![1]).Ttl);
        Assert.Equal("last", native.System[2].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardOnlySystemGetsOneAutomaticPointOnlyWhenTheSectionIsEnabled(bool enabled)
    {
        var request = new ModelRequest([]) { SystemPromptContent = [new GuardContentBlock("selected")] };
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(request, new() { Cache = new() { Strategy = BedrockCacheStrategy.Anthropic, SystemPrompt = enabled, SystemPromptTtl = "1h" } });
        Assert.Equal(enabled ? 2 : 1, native.System.Count);
        Assert.NotNull(native.System[0].GuardContent);
        Assert.Equal(enabled ? 1 : 0, native.System.Count(block => block.CachePoint is not null));
        if (enabled) Assert.Equal("1h", native.System[1].CachePoint.Ttl);
    }

    [Fact]
    public void GuardSystemComposesWithReasoningFilterNovaResultProjectionAndLatestUserSelection()
    {
        var request = new ModelRequest([
            new(Role.Assistant, [new ReasoningBlock("filtered", Signature: "signature"), new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [new ToolResultBlock("call", "") { ContentBlocks = [new JsonBlock(JsonSerializer.SerializeToElement(new { value = 7 }))] }]),
            Message.User("latest")])
        { SystemPromptContent = [new GuardContentBlock("system selected", qualifiers: [GuardQualifier.Query]), new CachePointBlock("caller")] };
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(request, new()
        {
            ModelId = "amazon.nova-pro",
            FilterRejectedReasoningContent = true,
            ConvertNovaToolResultJsonToText = true,
            Guardrail = new() { Identifier = "fixture", Version = "1", GuardLatestUserMessage = true }
        });
        Assert.Equal("system selected", native.System[0].GuardContent.Text.Text);
        Assert.Equal("caller", native.System[1].CachePoint.Ttl);
        Assert.Single(native.Messages[0].Content);
        Assert.NotNull(native.Messages[0].Content[0].ToolUse);
        Assert.Equal("{\"value\":7}", native.Messages[1].Content[0].ToolResult.Content[0].Text);
        Assert.Equal("latest", native.Messages[2].Content[0].GuardContent.Text.Text);
        Assert.Equal(2, request.Messages[0].Content.Count);
        Assert.IsType<ReasoningBlock>(request.Messages[0].Content[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquisitionRecoveriesRetainTheSystemGuardGraphAcrossOriginalGraphRetries(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new(BedrockRecoveryWireFixture.BindingError), new(BedrockRecoveryWireFixture.TurnError), new());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = "opaque-model",
            Streaming = streaming,
            RecoverStaleThinkingBlocks = true,
            RecoverToolResultTurns = true,
            AdditionalModelRequestFields = JsonSerializer.SerializeToElement(new { thinking = new { type = "enabled", budget_tokens = 1024 } }),
            RequestTimeout = TimeSpan.FromSeconds(15)
        });
        var request = new ModelRequest([
            Message.User("first"),
            new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [new ToolResultBlock("call", "saved")]), Message.User("last")])
        { SystemPromptContent = [new GuardContentBlock("system selected", qualifiers: [GuardQualifier.Query]), new CachePointBlock("caller")] };
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        Assert.Equal(3, wire.Requests.Length);
        var system = wire.Requests[0].Body.GetProperty("system").GetRawText();
        Assert.All(wire.Requests, sent => { Assert.True(sent.Signed); Assert.Equal(system, sent.Body.GetProperty("system").GetRawText()); });
        Assert.Equal(4, request.Messages.Count);
        Assert.IsType<ToolResultBlock>(Assert.Single(request.Messages[2].Content));
    }

    private async Task<JsonElement> SendAsync(ModelRequest request, string operation)
    {
        await using var wire = new BedrockRecoveryWireFixture(new BedrockRecoveryWireFixture.Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, new()
        {
            ModelId = "opaque-model",
            Streaming = operation == "stream",
            UseNativeTokenCount = operation == "count",
            TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw,
            RequestTimeout = TimeSpan.FromSeconds(15)
        });
        if (operation == "count") Assert.Equal(9, await model.CountTokensAsync(request));
        else Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        var sent = Assert.Single(wire.Requests);
        Assert.True(sent.Signed);
        Assert.EndsWith(operation switch { "stream" => "/converse-stream", "count" => "/count-tokens", _ => "/converse" }, sent.Target);
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "b07-stage3-conformance-wire", Operation = operation, sent.Target, sent.Signed, sent.Body }));
        return sent.Body.Clone();
    }
    private static JsonElement System(JsonElement body, string operation) => operation == "count" ? body.GetProperty("input").GetProperty("converse").GetProperty("system") : body.GetProperty("system");
    private static async Task<string> CollectAsync(IModel model, ModelRequest request)
    {
        var text = new StringBuilder();
        await foreach (var item in model.StreamAsync(request)) if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
        return text.ToString();
    }
}
