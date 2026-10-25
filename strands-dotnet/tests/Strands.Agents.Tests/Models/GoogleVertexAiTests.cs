using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class GoogleVertexAiTests
{
    private static readonly Uri Endpoint = new("https://vertex.example.test/v1/projects/p/locations/l/publishers/google/models/gemini:streamGenerateContent?alt=sse");

    [Fact]
    public async Task KeylessVertexUsesTheExplicitEndpointAndBorrowedBearerTransport()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "application-token");
        await Collect(new GoogleModel(client, new("gemini") { UseVertexAi = true, Endpoint = Endpoint }));
        var call = Assert.Single(handler.Calls);
        Assert.Equal(Endpoint, call.Endpoint);
        Assert.Equal("Bearer application-token", call.Authorization);
        Assert.Equal("Bearer application-token", client.DefaultRequestHeaders.Authorization!.ToString());
        Assert.False(handler.Disposed);
        Assert.Equal(1, handler.ResponseDisposals);
    }

    [Fact]
    public async Task VertexDoesNotAddAnApiKeyEvenWhenLegacyOptionsContainOne()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        await Collect(new GoogleModel(client, VertexOptions()));
        Assert.Null(Assert.Single(handler.Calls).ApiKey);
        Assert.False(client.DefaultRequestHeaders.Contains("x-goog-api-key"));
    }

    [Fact]
    public async Task VertexMixedToolsKeepFunctionPolicyWithoutTheAutomaticDeveloperFlag()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        await Collect(new GoogleModel(client, VertexOptions() with { BuiltInTools = [JsonSerializer.SerializeToElement(new { googleSearch = new { } })] }), MixedRequest());
        var body = Assert.Single(handler.Calls).Body;
        Assert.Equal(2, body.GetProperty("tools").GetArrayLength());
        var policy = body.GetProperty("toolConfig");
        Assert.Equal("AUTO", policy.GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        Assert.False(policy.TryGetProperty("includeServerSideToolInvocations", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VertexRequiresAnExplicitEndpointEvenWithAClientBaseAddress(bool baseAddress)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        if (baseAddress) client.BaseAddress = new("https://vertex.example.test/v1/");
        Assert.Throws<ArgumentException>(() => new GoogleModel(client, VertexOptions() with { Endpoint = null }));
        Assert.Empty(handler.Calls);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("count")]
    [InlineData("structured")]
    public async Task EveryPathKeepsTheExplicitVertexResourceAndBorrowedAuthentication(string path)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "application-token");
        var endpoint = new Uri(Endpoint.AbsoluteUri + "&trace=keep");
        var model = new GoogleModel(client, new("gemini") { UseVertexAi = true, Endpoint = endpoint, UseNativeTokenCount = true });
        await InvokePath(model, path);
        var call = Assert.Single(handler.Calls);
        var expected = path switch
        {
            "stream" => endpoint.AbsoluteUri,
            "count" => Endpoint.GetLeftPart(UriPartial.Path).Replace(":streamGenerateContent", ":countTokens"),
            _ => Endpoint.GetLeftPart(UriPartial.Path).Replace(":streamGenerateContent", ":generateContent") + "?trace=keep"
        };
        Assert.Equal(expected, call.Endpoint.AbsoluteUri);
        Assert.Equal("Bearer application-token", call.Authorization);
        Assert.Null(call.ApiKey);
        Assert.Equal(1, handler.ResponseDisposals);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false, "true")]
    [InlineData(false, "false")]
    [InlineData(false, "null")]
    [InlineData(true, "true")]
    [InlineData(true, "false")]
    [InlineData(true, "null")]
    public async Task ExplicitNativeToolPolicyAndItsFlagAreNeverStrippedOrReplaced(bool vertex, string flag)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        using var native = JsonDocument.Parse("{\"toolConfig\":{\"includeServerSideToolInvocations\":" + flag + ",\"functionCallingConfig\":{\"mode\":\"NONE\"},\"customMarker\":17}}");
        var expected = native.RootElement.GetProperty("toolConfig").Clone();
        var model = new GoogleModel(client, VertexOptions() with { UseVertexAi = vertex, Params = native.RootElement, BuiltInTools = [JsonSerializer.SerializeToElement(new { googleSearch = new { } })] });
        native.Dispose();
        await Collect(model, MixedRequest());
        Assert.True(JsonElement.DeepEquals(expected, Assert.Single(handler.Calls).Body.GetProperty("toolConfig")));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://vertex.example.test/model")]
    public void VertexRejectsAnInvalidExplicitEndpointBeforeUsingTheBorrowedTransport(string endpoint)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        Assert.Throws<ArgumentException>(() => new GoogleModel(client, VertexOptions() with { Endpoint = new(endpoint, UriKind.RelativeOrAbsolute) }));
        Assert.Empty(handler.Calls);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task DeveloperDefaultsStillRequireAKeyAndAutomaticallyEnableTheMixedToolsFlag()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var keyless = new GoogleModelOptions("gemini");
        Assert.False(keyless.UseVertexAi);
        Assert.Throws<ArgumentException>(() => new GoogleModel(client, keyless));
        await Collect(new GoogleModel(client, new("gemini", "developer-key") { BuiltInTools = [JsonSerializer.SerializeToElement(new { googleSearch = new { } })] }), MixedRequest());
        var call = Assert.Single(handler.Calls);
        Assert.Equal("generativelanguage.googleapis.com", call.Endpoint.Host);
        Assert.Equal("developer-key", call.ApiKey);
        Assert.True(call.Body.GetProperty("toolConfig").GetProperty("includeServerSideToolInvocations").GetBoolean());
    }

    [Theory]
    [InlineData("count")]
    [InlineData("structured")]
    public async Task ExplicitNativeEndpointsOverrideANonderivableCustomStreamRoute(string path)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var count = new Uri("https://vertex.example.test/custom-count?trace=required");
        var structured = new Uri("https://vertex.example.test/custom-json?trace=required");
        var model = new GoogleModel(client, new("gemini") { UseVertexAi = true, Endpoint = new("https://vertex.example.test/custom-stream"), TokenCountEndpoint = count, StructuredOutputEndpoint = structured, UseNativeTokenCount = true });
        await InvokePath(model, path);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(path == "count" ? count : structured, call.Endpoint);
        Assert.Null(call.ApiKey);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("structured")]
    public async Task MissingNonderivableNativeEndpointsFailBeforeSending(string path)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var options = VertexOptions() with { Endpoint = new("https://vertex.example.test/custom-stream") };
        if (path == "count") Assert.Throws<ArgumentException>(() => new GoogleModel(client, options with { UseNativeTokenCount = true }));
        else await Assert.ThrowsAsync<NotSupportedException>(() => InvokePath(new GoogleModel(client, options), path));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task CallerSuppliedDefaultHeadersRemainAuthoritativeAndUnmodified()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("x-goog-api-key", "caller-selected-key");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "application-token");
        await Collect(new GoogleModel(client, VertexOptions()));
        var call = Assert.Single(handler.Calls);
        Assert.Equal("caller-selected-key", call.ApiKey);
        Assert.Equal("Bearer application-token", call.Authorization);
        Assert.Equal("caller-selected-key", Assert.Single(client.DefaultRequestHeaders.GetValues("x-goog-api-key")));
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("count")]
    [InlineData("structured")]
    public async Task AuthenticationMayBeProvidedByACallerHandlerAtRequestTime(string path)
    {
        using var handler = new Capture();
        using var auth = new LateAuthentication(handler);
        using var client = new HttpClient(auth);
        var model = new GoogleModel(client, new("gemini") { UseVertexAi = true, Endpoint = Endpoint, UseNativeTokenCount = true });
        await InvokePath(model, path);
        Assert.Equal("Bearer handler-token", Assert.Single(handler.Calls).Authorization);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(auth.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task VertexRetainsTheAcceptedTypedImageUrlAndInlineOrdering()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var url = new Uri("https://images.example.test/caf\u00e9/image.png?key=a%2Fb#section");
        await Collect(new GoogleModel(client, VertexOptions()), new([new(Role.User, [new TextBlock("before"), new ImageBlock("image/png", "AQID"), new ImageBlock("image/jpeg", url), new TextBlock("after")])]));
        var call = Assert.Single(handler.Calls);
        Assert.Equal(Endpoint, call.Endpoint);
        var parts = call.Body.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("before", parts[0].GetProperty("text").GetString());
        Assert.Equal("AQID", parts[1].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal(url.AbsoluteUri, parts[2].GetProperty("fileData").GetProperty("fileUri").GetString());
        Assert.Equal("after", parts[3].GetProperty("text").GetString());
    }

    [Fact]
    public async Task StructuredRawMixedToolsUseTheSameVertexModeWithoutForgingAManagedToolCall()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var native = JsonSerializer.SerializeToElement(new { tools = new object[] { new { functionDeclarations = new[] { new { name = "native_function", parametersJsonSchema = new { type = "object" } } } }, new { googleSearch = new { } } }, toolConfig = new { functionCallingConfig = new { mode = "NONE" } } });
        var model = new GoogleModel(client, VertexOptions() with { Params = native });
        await InvokePath(model, "structured", MixedRequest());
        var body = Assert.Single(handler.Calls).Body;
        Assert.Equal(2, body.GetProperty("tools").GetArrayLength());
        Assert.Equal("native_function", body.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.Equal("NONE", body.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        Assert.False(body.GetProperty("toolConfig").TryGetProperty("includeServerSideToolInvocations", out _));
    }

    [Fact]
    public async Task GetConfigRetainsTheModeAndDetachedNativeConfiguration()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        using var document = JsonDocument.Parse("{\"cachedContent\":\"cachedContents/example\"}");
        var builtIns = new List<JsonElement> { JsonSerializer.SerializeToElement(new { googleSearch = new { } }) };
        var model = new GoogleModel(client, VertexOptions() with { Params = document.RootElement, BuiltInTools = builtIns });
        document.Dispose(); builtIns.Clear();
        var config = model.GetConfig();
        Assert.True(config.UseVertexAi);
        Assert.Equal(Endpoint, config.Endpoint);
        Assert.Equal("cachedContents/example", config.Params!.Value.GetProperty("cachedContent").GetString());
        Assert.Single(config.BuiltInTools);
        Assert.DoesNotContain("legacy-unused-key", config.ToString());
        await Collect(model, MixedRequest());
        Assert.Equal("cachedContents/example", Assert.Single(handler.Calls).Body.GetProperty("cachedContent").GetString());
    }

    [Fact]
    public async Task AnEnteredStreamRetainsItsModeWhileLaterCallsUseTheReplacement()
    {
        var entered = Signal(); var release = Signal();
        using var handler = new Capture { BeforeResponse = async (call, _) => { if (call.Endpoint == Endpoint) { entered.TrySetResult(true); await release.Task; } } };
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, VertexOptions());
        var first = Collect(model);
        try
        {
            await Ready(entered.Task);
            model.UpdateConfig(options => options with { UseVertexAi = false, ApiKey = "replacement-key", Endpoint = new("https://developer.example.test/gemini:streamGenerateContent?alt=sse") });
        }
        finally { release.TrySetResult(true); await first; }
        await Collect(model);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Null(handler.Calls[0].ApiKey);
        Assert.Equal("replacement-key", handler.Calls[1].ApiKey);
        Assert.Equal("developer.example.test", handler.Calls[1].Endpoint.Host);
        Assert.False(model.GetConfig().UseVertexAi);
    }

    [Fact]
    public async Task InvalidModeReplacementCannotPublishAnUnusableConfiguration()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, VertexOptions());
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { Endpoint = null }));
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { UseVertexAi = false, ApiKey = "" }));
        Assert.True(model.GetConfig().UseVertexAi);
        Assert.Equal(Endpoint, model.GetConfig().Endpoint);
        Assert.Empty(handler.Calls);
        await Collect(model);
        Assert.Null(Assert.Single(handler.Calls).ApiKey);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("count")]
    [InlineData("structured")]
    public async Task AlreadyRequestedCallerCancellationPreventsEveryTransportPath(string path)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var model = new GoogleModel(client, VertexOptions() with { UseNativeTokenCount = true });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokePath(model, path, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("count")]
    [InlineData("structured")]
    public async Task RequestedCallerCancellationPhysicallyJoinsTheAcceptedTransport(string path)
    {
        var entered = Signal(); var left = Signal();
        using var handler = new Capture { BeforeResponse = async (_, token) => { entered.TrySetResult(true); try { await Task.Delay(Timeout.Infinite, token); } finally { left.TrySetResult(true); } } };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = InvokePath(new GoogleModel(client, VertexOptions() with { UseNativeTokenCount = true }), path, cancellationToken: cancellation.Token);
        try
        {
            await Ready(entered.Task);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(left.Task.IsCompleted);
            Assert.Single(handler.Calls);
            Assert.False(handler.Disposed);
        }
        finally { cancellation.Cancel(); await Record.ExceptionAsync(() => operation); }
    }

    [Fact]
    public async Task EarlyStreamingDisposalClosesTheResponseAndKeepsTheModelReusable()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, VertexOptions());
        await using (var stream = model.StreamAsync(new([Message.User("go")])).GetAsyncEnumerator()) Assert.True(await stream.MoveNextAsync());
        Assert.Equal(1, handler.ResponseDisposals);
        await Collect(model);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(2, handler.ResponseDisposals);
        Assert.All(handler.Calls, call => Assert.Null(call.ApiKey));
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData("{\"ModelId\":\"gemini\",\"ApiKey\":\"key\"}", "gemini", "key")]
    [InlineData("{\"ModelId\":\"gemini\"}", "gemini", null)]
    [InlineData("{\"ApiKey\":\"key\"}", null, "key")]
    [InlineData("{\"ModelId\":null,\"ApiKey\":null}", null, null)]
    [InlineData("{}", null, null)]
    public void OriginalOptionsJsonConstructorSelectionRetainsNullAndMissingFieldBehavior(string json, string? modelId, string? apiKey)
    {
        var options = JsonSerializer.Deserialize<GoogleModelOptions>(json);
        Assert.NotNull(options);
        Assert.Equal(modelId, options.ModelId);
        Assert.Equal(apiKey, options.ApiKey);
        Assert.False(options.UseVertexAi);
    }

    [Fact]
    public void VertexOptionsRoundTripThroughTheExistingJsonOptionsSurface()
    {
        var original = new GoogleModelOptions("gemini") { UseVertexAi = true, Endpoint = Endpoint };
        var restored = JsonSerializer.Deserialize<GoogleModelOptions>(JsonSerializer.Serialize(original));
        Assert.NotNull(restored);
        Assert.Equal(original.ModelId, restored.ModelId);
        Assert.Equal(original.ApiKey, restored.ApiKey);
        Assert.Equal(original.Endpoint, restored.Endpoint);
        Assert.True(restored.UseVertexAi);
        Assert.Empty(restored.BuiltInTools);
    }

    [Fact]
    public void JsonNullStillDeserializesToANullOptionsReference()
        => Assert.Null(JsonSerializer.Deserialize<GoogleModelOptions>("null"));

    private static GoogleModelOptions VertexOptions() => new("gemini", "legacy-unused-key") { UseVertexAi = true, Endpoint = Endpoint };
    private static ModelRequest MixedRequest() => new([Message.User("go")], Tools: [new("lookup", "Find data", JsonSerializer.SerializeToElement(new { type = "object" }))]) { ToolChoice = new(ToolChoiceMode.Auto) };
    private static async Task Collect(IModel model, ModelRequest? request = null, CancellationToken cancellationToken = default)
    {
        await foreach (var unused in model.StreamAsync(request ?? new([Message.User("go")]), cancellationToken)) { }
    }

    private static async Task InvokePath(GoogleModel model, string path, ModelRequest? request = null, CancellationToken cancellationToken = default)
    {
        request ??= new([Message.User("go")]);
        if (path == "stream") await Collect(model, request, cancellationToken);
        else if (path == "count") Assert.Equal(7, await model.CountTokensAsync(request, cancellationToken));
        else Assert.Equal(42, (await model.InvokeStructuredAsync(request, new StructuredOutputOptions<Answer> { Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false }) }, cancellationToken)).Value);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Ready(Task task)
    {
        using var timeout = new CancellationTokenSource();
        try { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token))); }
        finally { timeout.Cancel(); }
    }

    private sealed record Answer(int Value);

    private sealed record Call(Uri Endpoint, string? Authorization, string? ApiKey, JsonElement Body);
    private sealed class Capture : HttpMessageHandler
    {
        internal List<Call> Calls { get; } = [];
        internal bool Disposed;
        internal int ResponseDisposals;
        internal Func<Call, CancellationToken, Task>? BeforeResponse { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var call = new Call(request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("x-goog-api-key", out var keys) ? string.Join(",", keys) : null, body.RootElement.Clone());
            Calls.Add(call);
            if (BeforeResponse is { } before) await before(call, cancellationToken);
            var streaming = request.Headers.Accept.Any(value => value.MediaType == "text/event-stream");
            var structured = call.Body.TryGetProperty("generationConfig", out var generation) && generation.TryGetProperty("responseMimeType", out var mime) && mime.GetString() == "application/json";
            var text = streaming ? "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n" : structured ? "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"value\\\":42}\"}]}}]}" : "{\"totalTokens\":7}";
            return new(HttpStatusCode.OK) { Content = new Content(text, streaming ? "text/event-stream" : "application/json", () => ResponseDisposals++) };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class LateAuthentication(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { request.Headers.Authorization = new("Bearer", "handler-token"); return base.SendAsync(request, cancellationToken); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Content(string text, string mediaType, Action disposed) : StringContent(text, Encoding.UTF8, mediaType)
    {
        private bool _disposed;
        protected override void Dispose(bool disposing) { var first = !_disposed; _disposed = true; base.Dispose(disposing); if (first) disposed(); }
    }
}
