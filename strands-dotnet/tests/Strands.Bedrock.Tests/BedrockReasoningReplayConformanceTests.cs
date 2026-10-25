using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockReasoningReplayConformanceTests
{
    [Theory]
    [InlineData("amazon.nova-lite")]
    [InlineData("amazon.nova-pro")]
    [InlineData("google.gemma")]
    [InlineData("meta.llama")]
    [InlineData("mistral.devstral")]
    [InlineData("mistral.ministral")]
    [InlineData("mistral.mistral-large")]
    [InlineData("nvidia.nemotron-nano-9b")]
    [InlineData("nvidia.nemotron-super")]
    [InlineData("openai.gpt-5")]
    [InlineData("openai.gpt-6")]
    [InlineData("qwen.qwen3-235b")]
    [InlineData("qwen.qwen3-coder")]
    [InlineData("writer.palmyra")]
    [InlineData("xai.grok")]
    public void KnownRejectingFamiliesKeepRedactedBytesButRejectBothTextVariants(string family)
        => AssertProjection("us." + family + "-fixture:0", 4);

    [Theory]
    [InlineData("openai.gpt-oss-120b-1:0", 5)]
    [InlineData("moonshot.kimi-k2-thinking", 5)]
    [InlineData("minimax.minimax-m2.5", 5)]
    [InlineData("global.anthropic.claude-opus-5", 6)]
    [InlineData("custom.CLAUDE", 6)]
    [InlineData("anthropic", 6)]
    [InlineData("arn:aws:bedrock:us-east-1::foundation-model/anthropic.claude", 6)]
    [InlineData("us.deepseek.r1-v1:0", 0)]
    [InlineData("arn:aws:bedrock:us-east-1::foundation-model/deepseek.r1", 0)]
    [InlineData("deepseek", 0)]
    [InlineData("arn:aws:bedrock:us-east-1::foundation-model/amazon.nova-pro", 7)]
    [InlineData("arn:aws:bedrock:us-east-1:123:application-inference-profile/opaque", 7)]
    [InlineData("abc123xyz", 7)]
    [InlineData("bare-profile", 7)]
    [InlineData("other.vendor.model", 5)]
    [InlineData("DEEPSEEK.anthropic.claude", 0)]
    [InlineData("claude.deepseek", 0)]
    [InlineData("uS.mEtA.LlAmA", 4)]
    public void OrderedModelIdentityRulesPreservePinnedOpaqueAndVendorExceptions(string modelId, int kept)
        => AssertProjection(modelId, kept);

    [Theory]
    [InlineData("anthropic.claude", null, false, null)]
    [InlineData("anthropic.claude", "", false, null)]
    [InlineData("anthropic.claude", " ", true, " ")]
    [InlineData("other.model", null, true, null)]
    [InlineData("other.model", "", true, null)]
    [InlineData("other.model", " ", false, null)]
    public void SignatureTruthUsesNonemptyPresenceWithoutTrimming(string modelId, string? signature, bool kept, string? projectedSignature)
    {
        using var mapper = new BedrockRequestMapper();
        var original = new ReasoningBlock("", signature);
        var mapped = mapper.Map(new([new(Role.Assistant, [original, new TextBlock("answer")])]), new()
        {
            ModelId = modelId,
            FilterRejectedReasoningContent = true
        });
        var reasoning = mapped.Messages[0].Content.Where(block => block.ReasoningContent?.ReasoningText is not null).ToArray();
        if (kept)
        {
            var selected = Assert.Single(reasoning).ReasoningContent.ReasoningText;
            Assert.Equal("", selected.Text);
            Assert.Equal(projectedSignature, selected.Signature);
        }
        else Assert.Empty(reasoning);
        Assert.Equal(signature, original.Signature);
    }

    [Theory]
    [InlineData(Role.User)]
    [InlineData(Role.Assistant)]
    public void FilteringAppliesToBothConversationRolesAndPreservesRoleAndOrder(Role role)
    {
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(new([new(role, [new TextBlock("before"), new ReasoningBlock("discard"), new TextBlock("after")])]), new()
        {
            ModelId = "anthropic.claude",
            FilterRejectedReasoningContent = true
        });
        var message = Assert.Single(mapped.Messages);
        Assert.Equal(role == Role.User ? "user" : "assistant", message.Role.Value);
        Assert.Equal(new[] { "before", "after" }, message.Content.Select(block => block.Text));
    }

    [Fact]
    public void DisabledProjectionPreservesAlreadyEmptyMessagesAndExplicitEmptySignatures()
    {
        using var mapper = new BedrockRequestMapper();
        var messages = new Message[] { new(Role.User, []), new(Role.Assistant, [new ReasoningBlock("text", ""), new RedactedReasoningBlock("AAE=")]) };
        var mapped = mapper.Map(new(messages), new() { ModelId = "deepseek" });
        Assert.Equal(2, mapped.Messages.Count);
        Assert.Empty(mapped.Messages[0].Content);
        Assert.Equal("", mapped.Messages[1].Content[0].ReasoningContent.ReasoningText.Signature);
        Assert.Equal(new byte[] { 0, 1 }, mapped.Messages[1].Content[1].ReasoningContent.RedactedContent.ToArray());
        Assert.Equal(2, messages.Length);
    }

    [Fact]
    public void EnabledProjectionPrunesZeroContentButPreservesAnEmptyTextBlock()
    {
        using var mapper = new BedrockRequestMapper();
        var original = new Message[] { new(Role.User, []), new(Role.Assistant, [new ReasoningBlock("only")]), new(Role.User, [new TextBlock("")]) };
        var mapped = mapper.Map(new(original), new() { ModelId = "anthropic.claude", FilterRejectedReasoningContent = true });
        Assert.Equal("", Assert.Single(Assert.Single(mapped.Messages).Content).Text);
        Assert.Equal(3, original.Length);
        Assert.Empty(original[0].Content);
        Assert.IsType<ReasoningBlock>(Assert.Single(original[1].Content));
    }

    [Fact]
    public void DeepSeekDoesNotDecodeADiscardedInvalidRedactedPayload()
    {
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(new([new(Role.Assistant, [new RedactedReasoningBlock("invalid base64!?"), new TextBlock("safe")])]), new()
        {
            ModelId = "arn:deepseek",
            FilterRejectedReasoningContent = true
        });
        Assert.Equal("safe", Assert.Single(Assert.Single(mapped.Messages).Content).Text);
    }

    [Theory]
    [InlineData(false, "deepseek")]
    [InlineData(true, "openai.gpt-oss")]
    [InlineData(false, "anthropic.claude")]
    public void RetainedRedactedPayloadKeepsExistingNativeDecodeFailure(bool filtering, string modelId)
    {
        using var mapper = new BedrockRequestMapper();
        Assert.Throws<FormatException>(() => mapper.Map(new([new(Role.Assistant, [new RedactedReasoningBlock("invalid base64!?")])]), new()
        {
            ModelId = modelId,
            FilterRejectedReasoningContent = filtering
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeUnsupportedConversationContentRemainsAuthoritative(bool filtering)
    {
        using var mapper = new BedrockRequestMapper();
        var error = Assert.Throws<ModelException>(() => mapper.Map(new([new(Role.User, [new JsonBlock(JsonSerializer.SerializeToElement(new { unsupported = true }))])]), new()
        {
            ModelId = "deepseek",
            FilterRejectedReasoningContent = filtering
        }));
        Assert.Contains("JsonBlock", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilterDoesNotBroadenNativeToolResultContentAuthority(bool filtering)
    {
        using var mapper = new BedrockRequestMapper();
        var result = new ToolResultBlock("call", "") { ContentBlocks = [new ReasoningBlock("nested")] };
        var error = Assert.Throws<ModelException>(() => mapper.Map(new([new(Role.User, [result])]), new()
        {
            ModelId = "deepseek",
            FilterRejectedReasoningContent = filtering
        }));
        Assert.Contains("tool result content 'ReasoningBlock'", error.Message);
    }

    [Fact]
    public void DefaultOldJsonAndConfigPublicationPreserveExplicitNativeOptIn()
    {
        var old = JsonSerializer.Deserialize<BedrockModelOptions>("{}")!;
        Assert.False(old.FilterRejectedReasoningContent);
        using var model = new BedrockModel(old);
        Assert.False(model.GetConfig().FilterRejectedReasoningContent);
        model.UpdateConfig(options => options with { FilterRejectedReasoningContent = true, RecoverStaleThinkingBlocks = true, RecoverToolResultTurns = true });
        var accepted = model.GetConfig();
        Assert.True(accepted.FilterRejectedReasoningContent);
        Assert.True(accepted.RecoverStaleThinkingBlocks);
        Assert.True(accepted.RecoverToolResultTurns);
        Assert.True(JsonSerializer.Deserialize<BedrockModelOptions>(JsonSerializer.Serialize(accepted))!.FilterRejectedReasoningContent);
        model.UpdateConfig(options => options with { ModelId = "other.model" });
        Assert.True(model.GetConfig().FilterRejectedReasoningContent);
        Assert.False(old.FilterRejectedReasoningContent);
    }

    private static void AssertProjection(string modelId, int kept)
    {
        var opaque = Encoding.UTF8.GetBytes("opaque 雪\0bytes");
        var content = new ContentBlock[] { new ReasoningBlock("unsigned"), new ReasoningBlock("signed", "signature-雪"),
            new RedactedReasoningBlock(Convert.ToBase64String(opaque)), new TextBlock("answer") };
        using var mapper = new BedrockRequestMapper();
        var projected = mapper.Map(new([new(Role.Assistant, content)]), new() { ModelId = modelId, FilterRejectedReasoningContent = true }).Messages[0].Content;
        var expectedText = new List<string>();
        if ((kept & 1) != 0) expectedText.Add("unsigned");
        if ((kept & 2) != 0) expectedText.Add("signed");
        Assert.Equal(expectedText, projected.Where(block => block.ReasoningContent?.ReasoningText is not null).Select(block => block.ReasoningContent.ReasoningText.Text));
        var redacted = projected.Where(block => block.ReasoningContent?.RedactedContent is not null).ToArray();
        if ((kept & 4) != 0) Assert.Equal(opaque, Assert.Single(redacted).ReasoningContent.RedactedContent.ToArray());
        else Assert.Empty(redacted);
        Assert.Equal("answer", projected.Last().Text);
        Assert.Equal(4, content.Length);
        Assert.Equal("signature-雪", Assert.IsType<ReasoningBlock>(content[1]).Signature);
        Assert.Equal(Convert.ToBase64String(opaque), Assert.IsType<RedactedReasoningBlock>(content[2]).Data);
    }
}
