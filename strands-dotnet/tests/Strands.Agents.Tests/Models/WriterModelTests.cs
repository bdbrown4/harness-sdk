using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class WriterModelTests
{
    [Theory]
    [InlineData("data: {broken\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call\",\"function\":{\"name\":\"tool\",\"arguments\":\"{\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n")]
    public async Task RejectsMalformedAndIncompleteResponsesWithoutRetrying(string wire)
    {
        using var handler = new Handler(wire);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ModelException>(() => Consume(new WriterModel(client, new("palmyra-x4", "key")), new([Message.User("question")])));

        Assert.Single(handler.Requests);
        Assert.True(handler.Body!.Closed);
    }

    [Fact]
    public async Task OlderPalmyraUsesOneNonemptyTextBlockAndOmitsUnsupportedInputsAndEmptyTools()
    {
        using var handler = new Handler(Frames("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));
        using var client = new HttpClient(handler) { BaseAddress = new("https://writer.example/v1/") };

        await Consume(new WriterModel(client, new("palmyra-x4", "key")), new([new(Role.User, [new TextBlock(""), new ImageBlock("image/png", "AQID"), new ReasoningBlock("ignored"), new TextBlock("question")])]));

        var body = Assert.Single(handler.Requests);
        Assert.Equal("https://writer.example/v1/chat", handler.Endpoint!.AbsoluteUri);
        Assert.Equal("question", body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task OlderPalmyraRejectsMultipleTextBlocksBeforeSending()
    {
        using var handler = new Handler("");
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => Consume(new WriterModel(client, new("palmyra-x4", "key")), new([new(Role.User, [new TextBlock("one"), new TextBlock("two")])])));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task VisionRejectsUnsupportedContentBeforeSending()
    {
        using var handler = new Handler("");
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<NotSupportedException>(() => Consume(new WriterModel(client, new("palmyra-x5", "key")), new([new(Role.User, [new ReasoningBlock("unsupported")])])));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnvironmentCredentialIsResolvedPerCallAndMissingCredentialsFailBeforeHttp()
    {
        var previous = Environment.GetEnvironmentVariable("WRITER_API_KEY");
        using var handler = new Handler(Frames("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));
        using var client = new HttpClient(handler);
        var model = new WriterModel(client, new("palmyra-x4"));
        try
        {
            Environment.SetEnvironmentVariable("WRITER_API_KEY", "first");
            await Consume(model, new([Message.User("one")]));
            Assert.Equal("first", handler.Credential);
            Environment.SetEnvironmentVariable("WRITER_API_KEY", "second");
            await Consume(model, new([Message.User("two")]));
            Assert.Equal("second", handler.Credential);
            Environment.SetEnvironmentVariable("WRITER_API_KEY", null);
            await Assert.ThrowsAsync<ModelException>(() => Consume(model, new([Message.User("three")])));
            Assert.Equal(2, handler.Requests.Count);
        }
        finally { Environment.SetEnvironmentVariable("WRITER_API_KEY", previous); }
    }

    [Fact]
    public async Task StructuredResponseBufferIsBoundedAndAlwaysClosed()
    {
        using var handler = new Handler(StructuredWire("{\"n\":42}"), json: true);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<ModelException>(() => new WriterModel(client, new("palmyra-x4", "key") { MaxResponseBytes = 8 }).InvokeStructuredAsync(new([Message.User("question")]), AnswerOptions()));

        Assert.Contains("MaxResponseBytes", error.Message, StringComparison.Ordinal);
        Assert.True(handler.Body!.Closed);
    }

    [Fact]
    public async Task BuffersToolsInFirstAppearanceOrderUntilTextStopsAndReportsLateUsageAfterMessageStop()
    {
        using var handler = new Handler(Frames(
            """{"choices":[{"index":0,"delta":{"content":"first","tool_calls":[{"index":3,"id":"b","function":{"name":"second","arguments":"{\"n\":"}},{"index":1,"id":"a","function":{"name":"first","arguments":"{}"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"last","tool_calls":[{"index":3,"function":{"arguments":"2}"}}]},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""));
        using var client = new HttpClient(handler);

        var events = await Consume(new WriterModel(client, new("palmyra-x5", "key")), new([Message.User("question")]));

        Assert.Equal(new[] { "b", "a" }, events.OfType<ModelContentBlockStartEvent>().Where(value => value.ToolUse is not null).Select(value => value.ToolUse!.ToolUseId));
        var textStop = events.FindIndex(value => value is ModelContentBlockStopEvent { Index: 0 });
        var toolStart = events.FindIndex(value => value is ModelContentBlockStartEvent { ToolUse: not null });
        Assert.True(textStop < toolStart);
        Assert.Equal("firstlast", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Where(value => value.Index == 0).Select(value => value.Text)));
        Assert.Equal("{\"n\":2}", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Where(value => value.Index == 1).Select(value => value.ToolInput)));
        Assert.IsType<ModelMessageStopEvent>(events[events.Count - 2]);
        Assert.Equal(5, Assert.IsType<ModelMetadataEvent>(events.Last()).Usage.TotalTokens);
    }

    [Fact]
    public async Task NoUsageStillEmitsThePinnedEmptyTextBlockAndZeroMetadata()
    {
        using var handler = new Handler("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        using var client = new HttpClient(handler);

        var events = await Consume(new WriterModel(client, new("palmyra-x4", "key")), new([Message.User("question")]));

        Assert.IsType<ModelMessageStartEvent>(events[0]);
        Assert.Equal(0, Assert.IsType<ModelContentBlockStartEvent>(events[1]).Index);
        Assert.Equal(0, Assert.IsType<ModelContentBlockStopEvent>(events[2]).Index);
        Assert.IsType<ModelMessageStopEvent>(events[3]);
        Assert.Equal(0, Assert.IsType<ModelMetadataEvent>(events[4]).Usage.TotalTokens);
    }

    [Theory]
    [InlineData("this model's maximum context length is 4000")]
    [InlineData("exceed context limit")]
    [InlineData("model's maximum context limit")]
    [InlineData("is longer than the model's context length")]
    [InlineData("prompt is too long")]
    [InlineData("too many tokens")]
    public async Task ClassifiesEveryPinnedWriterOverflowMessage(string detail)
    {
        using var handler = new Handler(JsonSerializer.Serialize(new { error = new { message = detail } }), json: true, status: HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ContextWindowOverflowException>(() => Consume(new WriterModel(client, new("palmyra-x4", "key")), new([Message.User("question")])));

        Assert.Single(handler.Requests);
        Assert.True(handler.Body!.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredOutputUsesJsonSchemaWithoutToolsOrStreamingOptions(bool customDecoder)
    {
        using var handler = new Handler(StructuredWire(" {\"n\":42} "), json: true);
        using var client = new HttpClient(handler);
        var options = AnswerOptions();
        if (customDecoder) options = options with { Deserializer = value => new(value.GetProperty("n").GetInt32() + 1) };

        var answer = await new WriterModel(client, new("palmyra-x4", "key") { IncludeUsage = true }).InvokeStructuredAsync(new([Message.User("question")], Tools: [new("ignored", "ignored", JsonSerializer.SerializeToElement(new { type = "object" }))]), options);

        Assert.Equal(customDecoder ? 43 : 42, answer.N);
        var body = Assert.Single(handler.Requests);
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("stream_options", out _));
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("integer", body.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("n").GetProperty("type").GetString());
        Assert.True(handler.Body!.Closed);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"n\":\"wrong\"}")]
    public async Task StructuredOutputRejectsInvalidOrSchemaMismatchedJsonWithoutRetrying(string content)
    {
        using var handler = new Handler(StructuredWire(content), json: true);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<StructuredOutputException>(() => new WriterModel(client, new("palmyra-x4", "key")).InvokeStructuredAsync(new([Message.User("question")]), AnswerOptions()));

        Assert.Single(handler.Requests);
        Assert.True(handler.Body!.Closed);
    }

    private sealed record Answer(int N);
    private static StructuredOutputOptions<Answer> AnswerOptions() => new()
    {
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { n = new { type = "integer" } }, required = new[] { "n" } })
    };
    private static string StructuredWire(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public async Task UsesWriterEndpointGenerationFieldsAndPalmyraVisionWithoutOpenAIOnlyOptions()
    {
        using var handler = new Handler(Frames("""{"choices":[{"index":0,"delta":{"content":"answer"},"finish_reason":"stop"}]}"""));
        using var client = new HttpClient(handler);
        var stops = new List<string> { "END" };
        var model = new WriterModel(client, new("palmyra-x5", "key") { MaxTokens = 100, Temperature = .4, TopP = .8, Stop = stops, IncludeUsage = true, CacheConfig = new() });
        stops.Clear();

        var events = await Consume(model, new([
            new(Role.User, [new TextBlock("one"), new ImageBlock("image/png", "AQID"), new TextBlock("two")]),
            new(Role.Assistant, [new ToolUseBlock("call", "calculator", JsonSerializer.SerializeToElement(new { n = 2 }))]),
            new(Role.User, [new ToolResultBlock("call", [new JsonBlock(JsonSerializer.SerializeToElement(new { n = 4 }))], ToolResultStatus.Error)])
        ], "instructions", [new("calculator", "calculates", JsonSerializer.SerializeToElement(new { type = "object" }))])
        { ToolChoice = new(ToolChoiceMode.None) });

        var body = Assert.Single(handler.Requests);
        Assert.Equal("https://api.writer.com/v1/chat", handler.Endpoint!.AbsoluteUri);
        Assert.Equal("key", handler.Credential);
        Assert.Equal(100, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(.4, body.GetProperty("temperature").GetDouble());
        Assert.Equal(.8, body.GetProperty("top_p").GetDouble());
        Assert.Equal("END", Assert.Single(body.GetProperty("stop").EnumerateArray()).GetString());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.TryGetProperty("prompt_cache_key", out _));
        Assert.False(body.TryGetProperty("tool_choice", out _));
        var messages = body.GetProperty("messages");
        Assert.Equal("instructions", messages[0].GetProperty("content").GetString());
        Assert.Equal("one", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,AQID", messages[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("call", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"n\":4}", messages[3].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(StopReasons.EndTurn, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        Assert.True(handler.Body!.Closed);
    }

    private static async Task<List<ModelStreamEvent>> Consume(IModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var value in model.StreamAsync(request, cancellationToken)) events.Add(value);
        return events;
    }
    private static string Frames(params string[] values) => string.Concat(values.Select(value => "data: " + value + "\n\n")) + "data: [DONE]\n\n";
    private sealed class Handler(string wire, bool json = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public Uri? Endpoint { get; private set; }
        public string? Credential { get; private set; }
        public BodyStream? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Requests.Add(document.RootElement.Clone());
            Endpoint = request.RequestUri;
            Credential = request.Headers.Authorization?.Parameter;
            Body = new(wire);
            var content = new StreamContent(Body);
            content.Headers.ContentType = new(json ? "application/json" : "text/event-stream");
            return new(status) { Content = content };
        }
    }
    private sealed class BodyStream(string wire) : MemoryStream(Encoding.UTF8.GetBytes(wire))
    {
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
