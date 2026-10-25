using System.Net;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Models;

namespace Strands.Harness.Tests;

[Collection("Native model environment")]
public sealed class NativeModelPolicyTests
{
    [Fact]
    public async Task NativeMantleBuilderUsesAwsBearerCredentialsAndItsActualResponsesEndpoint()
    {
        var values = new Dictionary<string, string>
        {
            ["AWS_REGION"] = "us-west-2",
            ["AWS_ACCESS_KEY_ID"] = "fixture-access",
            ["AWS_SECRET_ACCESS_KEY"] = "fixture-secret",
            ["AWS_SESSION_TOKEN"] = "fixture-session",
        };
        var previous = values.Keys.ToDictionary(variable => variable, Environment.GetEnvironmentVariable);
        foreach (var pair in values) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        var owned = new List<IAsyncDisposable>();
        using var handler = new Handler(ResponsesResponse);
        try
        {
            var model = NativeModels.Create(ModelPolicy.Configure("bedrock-mantle/openai.gpt-6-astra", HarnessEffort.Low, true, false, true), owned, () => new HttpClient(handler));
            await foreach (var _ in model.StreamAsync(new([Message.User("question")]))) { }
            Assert.Equal("https://bedrock-mantle.us-west-2.api.aws/openai/v1/responses", handler.Uri!.AbsoluteUri);
            Assert.StartsWith("Bearer bedrock-api-key-", handler.Authorization, StringComparison.Ordinal);
            var token = Encoding.UTF8.GetString(Convert.FromBase64String(handler.Authorization!.Substring("Bearer bedrock-api-key-".Length)));
            Assert.Contains("X-Amz-Security-Token=fixture-session", token, StringComparison.Ordinal);
            var body = handler.Body!.Value;
            Assert.Equal("low", body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.True(body.GetProperty("tools")[0].GetProperty("external_web_access").GetBoolean());
        }
        finally
        {
            foreach (var resource in owned.AsEnumerable().Reverse()) await resource.DisposeAsync();
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        Assert.True(handler.Disposed);
    }

    [Theory]
    [InlineData("openai/gpt-5.6", "OPENAI_API_KEY", "OPENAI_BASE_URL")]
    [InlineData("anthropic/claude-opus-5", "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL")]
    public async Task NativeBuildersSendMappedReasoningSearchCachingAndApplicationTools(string identifier, string keyVariable, string urlVariable)
    {
        var previousKey = Environment.GetEnvironmentVariable(keyVariable);
        var previousUrl = Environment.GetEnvironmentVariable(urlVariable);
        Environment.SetEnvironmentVariable(keyVariable, "fixture-key");
        Environment.SetEnvironmentVariable(urlVariable, "https://provider.fixture");
        var owned = new List<IAsyncDisposable>();
        var anthropic = identifier.StartsWith("anthropic/", StringComparison.Ordinal);
        using var handler = new Handler(anthropic ? AnthropicResponse : ResponsesResponse);
        try
        {
            var model = NativeModels.Create(ModelPolicy.Configure(identifier, HarnessEffort.Low, true, false, true), owned, () => new HttpClient(handler));
            var spec = new ToolSpec("local", "Application tool", JsonSerializer.SerializeToElement(new { type = "object" }));
            await foreach (var _ in model.StreamAsync(new([Message.User("question")], "instructions", [spec]))) { }
            var body = handler.Body!.Value;
            Assert.Equal(2, body.GetProperty("tools").GetArrayLength());
            if (anthropic)
            {
                Assert.Equal("/v1/messages", handler.Uri!.AbsolutePath);
                Assert.Equal("low", body.GetProperty("output_config").GetProperty("effort").GetString());
                Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
                Assert.Equal(128000, body.GetProperty("max_tokens").GetInt32());
                Assert.Contains(body.GetProperty("tools").EnumerateArray(), tool => tool.TryGetProperty("type", out var type) && type.GetString() == "web_search_20260318");
                Assert.Equal("ephemeral", body.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
                Assert.Equal("fixture-key", handler.ApiKey);
            }
            else
            {
                Assert.Equal("/responses", handler.Uri!.AbsolutePath);
                Assert.Equal("low", body.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Contains(body.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("type").GetString() == "web_search");
                Assert.Contains(body.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("type").GetString() == "function" && tool.GetProperty("name").GetString() == "local");
                Assert.Equal("Bearer fixture-key", handler.Authorization);
            }
        }
        finally
        {
            foreach (var resource in owned.AsEnumerable().Reverse()) await resource.DisposeAsync();
            Environment.SetEnvironmentVariable(keyVariable, previousKey);
            Environment.SetEnvironmentVariable(urlVariable, previousUrl);
        }
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task NativeGoogleBuilderEmitsOneSearchToolAndMappedThinking()
    {
        var previous = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "fixture-key");
        var owned = new List<IAsyncDisposable>();
        using var handler = new Handler("data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}]}\n\n");
        try
        {
            var model = NativeModels.Create(ModelPolicy.Configure("google/gemini-3.5-flash", HarnessEffort.Low, true, false, true), owned, () => new HttpClient(handler));
            await foreach (var _ in model.StreamAsync(new([Message.User("question")], "instructions"))) { }
            var body = handler.Body!.Value;
            Assert.Single(body.GetProperty("tools").EnumerateArray());
            Assert.True(body.GetProperty("tools")[0].TryGetProperty("googleSearch", out _));
            Assert.Equal("low", body.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
            Assert.Equal("fixture-key", handler.ApiKey);
        }
        finally
        {
            foreach (var resource in owned.AsEnumerable().Reverse()) await resource.DisposeAsync();
            Environment.SetEnvironmentVariable("GOOGLE_API_KEY", previous);
        }
        Assert.True(handler.Disposed);
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public JsonElement? Body { get; private set; }
        public string? ApiKey { get; private set; }
        public bool Disposed { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Body = document.RootElement.Clone();
            ApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values) || request.Headers.TryGetValues("x-api-key", out values) ? values.Single() : null;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private const string AnthropicResponse = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":2,\"output_tokens\":0}}}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":0}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    private const string ResponsesResponse = "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"fixture\",\"status\":\"in_progress\"}}\n\nevent: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"fixture\",\"status\":\"completed\"}}\n\n";
}

[CollectionDefinition("Native model environment", DisableParallelization = true)]
public sealed class NativeModelEnvironmentCollection;
