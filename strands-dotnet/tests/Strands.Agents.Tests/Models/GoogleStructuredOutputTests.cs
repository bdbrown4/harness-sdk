using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class GoogleStructuredOutputTests
{
    [Fact]
    public async Task SendsNativeJsonSchemaToGenerateContentAndRetainsConfiguredProviderTools()
    {
        var handler = new Handler("""{"candidates":[{"content":{"role":"model","parts":[{"text":"hidden thought","thought":true},{"text":"{\"value\":"},{"text":"42}"}]},"finishReason":"STOP"}]}""");
        using var client = new HttpClient(handler) { BaseAddress = new("https://gemini.test/v1beta/") };
        var model = new GoogleModel(client, new("model", "key")
        {
            Endpoint = new("https://gemini.test/v1beta/models/model:streamGenerateContent?tenant=one&alt=sse"),
            GenerationConfig = JsonSerializer.SerializeToElement(new { temperature = 0.2, responseSchema = new { type = "STRING" }, responseFormat = new { text = new { mimeType = "text/plain" } } }),
            BuiltInTools = [JsonSerializer.SerializeToElement(new { googleSearch = new { } })]
        });
        var request = new ModelRequest([Message.User("extract")], "instructions", [new("unused", "unused", JsonSerializer.SerializeToElement(new { type = "object" }))]) { ToolChoice = new(ToolChoiceMode.Tool, "unused") };

        var result = await model.InvokeStructuredAsync(request, Options());

        Assert.Equal(42, result.Value);
        Assert.Equal(new Uri("https://gemini.test/v1beta/models/model:generateContent?tenant=one"), handler.Endpoint);
        Assert.Equal("key", handler.Key);
        Assert.Equal(1, handler.Calls);
        var generation = handler.Body.GetProperty("generationConfig");
        Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", generation.GetProperty("responseJsonSchema").GetProperty("type").GetString());
        Assert.False(generation.TryGetProperty("responseSchema", out _));
        Assert.False(generation.TryGetProperty("responseFormat", out _));
        Assert.Equal(0.2, generation.GetProperty("temperature").GetDouble());
        Assert.True(Assert.Single(handler.Body.GetProperty("tools").EnumerateArray()).TryGetProperty("googleSearch", out _));
        Assert.False(handler.Body.TryGetProperty("toolConfig", out _));
        Assert.True(handler.Content.Disposed);
        Assert.False(handler.Disposed);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"value\":\"wrong\"}")]
    [InlineData("{\"missing\":42}")]
    public async Task InvalidOutputTextUsesTheSharedSchemaContractWithoutRetry(string text)
    {
        var handler = new Handler(JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } }, finishReason = "STOP" } } }));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new GoogleModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.Content.Disposed);
    }

    [Theory]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"MAX_TOKENS\",\"content\":{\"parts\":[{\"text\":\"{\\\"value\\\":42}\"}]}}]}")]
    [InlineData("{\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"side_effect\",\"args\":{}}}]}}]}")]
    [InlineData("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hidden\",\"thought\":true}]}}]}")]
    public async Task MissingTruncatedOrNonTextOutputsAreNotTreatedAsStructuredSuccess(string text)
    {
        var handler = new Handler(text);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new GoogleModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.Content.Disposed);
    }

    [Fact]
    public async Task UsesUpdatedCredentialsAndModelEndpointAndHonorsCancellation()
    {
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"{\"value\":42}"}]}}]}""");
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("before", "old-key"));
        model.UpdateConfig(options => options with { ModelId = "after", ApiKey = "new-key" });
        Assert.Equal(42, (await model.InvokeStructuredAsync(new([Message.User("extract")]), Options())).Value);
        Assert.Equal("new-key", handler.Key);
        Assert.EndsWith("/models/after:generateContent", handler.Endpoint!.AbsoluteUri);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.InvokeStructuredAsync(new([Message.User("extract")]), Options(), cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CustomStreamingRoutesRequireAnExplicitStructuredEndpointAndUpdatesValidateIt()
    {
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"{\"value\":42}"}]}}]}""");
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key") { Endpoint = new("https://custom.test/stream") });
        await Assert.ThrowsAsync<NotSupportedException>(() => model.InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.Equal(0, handler.Calls);
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { StructuredOutputEndpoint = new("ftp://custom.test/generate") }));
        Assert.Null(model.GetConfig().StructuredOutputEndpoint);
        model.UpdateConfig(options => options with { StructuredOutputEndpoint = new("https://custom.test/generate?tenant=one") });
        Assert.Equal(42, (await model.InvokeStructuredAsync(new([Message.User("extract")]), Options())).Value);
        Assert.Equal(new Uri("https://custom.test/generate?tenant=one"), handler.Endpoint);
    }

    [Fact]
    public async Task CompleteResponseBoundsAreValidatedBeforePublishingUpdates()
    {
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"{\"value\":42}"}]}}]}""");
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key"));
        model.UpdateConfig(options => options with { MaxResponseBytes = 8 });
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { MaxResponseBytes = 0 }));
        Assert.Equal(8, model.GetConfig().MaxResponseBytes);
        var error = await Assert.ThrowsAsync<ModelException>(() => model.InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.Contains("MaxResponseBytes", error.Message);
        Assert.True(handler.Content.Disposed);
    }

    [Fact]
    public async Task ProviderErrorEnvelopesRetainTypedClassification()
    {
        var handler = new Handler("""{"error":{"status":"RESOURCE_EXHAUSTED","message":"try later"}}""");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelThrottledException>(() => new GoogleModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.True(handler.Content.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DecoderFailuresRemainPrimaryWhenResponseDisposalAlsoFails()
    {
        var cleanup = new IOException("cleanup");
        var decoder = new InvalidOperationException("decoder");
        var handler = new Handler("""{"candidates":[{"content":{"parts":[{"text":"{\"value\":42}"}]}}]}""", cleanup);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<StructuredOutputException>(() => new GoogleModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("extract")]), Options() with { Deserializer = _ => throw decoder }));
        Assert.Same(decoder, error.InnerException);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.True(handler.Content.Disposed);
    }

    private static StructuredOutputOptions<Answer> Options() => new()
    {
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
    };
    private sealed record Answer(int Value);
    private sealed class Handler(string text, Exception? cleanup = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public string? Key { get; private set; }
        public Uri? Endpoint { get; private set; }
        public JsonElement Body { get; private set; }
        public TrackedContent Content { get; } = new(text, cleanup);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Endpoint = request.RequestUri;
            Key = request.Headers.GetValues("x-goog-api-key").Single();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Body = body.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = Content };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TrackedContent(string text, Exception? cleanup) : StringContent(text, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { var first = !Disposed; Disposed = true; base.Dispose(disposing); if (first && cleanup is not null) throw cleanup; }
    }
}
