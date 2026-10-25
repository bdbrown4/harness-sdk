using Strands.Agents.Bedrock;
using Strands.Harness.Models;

namespace Strands.Harness.Tests;

public sealed class ModelPolicyTests
{
    [Fact]
    public void NativeDefaultBuildUsesMappedOptionsWithoutMakingAnAwsRequest()
    {
        var owned = new List<IAsyncDisposable>();
        using var model = Assert.IsType<BedrockModel>(NativeModels.Create(ModelPolicy.Configure(Harness.DefaultModel, HarnessEffort.Auto, true, false, false), owned));
        Assert.Equal("global.anthropic.claude-opus-5", model.ModelId);
        Assert.Equal(128000, model.Metadata.MaxOutputTokens);
        Assert.Empty(owned);
    }

    [Fact]
    public void DefaultBedrockPolicyUsesAdaptiveHighAndAllCacheSections()
    {
        var policy = ModelPolicy.Configure(Harness.DefaultModel, HarnessEffort.Auto, true, false, false);
        Assert.Equal("high", policy.Effort);
        Assert.Equal(128000, policy.MaxTokens);
        Assert.Equal("adaptive", policy.Parameters!.Value.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("summarized", policy.Parameters.Value.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("high", policy.Parameters.Value.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.True(policy.CacheConfig!.Messages);
        Assert.True(policy.CacheConfig.Tools);
        Assert.True(policy.CacheConfig.SystemPrompt);
    }

    [Fact]
    public void ExtendedThinkingUsesPinnedVersionLimitsAndBoundedBudgets()
    {
        var policy = ModelPolicy.Configure("anthropic/claude-sonnet-4-5", HarnessEffort.Low, true, true, false);
        Assert.Equal(64000, policy.MaxTokens);
        Assert.Equal(2048, policy.Parameters!.Value.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.False(policy.Parameters.Value.TryGetProperty("output_config", out _));
    }

    [Theory]
    [InlineData("bedrock/amazon.nova-pro-v1:0")]
    [InlineData("anthropic/claude-3-haiku-20240307")]
    [InlineData("ollama/llama3")]
    public void UnsupportedReasoningOmitsAutoAndRejectsAnExplicitLevel(string identifier)
    {
        var policy = ModelPolicy.Configure(identifier, HarnessEffort.Auto, false, false, false);
        Assert.Null(policy.Effort);
        Assert.Null(policy.Parameters);
        Assert.Throws<ArgumentException>(() => ModelPolicy.Configure(identifier, HarnessEffort.High, false, false, false));
    }

    [Fact]
    public void OffUsesOpenAiNoneAndKeepsNativeSearchIndependent()
    {
        var policy = ModelPolicy.Configure("openai/model", HarnessEffort.Off, true, true, true);
        Assert.Equal("none", policy.Effort);
        Assert.Equal("none", policy.Parameters!.Value.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("web_search", policy.Parameters.Value.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Null(policy.CacheConfig);
    }

    [Fact]
    public void PythonNovaTwoPolicyMapsNativeReasoningFields()
    {
        var policy = ModelPolicy.Configure("bedrock/eu.amazon.nova-2-lite-v1:0", HarnessEffort.Auto, true, false, false);
        Assert.Equal("high", policy.Parameters!.Value.GetProperty("reasoningConfig").GetProperty("maxReasoningEffort").GetString());
        Assert.Null(policy.CacheConfig);
    }

    [Theory]
    [InlineData("ollama/model")]
    [InlineData("bedrock/us.anthropic.claude-3-haiku-20240307-v1:0")]
    public void UnsupportedCachingIsDefaultBestEffortAndExplicitlyStrict(string identifier)
    {
        Assert.False(ModelPolicy.Configure(identifier, HarnessEffort.Auto, true, false, false).Caching);
        Assert.Throws<ArgumentException>(() => ModelPolicy.Configure(identifier, HarnessEffort.Auto, true, true, false));
    }
}
