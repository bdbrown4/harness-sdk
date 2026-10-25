using System.Net;
using System.Net.Http;
using System.Text.Json;
using Strands.Agents.Models;
using static Strands.Agents.Tests.Models.OpenAIResponsesStructuredOutputTests;

namespace Strands.Agents.Tests.Models;

public sealed class OpenAIResponsesStructuredOutputErrorTests
{
    [Theory]
    [InlineData("MAXIMUM CONTEXT LENGTH")]
    [InlineData("context_length_exceeded")]
    [InlineData("too many tokens")]
    [InlineData("context length")]
    [InlineData("input is too long for requested model")]
    [InlineData("input length and `max_tokens` exceed context limit")]
    [InlineData("too many total text bytes")]
    [InlineData("exceeds the max_model_len")]
    [InlineData("exceeds customer model maximum")]
    [InlineData("exceeded model maximum")]
    [InlineData("EXCEED MODEL MAXIMUM")]
    public async Task PinnedContextPatternsUseTheResponsesOnlyClassifier(string message)
    {
        using var transport = new ResponsesStructuredTransport(JsonSerializer.Serialize(new { error = new { message } })) { Status = HttpStatusCode.BadRequest };
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<ContextWindowOverflowException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Theory]
    [InlineData("RATE_LIMIT_EXCEEDED")]
    [InlineData("Rate LIMIT reached")]
    public async Task PinnedRatePatternsUseInvariantCasing(string message)
    {
        using var transport = new ResponsesStructuredTransport(JsonSerializer.Serialize(new { error = new { message } })) { Status = HttpStatusCode.BadRequest };
        using var client = new HttpClient(transport);
        await Assert.ThrowsAsync<ModelThrottledException>(() => new OpenAIResponsesModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), Options()));
    }

    [Theory]
    [InlineData("RATE_LIMIT_EXCEEDED", true)]
    [InlineData("CONTEXT_LENGTH_EXCEEDED", false)]
    public void PinnedCodesAreCaseInsensitive(string code, bool throttled)
    {
        var error = ProviderHttp.CreateError("OpenAI Responses", JsonSerializer.SerializeToElement(new { error = new { code, message = "other" } }), HttpStatusCode.BadRequest);
        if (throttled) Assert.IsType<ModelThrottledException>(error);
        else Assert.IsType<ContextWindowOverflowException>(error);
    }

    [Theory]
    [InlineData("OpenAI", "Too many requests from provider")]
    [InlineData("Google", "prompt tokens exceed customer model maximum")]
    public void NewPatternsDoNotChangeOtherProviders(string provider, string message)
    {
        var original = new IOException("transport");
        var error = ProviderHttp.CreateError(provider, JsonSerializer.SerializeToElement(new { error = new { message } }), HttpStatusCode.BadRequest, original);
        Assert.IsType<ModelException>(error);
        Assert.Same(original, error.InnerException);
    }

    [Fact]
    public void ExistingHttpThrottlePriorityWinsAContextLikeMessage()
    {
        var original = new HttpRequestException("http429");
        var error = ProviderHttp.CreateError("OpenAI Responses", JsonSerializer.SerializeToElement(new { error = new { message = "maximum context length" } }), (HttpStatusCode)429, original);
        Assert.IsType<ModelThrottledException>(error);
        Assert.Same(original, error.InnerException);
    }

    [Theory]
    [InlineData("Too many requests from provider", true)]
    [InlineData("prompt tokens exceed customer model maximum", false)]
    public async Task ExistingSseErrorFramesUseTheSameProviderClassifier(string message, bool throttled)
    {
        var frame = JsonSerializer.Serialize(new { type = "error", error = new { message } });
        using var transport = new ResponsesStructuredTransport("event: error\ndata: " + frame + "\n\n") { ContentType = "text/event-stream" };
        using var client = new HttpClient(transport);
        var error = await Assert.ThrowsAnyAsync<ModelException>(async () =>
        {
            await foreach (var value in new OpenAIResponsesModel(client, new("model", "key")).StreamAsync(new([Message.User("question")]))) { }
        });
        if (throttled) Assert.IsType<ModelThrottledException>(error);
        else Assert.IsType<ContextWindowOverflowException>(error);
        Assert.True(transport.Body.Closed);
    }
}
