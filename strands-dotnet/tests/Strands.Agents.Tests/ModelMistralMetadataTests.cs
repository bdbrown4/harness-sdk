using System.Net.Http;
using Strands.Agents.Models;

namespace Strands.Agents.Tests;

public sealed class ModelMistralMetadataTests
{
    [Theory]
    [InlineData("mistral-large-latest", 262_144L)]
    [InlineData("mistral-large-2512", 262_144L)]
    [InlineData("mistral-large-3", 262_144L)]
    [InlineData("mistral-medium-latest", 131_072L)]
    [InlineData("mistral-medium-2505", 131_072L)]
    [InlineData("mistral-small-latest", 131_072L)]
    [InlineData("mistral-small-3-2-2506", 131_072L)]
    public void PythonMistralIdentifiersResolveTheirDeclaredCapacity(string modelId, long expected)
    {
        var handler = new NoRequestHandler();
        using var http = new HttpClient(handler);
        IModel model = new MistralModel(http, new(modelId, "fixed-dummy-key"));

        Assert.Equal(expected, model.GetLimits().ContextWindowLimit);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("gpt-5.4", 1_050_000L)]
    [InlineData("eu.anthropic.claude-opus-5", 1_000_000L)]
    [InlineData("US.anthropic.claude-opus-5", 200_000L)]
    public void ExistingTypeScriptLookupRulesRemainExact(string modelId, long expected)
    {
        var handler = new NoRequestHandler();
        using var http = new HttpClient(handler);
        // These are metadata lookup inputs, not a claim that Mistral serves other providers' models.
        IModel model = new MistralModel(http, new(modelId, "fixed-dummy-key"));

        Assert.Equal(expected, model.GetLimits().ContextWindowLimit);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void ContextResolutionRetainsTheConfiguredOutputAllowance()
    {
        var handler = new NoRequestHandler();
        using var http = new HttpClient(handler);
        IModel model = new MistralModel(http, new("mistral-large-latest", "fixed-dummy-key") { MaxTokens = 73 });

        Assert.Equal(73L, model.GetLimits().MaxOutputTokens);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class NoRequestHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Model metadata resolution must not send HTTP requests.");
        }
    }
}
