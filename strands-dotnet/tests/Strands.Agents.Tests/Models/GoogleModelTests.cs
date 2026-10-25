using System.Net;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class GoogleModelTests
{
    [Fact]
    public async Task StreamsNativeThoughtTextAndUsageWithoutLosingEmptySignaturePart()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("gemini-2.5-flash", "key"));
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(new([Message.User("hello")], "system"))) events.Add(item);
        Assert.NotEmpty(events);
        Assert.Equal("hello", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
        Assert.Equal("consider", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Reasoning)));
        Assert.Equal(new Usage(4, 6, 1, ReportedTotalTokens: 10), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.Equal(StopReasons.EndTurn, Assert.IsType<ModelMessageStopEvent>(events[^1]).StopReason);
        Assert.Contains(events.OfType<ModelContentBlockStopEvent>(), value => value.ContentBlock is GoogleContentBlock block && block.Part.TryGetProperty("thoughtSignature", out var signature) && signature.GetString() == "trailing-signature");
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", Assert.Single(handler.Uris).AbsoluteUri);
        Assert.Equal("key", Assert.Single(handler.ApiKeys));
    }

    [Theory]
    [InlineData(ToolChoiceMode.Auto, "AUTO")]
    [InlineData(ToolChoiceMode.Any, "ANY")]
    [InlineData(ToolChoiceMode.None, "NONE")]
    [InlineData(ToolChoiceMode.Tool, "ANY")]
    public async Task MapsNativeGenerationConfigurationAndEveryToolChoice(ToolChoiceMode mode, string expected)
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler) { BaseAddress = new("https://example.test/v1beta/") };
        using var document = JsonDocument.Parse("""{"temperature":0.5,"maxOutputTokens":90,"responseMimeType":"application/json","responseJsonSchema":{"type":"object"}}""");
        var builtIn = JsonSerializer.SerializeToElement(new { googleSearch = new { } });
        var tools = new List<JsonElement> { builtIn };
        var model = new GoogleModel(client, new("models/gemini-2.5-flash", "key")
        {
            GenerationConfig = document.RootElement,
            Params = JsonSerializer.SerializeToElement(new { generationConfig = new { temperature = 0.1, topP = 0.8 }, cachedContent = "cachedContents/example" }),
            BuiltInTools = tools
        });
        document.Dispose(); tools.Clear();
        var specification = new ToolSpec("lookup", "Find data", JsonSerializer.SerializeToElement(new { type = "object" }));
        await Collect(model, new([Message.User("go")], "instructions", [specification]) { ToolChoice = new(mode, mode == ToolChoiceMode.Tool ? "lookup" : null) });
        var body = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Bodies));
        Assert.Equal(0.5, body.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, body.GetProperty("generationConfig").GetProperty("topP").GetDouble());
        Assert.Equal("application/json", body.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.Equal("cachedContents/example", body.GetProperty("cachedContent").GetString());
        Assert.Equal("instructions", body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("name").GetString());
        Assert.True(body.GetProperty("tools")[1].TryGetProperty("googleSearch", out _));
        var config = body.GetProperty("toolConfig");
        Assert.Equal(expected, config.GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        Assert.True(config.GetProperty("includeServerSideToolInvocations").GetBoolean());
        if (mode == ToolChoiceMode.Tool) Assert.Equal("lookup", config.GetProperty("functionCallingConfig").GetProperty("allowedFunctionNames")[0].GetString());
        Assert.Equal(new ModelMetadata("models/gemini-2.5-flash", MaxOutputTokens: 90), model.Metadata);
        Assert.Equal("https://example.test/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", Assert.Single(handler.Uris).AbsoluteUri);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("x-goog-api-key"));
    }

    [Fact]
    public async Task NativeToolConfigTakesPrecedenceOverNarrowRequestChoice()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key") { Params = JsonSerializer.SerializeToElement(new { toolConfig = new { functionCallingConfig = new { mode = "VALIDATED" }, includeServerSideToolInvocations = false } }) });
        await Collect(model, new([Message.User("go")]) { ToolChoice = new(ToolChoiceMode.Tool, "absent") });
        var config = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Bodies)).GetProperty("toolConfig");
        Assert.Equal("VALIDATED", config.GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        Assert.False(config.GetProperty("includeServerSideToolInvocations").GetBoolean());
    }

    [Fact]
    public async Task AgentPreservesThoughtSignaturesCandidateCitationsAndOpaquePartsThroughReplay()
    {
        using var handler = new Handler(TextResponse(), TextResponse());
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new GoogleModel(client, new("model", "key")) });
        Assert.Equal("hello", (await agent.InvokeAsync("go")).ToString());
        var response = agent.Messages[1];
        Assert.Equal(3, response.Content.Count);
        Assert.Equal("reasoning-signature", Assert.IsType<GoogleContentBlock>(response.Content[0]).Part.GetProperty("thoughtSignature").GetString());
        Assert.Equal("trailing-signature", Assert.IsType<GoogleContentBlock>(response.Content[2]).Part.GetProperty("thoughtSignature").GetString());
        Assert.Equal("https://example.test", response.Metadata!.Value.GetProperty("google").GetProperty("candidate").GetProperty("citationMetadata").GetProperty("citationSources")[0].GetProperty("uri").GetString());
        Assert.Equal("r1", response.Metadata.Value.GetProperty("google").GetProperty("response").GetProperty("responseId").GetString());
        await agent.InvokeAsync("again");
        var replay = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("contents")[1];
        Assert.Equal("model", replay.GetProperty("role").GetString());
        Assert.Equal("reasoning-signature", replay.GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal("trailing-signature", replay.GetProperty("parts")[2].GetProperty("thoughtSignature").GetString());
        Assert.False(replay.TryGetProperty("citationMetadata", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentExecutesFunctionWithStableIdAndReplaysSignedPart(bool providerId)
    {
        using var handler = new Handler(FunctionResponse(providerId), TextResponse());
        using var client = new HttpClient(handler);
        var tool = new FunctionTool(new("lookup", "Find data", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => ValueTask.FromResult("found:" + input.GetProperty("value").GetInt32()));
        await using var agent = new Agent(new() { Model = new GoogleModel(client, new("model", "key")), Tools = [tool] });
        Assert.Equal("hello", (await agent.InvokeAsync("go")).ToString());
        var call = Assert.IsType<ToolUseBlock>(agent.Messages[1].Content[0]);
        Assert.Equal("tool-signature", call.ProviderMetadata!.Value.GetProperty("google").GetProperty("thoughtSignature").GetString());
        if (providerId) Assert.Equal("call", call.ToolUseId);
        else Assert.StartsWith("tooluse_", call.ToolUseId, StringComparison.Ordinal);
        var input = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("contents");
        Assert.Equal("tool-signature", input[1].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
        Assert.Equal(call.ToolUseId, input[1].GetProperty("parts")[0].GetProperty("functionCall").GetProperty("id").GetString());
        var result = input[2].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal(call.ToolUseId, result.GetProperty("id").GetString());
        Assert.Equal("lookup", result.GetProperty("name").GetString());
        Assert.Equal("found:42", result.GetProperty("response").GetProperty("output")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task TokenLimitedFunctionNeverExecutesLocalSideEffects()
    {
        using var handler = new Handler(FunctionResponse().Replace("STOP", "MAX_TOKENS", StringComparison.Ordinal));
        using var client = new HttpClient(handler);
        var called = false;
        var tool = new FunctionTool(new("lookup", "Find", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { called = true; return ValueTask.FromResult("unsafe"); });
        await using var agent = new Agent(new() { Model = new GoogleModel(client, new("model", "key")), Tools = [tool] });
        Assert.Equal(StopReasons.MaxTokens, (await agent.InvokeAsync("go")).StopReason);
        Assert.False(called);
        Assert.Equal(ToolResultStatus.Error, Assert.IsType<ToolResultBlock>(agent.Messages.Last().Content[0]).Status);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("input")]
    public async Task RejectsStaleSignedFunctionMetadataBeforeSending(string changed)
    {
        using var handler = new Handler(FunctionResponse(), TextResponse());
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key"));
        var events = await Collect(model, new([Message.User("go")]));
        var start = Assert.Single(events.OfType<ModelContentBlockStartEvent>());
        var original = start.ToolUse! with { Input = JsonSerializer.SerializeToElement(new { value = 42 }) };
        var altered = original with { ToolUseId = changed == "id" ? "other" : original.ToolUseId, Name = changed == "name" ? "other" : original.Name, Input = changed == "input" ? JsonSerializer.SerializeToElement(new { value = 43 }) : original.Input };
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(model, new([new(Role.Assistant, [altered])])));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RichFunctionResultsRetainStructuredErrorsAndUniqueMediaReferences()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var input = JsonSerializer.SerializeToElement(new { });
        var result = new ToolResultBlock("call", [new TextBlock("failed"), new ImageBlock("image/png", "AA=="), new ImageBlock("image/png", "AQ=="), new DocumentBlock("application/pdf", "Ag==", "report.pdf")], ToolResultStatus.Error, JsonSerializer.SerializeToElement(new { code = 42 }));
        await Collect(new GoogleModel(client, new("model", "key")), new([new(Role.Assistant, [new ToolUseBlock("call", "lookup", input)]), new(Role.User, [result])]));
        var output = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Bodies)).GetProperty("contents")[1].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("lookup", output.GetProperty("name").GetString());
        var parts = output.GetProperty("parts");
        Assert.Equal("image.png", parts[0].GetProperty("inlineData").GetProperty("displayName").GetString());
        Assert.Equal("image-1.png", parts[1].GetProperty("inlineData").GetProperty("displayName").GetString());
        Assert.Equal("report.pdf", parts[2].GetProperty("inlineData").GetProperty("displayName").GetString());
        var error = output.GetProperty("response").GetProperty("error");
        Assert.Equal("failed", error[0].GetProperty("text").GetString());
        Assert.Equal("image.png", error[1].GetProperty("$ref").GetString());
        Assert.Equal(42, error[4].GetProperty("json").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task NativeCountIsOptInAndAddsOnlyHeuristicSystemAndToolOverhead()
    {
        using var handler = new Handler("""{"totalTokens":17}""") { ContentType = "application/json" };
        using var client = new HttpClient(handler) { BaseAddress = new("https://example.test/v1beta/") };
        var request = new ModelRequest([Message.User("12345678")], "12345678", [new("lookup", "Find", JsonSerializer.SerializeToElement(new { type = "object" }))]);
        var fallback = new GoogleModel(client, new("model", "key"));
        var estimated = await fallback.CountTokensAsync(request);
        Assert.Empty(handler.Bodies);
        var native = new GoogleModel(client, new("model", "key") { UseNativeTokenCount = true });
        Assert.Equal(17 + estimated - 2, await native.CountTokensAsync(request));
        Assert.Equal("https://example.test/v1beta/models/model:countTokens", Assert.Single(handler.Uris).AbsoluteUri);
        var body = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Bodies));
        Assert.True(body.TryGetProperty("contents", out _));
        Assert.False(body.TryGetProperty("systemInstruction", out _));
        Assert.False(body.TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData("{\"totalTokens\":-1}")]
    [InlineData("{\"totalTokens\":\"bad\"}")]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task NativeCountFailuresFallBackWithoutReturningInvalidCounts(string response)
    {
        using var handler = new Handler(response) { ContentType = "application/json" };
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key") { UseNativeTokenCount = true });
        Assert.Equal(2, await model.CountTokensAsync(new([Message.User("12345678")])));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task NativeCountCancellationDoesNotFallBackOrCloseBorrowedClient()
    {
        using var handler = new Handler("{}");
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var model = new GoogleModel(client, new("model", "key") { UseNativeTokenCount = true });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CountTokensAsync(new([Message.User("go")]), cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(handler.Bodies);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData("RESOURCE_EXHAUSTED", "busy", true)]
    [InlineData("UNAVAILABLE", "busy", true)]
    [InlineData("INVALID_ARGUMENT", "input exceeds the maximum number of tokens", false)]
    public async Task ClassifiesNativeErrorObjectsWithNumericErrorCode(string status, string message, bool throttled)
    {
        using var handler = new Handler(Frame(JsonSerializer.Serialize(new { error = new { code = 400, status, message } })));
        using var client = new HttpClient(handler);
        var action = () => Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")]));
        if (throttled) await Assert.ThrowsAsync<ModelThrottledException>(action);
        else await Assert.ThrowsAsync<ContextWindowOverflowException>(action);
    }

    [Theory]
    [InlineData("\"promptTokenCount\":3", "\"promptTokenCount\":-1")]
    [InlineData("\"index\":0", "\"index\":1")]
    [InlineData("\"finishReason\":\"STOP\"", "\"finishReason\":\"FINISH_REASON_UNSPECIFIED\"")]
    [InlineData("\"parts\":[{\"text\":\"hello\"}", "\"parts\":[{\"text\":42}")]
    public async Task RejectsMalformedUsageCandidateContentAndMissingTermination(string original, string replacement)
    {
        using var handler = new Handler(TextResponse().Replace(original, replacement, StringComparison.Ordinal));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")])));
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("OTHER")]
    [InlineData("BLOCKLIST")]
    public async Task PromptFeedbackBlockWithoutCandidatesIsAnExplicitFilteredResult(string reason)
    {
        using var handler = new Handler(Frame(JsonSerializer.Serialize(new { promptFeedback = new { blockReason = reason } })));
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new GoogleModel(client, new("model", "key")) });
        Assert.Equal(StopReasons.ContentFiltered, (await agent.InvokeAsync("go")).StopReason);
        Assert.Equal(reason, agent.Messages[1].Metadata!.Value.GetProperty("google").GetProperty("response").GetProperty("promptFeedback").GetProperty("blockReason").GetString());
    }

    [Fact]
    public async Task RetainsUsageTrailerAfterFinishReasonWithoutDoubleCountingCumulativeTotals()
    {
        var stream = Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"hello"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":1,"totalTokenCount":3}}""") +
            Frame("""{"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":4,"totalTokenCount":6}}""");
        using var handler = new Handler(stream);
        using var client = new HttpClient(handler);
        var events = await Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")]));
        Assert.Equal(new Usage(2, 4, ReportedTotalTokens: 6), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.IsType<ModelMessageStopEvent>(events[^1]);
    }

    [Fact]
    public async Task RejectsContentAfterFinishReason()
    {
        using var handler = new Handler(TextResponse() + Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"late"}]}}]}"""));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")])));
    }

    [Fact]
    public void NativeContentClonesProducerJsonAndRoundTripsItsEmptySignaturePart()
    {
        using var producer = JsonDocument.Parse("""{"text":"","thoughtSignature":"opaque","partMetadata":{"source":"provider"}}""");
        var block = new GoogleContentBlock(producer.RootElement);
        producer.Dispose();
        var copy = Assert.IsType<GoogleContentBlock>(Assert.Single(JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(new Message(Role.Assistant, [block])))!.Content));
        Assert.Empty(copy.Text);
        Assert.Equal("opaque", copy.Part.GetProperty("thoughtSignature").GetString());
        Assert.Equal("provider", copy.Part.GetProperty("partMetadata").GetProperty("source").GetString());
    }

    [Fact]
    public async Task RejectsChangingResponseIdentityAcrossChunks()
    {
        var response = Frame("""{"responseId":"first","candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"hello"}]}}]}""") +
            Frame("""{"responseId":"second","candidates":[{"index":0,"finishReason":"STOP"}]}""");
        using var handler = new Handler(response);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")])));
    }

    [Fact]
    public async Task ValidatesThoughtUsageEvenWhenCandidateCountUsesTotalFallback()
    {
        var response = TextResponse().Replace("\"candidatesTokenCount\":2,", "", StringComparison.Ordinal).Replace("\"thoughtsTokenCount\":4", "\"thoughtsTokenCount\":-1", StringComparison.Ordinal);
        using var handler = new Handler(response);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")])));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "not-json", true)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":400,\"status\":\"INVALID_ARGUMENT\",\"message\":\"exceeds the maximum number of tokens\"}}", false)]
    public async Task ClassifiesHttpFailuresAndRetainsTransportCause(HttpStatusCode status, string response, bool throttled)
    {
        using var handler = new Handler(response) { StatusCode = status, ContentType = "application/json" };
        using var client = new HttpClient(handler);
        var action = () => Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")]));
        ModelException error = throttled ? await Assert.ThrowsAsync<ModelThrottledException>(action) : await Assert.ThrowsAsync<ContextWindowOverflowException>(action);
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task SecondaryBodyCleanupDoesNotReplacePrimaryMalformedStreamError()
    {
        var stream = new FailingDisposeStream("data: not-json\n\n");
        using var handler = new StreamHandler(stream);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelException>(() => Collect(new GoogleModel(client, new("model", "key")), new([Message.User("go")])));
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Contains(stream.Failure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
    }

    private sealed class FailingDisposeStream(string content) : MemoryStream(Encoding.UTF8.GetBytes(content))
    {
        internal IOException Failure { get; } = new("body close failed");
        protected override void Dispose(bool disposing) { base.Dispose(disposing); throw Failure; }
    }

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(response);
        }
    }

    internal static string FunctionResponse(bool providerId = true)
    {
        var function = new Dictionary<string, object?> { ["name"] = "lookup", ["args"] = new { value = 42 } };
        if (providerId) function["id"] = "call";
        return Frame(JsonSerializer.Serialize(new { candidates = new[] { new { index = 0, content = new { role = "model", parts = new[] { new { functionCall = function, thoughtSignature = "tool-signature" } } }, finishReason = "STOP" } } }));
    }

    internal static async Task<List<ModelStreamEvent>> Collect(IModel model, ModelRequest request)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(request)) result.Add(item);
        return result;
    }

    internal static string Frame(string json) => "data: " + json + "\n\n";
    internal static string TextResponse() => Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"consider","thought":true,"thoughtSignature":"reasoning-signature"}]}}]}""") +
        Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"hello"},{"text":"","thoughtSignature":"trailing-signature"}]},"finishReason":"STOP","citationMetadata":{"citationSources":[{"uri":"https://example.test"}]}}],"usageMetadata":{"promptTokenCount":3,"toolUsePromptTokenCount":1,"candidatesTokenCount":2,"thoughtsTokenCount":4,"totalTokenCount":10,"cachedContentTokenCount":1},"responseId":"r1","modelVersion":"gemini-2.5-flash"}""");

    internal sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal List<Uri> Uris { get; } = [];
        internal List<string> ApiKeys { get; } = [];
        internal string ContentType { get; init; } = "text/event-stream";
        internal HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        internal bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Uris.Add(request.RequestUri!);
            ApiKeys.Add(request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : "");
            return new(StatusCode) { Content = new StringContent(responses[Math.Min(Bodies.Count - 1, responses.Length - 1)], Encoding.UTF8, ContentType) };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
