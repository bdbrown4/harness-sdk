using System.Net.Http;
using Strands.Agents.Bedrock;
using Strands.Agents.Models;

namespace Strands.Agents.Compatibility.Tests;

public sealed class ProviderMetadataCompatibilityTests
{
    [Fact]
    public void NativeOpenAIProjectsItsConfiguredIdentityAndOutputAllowance()
    {
        using var client = new HttpClient();
        var model = new OpenAIModel(client, new("gpt-4.1", "test") { MaxTokens = 1234 });
        var limits = model.GetLimits();
        Assert.Equal("gpt-4.1", limits.ModelId);
        Assert.Equal(1_047_576, limits.ContextWindowLimit);
        Assert.Equal(1234, limits.MaxOutputTokens);
    }

    [Fact]
    public void NativeAnthropicProjectsItsConfiguredIdentityAndOutputAllowance()
    {
        using var client = new HttpClient();
        var model = new AnthropicModel(client, new("claude-opus-5", "test") { MaxTokens = 1234 });
        var limits = model.GetLimits();
        Assert.Equal("claude-opus-5", limits.ModelId);
        Assert.Equal(1_000_000, limits.ContextWindowLimit);
        Assert.Equal(1234, limits.MaxOutputTokens);
    }

    [Fact]
    public void HarnessBedrockIdentityResolvesWithoutCreatingAnAwsClient()
    {
        using var model = new BedrockModel(new() { ModelId = "global.anthropic.claude-opus-5", MaxTokens = 1234 });
        var limits = model.GetLimits();
        Assert.Equal("global.anthropic.claude-opus-5", limits.ModelId);
        Assert.Equal(1_000_000, limits.ContextWindowLimit);
        Assert.Equal(1234, limits.MaxOutputTokens);
    }
}
