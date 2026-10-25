using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class ProviderParityTests
{
    [Fact]
    public async Task AnthropicExplicitCacheBoundaryStillWinsBeforeTheDynamicSuffix()
    {
        using var handler = new CaptureHandler(AnthropicResponse);
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Messages = true, Ttl = "5m" } });

        await Collect(model, new([new(Role.User, [new TextBlock("stable"), new CachePointBlock("1h"), new TextBlock("stable tail"), new TextBlock("dynamic")]), Message.Assistant("assistant tail")]) { DynamicTrailingBlocks = 1 });

        var content = handler.Body!.Value.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("1h", content[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.False(content[1].TryGetProperty("cache_control", out _));
        Assert.False(content[2].TryGetProperty("cache_control", out _));
    }

    [Fact]
    public async Task AnthropicEntirelyDynamicUserContentGetsNoAutomaticCachePoint()
    {
        using var handler = new CaptureHandler(AnthropicResponse);
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Messages = true } });

        await Collect(model, new([Message.User("dynamic")]) { DynamicTrailingBlocks = 1 });

        Assert.False(handler.Body!.Value.GetProperty("messages")[0].GetProperty("content")[0].TryGetProperty("cache_control", out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task AnthropicInvalidDynamicSuffixFailsBeforeHttp(int count)
    {
        using var handler = new CaptureHandler(AnthropicResponse);
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Messages = true } });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Collect(model, new([Message.User("one")]) { DynamicTrailingBlocks = count }));

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AnthropicAutomaticCachePrecedesTheEphemeralTrailingSuffix()
    {
        using var handler = new CaptureHandler(AnthropicResponse);
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Messages = true } });
        var original = new Message(Role.User, [new TextBlock("stable"), new TextBlock("dynamic status")]);

        await Collect(model, new([original]) { DynamicTrailingBlocks = 1 });

        var content = handler.Body!.Value.GetProperty("messages")[0].GetProperty("content");
        Assert.True(content[0].TryGetProperty("cache_control", out _));
        Assert.False(content[1].TryGetProperty("cache_control", out _));
        Assert.Equal(2, original.Content.Count);
    }

    [Theory]
    [InlineData(false, ToolChoiceMode.Auto, "auto")]
    [InlineData(false, ToolChoiceMode.Any, "required")]
    [InlineData(false, ToolChoiceMode.None, "none")]
    [InlineData(true, ToolChoiceMode.Auto, "auto")]
    [InlineData(true, ToolChoiceMode.Any, "any")]
    [InlineData(true, ToolChoiceMode.None, "none")]
    [InlineData(false, ToolChoiceMode.Tool, "function")]
    [InlineData(true, ToolChoiceMode.Tool, "tool")]
    public async Task ToolChoiceUsesProviderNativeWireShape(bool anthropic, ToolChoiceMode mode, string expected)
    {
        using var handler = new CaptureHandler(anthropic ? AnthropicResponse : OpenAIResponse);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await Collect(model, new([Message.User("go")], Tools: [new("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }))])
        {
            ToolChoice = new(mode, mode == ToolChoiceMode.Tool ? "lookup" : null)
        });
        var value = handler.Body!.Value.GetProperty("tool_choice");
        Assert.Equal(expected, anthropic || mode == ToolChoiceMode.Tool ? value.GetProperty("type").GetString() : value.GetString());
        if (mode == ToolChoiceMode.Tool) Assert.Equal("lookup", anthropic ? value.GetProperty("name").GetString() : value.GetProperty("function").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidToolChoiceFailsBeforeSendingHttp(bool anthropic)
    {
        using var handler = new CaptureHandler(anthropic ? AnthropicResponse : OpenAIResponse);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await Assert.ThrowsAsync<ArgumentException>(() => Collect(model, new([Message.User("go")]) { ToolChoice = new(ToolChoiceMode.Tool, "missing") }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task OpenAIRichToolContentHoistsMediaAfterAllCorrelatedResults()
    {
        using var handler = new CaptureHandler(OpenAIResponse);
        using var client = new HttpClient(handler);
        var result = new ToolResultBlock("one", [new TextBlock("found:"), new JsonBlock(JsonSerializer.SerializeToElement(new { id = 3 })), new ImageBlock("image/png", "aGVsbG8=")], ToolResultStatus.Error);
        await Collect(new OpenAIModel(client, new("model", "key")), new([new(Role.User, [new TextBlock("question"), result, new ToolResultBlock("two", "other")])]));
        var messages = handler.Body!.Value.GetProperty("messages");
        Assert.Equal("tool", messages[0].GetProperty("role").GetString());
        Assert.Equal("[ERROR] found:{\"id\":3}", messages[0].GetProperty("content").GetString());
        Assert.Equal("two", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("data:image/png;base64,aGVsbG8=", messages[2].GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("question", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AnthropicRichContentAndRedactedReasoningRoundTrip()
    {
        using var handler = new CaptureHandler(AnthropicResponse);
        using var client = new HttpClient(handler);
        await Collect(new AnthropicModel(client, new("model", "key")), new([
            new(Role.Assistant, [new RedactedReasoningBlock("opaque")]),
            new(Role.User, [new ToolResultBlock("call", [new TextBlock("result"), new ImageBlock("image/png", "aGVsbG8="), new JsonBlock(JsonSerializer.SerializeToElement(new { ok = true }))])]) ]));
        var messages = handler.Body!.Value.GetProperty("messages");
        Assert.Equal("opaque", messages[0].GetProperty("content")[0].GetProperty("data").GetString());
        var content = messages[1].GetProperty("content")[0].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("{\"ok\":true}", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task AnthropicRedactedStreamIsOpaqueAndReplayable()
    {
        var response = AnthropicResponse.Replace("event: message_delta", "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"redacted_thinking\",\"data\":\"opaque\"}}\n\nevent: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\nevent: message_delta");
        using var handler = new CaptureHandler(response);
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new AnthropicModel(client, new("model", "key")) });
        var result = await agent.InvokeAsync("go");
        Assert.Equal("opaque", Assert.IsType<RedactedReasoningBlock>(Assert.Single(result.Message!.Content)).Data);
    }

    [Fact]
    public async Task DisabledAutomaticMessageCacheStillHonorsExplicitPoints()
    {
        using var handler = new CaptureHandler(AnthropicResponse);
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Messages = false, Ttl = "1h" } });
        await Collect(model, new([new(Role.User, [new TextBlock("prefix"), new CachePointBlock()])]));
        var cache = handler.Body!.Value.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("cache_control");
        Assert.Equal("ephemeral", cache.GetProperty("type").GetString());
        Assert.False(cache.TryGetProperty("ttl", out _));
    }

    [Fact]
    public async Task AnthropicCacheSectionsUseExplicitTtlAndDoNotModifyInputs()
    {
        using var handler = new CaptureHandler(AnthropicResponse);
        using var client = new HttpClient(handler);
        var message = new Message(Role.User, [new TextBlock("prefix"), new CachePointBlock("1h"), new TextBlock("tail")]);
        var model = new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Ttl = "5m", ToolsTtl = "1h" } });
        await Collect(model, new([message], "system", [new("tool", "Describe", JsonSerializer.SerializeToElement(new { type = "object" }))]));
        var body = handler.Body!.Value;
        Assert.Equal("5m", body.GetProperty("system")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.Equal("1h", body.GetProperty("tools")[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        var content = body.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("1h", content[0].GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.False(content[1].TryGetProperty("cache_control", out _));
        Assert.IsType<CachePointBlock>(message.Content[1]);
    }

    [Fact]
    public async Task OpenAINativeParametersAreClonedAndManagedFieldsWin()
    {
        using var handler = new CaptureHandler(OpenAIResponse);
        using var client = new HttpClient(handler);
        OpenAIModel model;
        using (var parameters = JsonDocument.Parse("{\"stream\":false,\"model\":\"wrong\",\"messages\":[],\"seed\":42,\"temperature\":0.9}"))
            model = new(client, new("model", "key") { Params = parameters.RootElement, Temperature = 0.2, TopP = 0.7, FrequencyPenalty = 1, PresencePenalty = -1, CacheConfig = new() { Ttl = "24h" } });
        await Collect(model, new([Message.User("go")]) { SessionId = "session" });
        var body = handler.Body!.Value;
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("model", body.GetProperty("model").GetString());
        Assert.Equal(42, body.GetProperty("seed").GetInt32());
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.7, body.GetProperty("top_p").GetDouble());
        Assert.Equal("strands-session", body.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("24h", body.GetProperty("prompt_cache_retention").GetString());
    }

    [Fact]
    public async Task OpenAIUserAudioImageAndDocumentRetainWirePayloads()
    {
        using var handler = new CaptureHandler(OpenAIResponse);
        using var client = new HttpClient(handler);
        await Collect(new OpenAIModel(client, new("model", "key")), new([new(Role.User, [new AudioBlock("audio/wav", "audio"), new ImageBlock("image/png", "image"), new DocumentBlock("application/pdf", "document", "report.pdf")])]));
        var content = handler.Body!.Value.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("wav", content[0].GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal("data:image/png;base64,image", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:application/pdf;base64,document", content[2].GetProperty("file").GetProperty("file_data").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedContentIsRejectedBeforeSending(bool anthropic)
    {
        using var handler = new CaptureHandler(anthropic ? AnthropicResponse : OpenAIResponse);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(model, new([new(Role.User, [new UnsupportedBlock()])])));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ReasoningConversionDoesNotSerializeProviderSignatureOrCredentials()
    {
        using var handler = new CaptureHandler(OpenAIResponse);
        using var client = new HttpClient(handler);
        await Collect(new OpenAIModel(client, new("model", "credential-secret")), new([new(Role.Assistant, [new ReasoningBlock("reasoning text", "signature-secret")])]));
        var json = handler.Body!.Value.GetRawText();
        Assert.DoesNotContain("signature-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-secret", json, StringComparison.Ordinal);
        Assert.Equal("Bearer credential-secret", handler.Authorization);
        Assert.Equal("reasoning text", handler.Body.Value.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AnthropicUsageOverflowIsReportedAsModelFailure()
    {
        var stream = AnthropicResponse.Replace("\"input_tokens\":2", "\"input_tokens\":9223372036854775807")
            .Replace("\"output_tokens\":0", "\"output_tokens\":1");
        using var handler = new CaptureHandler(stream);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelException>(() => Collect(new AnthropicModel(client, new("model", "key")), new([Message.User("go")])));
        Assert.IsType<OverflowException>(error.InnerException);
    }

    private sealed record UnsupportedBlock : ContentBlock;

    internal const string OpenAIResponse = "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    internal const string AnthropicResponse = "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":2,\"output_tokens\":0}}}\n\nevent: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":0}}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
    internal static async Task<List<ModelStreamEvent>> Collect(IModel model, ModelRequest request)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(request)) events.Add(item);
        return events;
    }
    internal sealed class CaptureHandler(string response) : HttpMessageHandler
    {
        internal JsonElement? Body { get; private set; }
        internal int Calls { get; private set; }
        internal string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
#if NET48
            cancellationToken.ThrowIfCancellationRequested();
            var body = await request.Content!.ReadAsStringAsync();
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(body);
#else
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
#endif
            Body = document.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") };
        }
    }
}
