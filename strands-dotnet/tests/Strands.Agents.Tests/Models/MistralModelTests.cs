using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class MistralModelTests
{
    [Fact]
    public async Task UsesMistralGenerationMediaToolAndCacheFieldsWithoutOpenAIOnlyOptions()
    {
        using var handler = new Handler(Frames("""{"choices":[{"index":0,"delta":{"content":"answer"},"finish_reason":"stop"}]}"""));
        using var client = new HttpClient(handler);
        var model = new MistralModel(client, new("mistral-large-latest", "key") { MaxTokens = 100, Temperature = .4, TopP = .8, CacheConfig = new() });
        var request = new ModelRequest([
            new(Role.User, [new TextBlock("one"), new TextBlock("two"), new ImageBlock("image/png", "AQID")]),
            new(Role.Assistant, [new ToolUseBlock("calculator_123", "calculator", JsonSerializer.SerializeToElement(new { n = 2 }))]),
            new(Role.User, [new ToolResultBlock("calculator_123", [new TextBlock("four"), new JsonBlock(JsonSerializer.SerializeToElement(new { result = 4 }))], ToolResultStatus.Error)])
        ], "instructions", [new("calculator", "calculates", JsonSerializer.SerializeToElement(new { type = "object" }))])
        { SessionId = "session", ToolChoice = new(ToolChoiceMode.None) };

        var events = await Consume(model, request);

        var body = Assert.Single(handler.Requests);
        Assert.Equal("https://api.mistral.ai/v1/chat/completions", handler.Endpoint!.AbsoluteUri);
        Assert.Equal("key", handler.Credential);
        Assert.Equal(100, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(.4, body.GetProperty("temperature").GetDouble());
        Assert.Equal(.8, body.GetProperty("top_p").GetDouble());
        Assert.Equal("strands-session", body.GetProperty("prompt_cache_key").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.TryGetProperty("stream_options", out _));
        Assert.False(body.TryGetProperty("tool_choice", out _));
        var messages = body.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("one", messages[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,AQID", messages[1].GetProperty("content")[2].GetProperty("image_url").GetString());
        Assert.Equal("calculator", messages[3].GetProperty("name").GetString());
        Assert.Equal("calculator_123", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("four\n{\"result\":4}", messages[3].GetProperty("content").GetString());
        Assert.Equal(StopReasons.EndTurn, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
    }

    [Fact]
    public async Task TextOnlyMessagesUseThePinnedSpaceJoinAndRetainUnicode()
    {
        using var handler = new Handler(Frames("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));
        using var client = new HttpClient(handler);
        var model = new MistralModel(client, new("model", "key"));

        await Consume(model, new([new(Role.User, [new TextBlock("東京"), new TextBlock("world")])]));

        Assert.Equal("東京 world", Assert.Single(handler.Requests).GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ParallelToolsRetainIndicesFragmentsAndReportedUsage()
    {
        var first = """{"choices":[{"index":0,"delta":{"role":"assistant","content":"thinking","tool_calls":[{"index":1,"id":"second","type":"function","function":{"name":"b","arguments":"{\"x\":"}},{"index":0,"id":"first","type":"function","function":{"name":"a","arguments":"{"}}]}}]}""";
        var last = """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"}"}},{"index":1,"function":{"arguments":"2}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""";
        using var handler = new Handler(Frames(first, last));
        using var client = new HttpClient(handler);

        var events = await Consume(new MistralModel(client, new("model", "key")), new([Message.User("question")]));

        Assert.Equal(new[] { "second", "first" }, events.OfType<ModelContentBlockStartEvent>().Where(value => value.ToolUse is not null).Select(value => value.ToolUse!.ToolUseId));
        Assert.Equal(new[] { 0, 1, 2 }, events.OfType<ModelContentBlockStopEvent>().Select(value => value.Index));
        Assert.Equal("{\"x\":2}", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Where(value => value.Index == 1).Select(value => value.ToolInput)));
        Assert.Equal(StopReasons.ToolUse, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        Assert.Equal(5, Assert.Single(events.OfType<ModelMetadataEvent>()).Usage.TotalTokens);
    }

    [Fact]
    public async Task CompletedToolOrderFollowsFirstAppearanceAsInThePinnedMistralAdapter()
    {
        using var handler = new Handler(Frames("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"second","type":"function","function":{"name":"b","arguments":"{}"}},{"index":0,"id":"first","type":"function","function":{"name":"a","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""));
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new MistralModel(client, new("model", "key")), MaxTurns = 1 });

        await agent.InvokeAsync("question");

        Assert.Equal(new[] { "second", "first" }, Assert.Single(agent.Messages, value => value.Role == Role.Assistant).Content.OfType<ToolUseBlock>().Select(value => value.ToolUseId));
        Assert.Equal(new[] { "second", "first" }, agent.Messages.Last().Content.OfType<ToolResultBlock>().Select(value => value.ToolUseId));
    }

    [Fact]
    public async Task NonStreamingJsonAdaptsObjectToolArgumentsWithoutLosingUsage()
    {
        using var handler = new Handler("""{"choices":[{"index":0,"message":{"role":"assistant","content":"answer","tool_calls":[{"id":"call","type":"function","function":{"name":"tool","arguments":{"city":"東京"}}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":7,"completion_tokens":4,"total_tokens":11}}""", json: true);
        using var client = new HttpClient(handler);

        var events = await Consume(new MistralModel(client, new("model", "key") { Stream = false }), new([Message.User("question")]));

        Assert.False(Assert.Single(handler.Requests).GetProperty("stream").GetBoolean());
        var arguments = string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Where(value => value.ToolInput is not null).Select(value => value.ToolInput));
        using var parsed = JsonDocument.Parse(arguments);
        Assert.Equal("東京", parsed.RootElement.GetProperty("city").GetString());
        Assert.Equal(11, Assert.Single(events.OfType<ModelMetadataEvent>()).Usage.TotalTokens);
        Assert.Equal(StopReasons.ToolUse, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
    }

    [Theory]
    [InlineData("data: {broken\n\n")]
    [InlineData("data: {\"choices\":[],\"choices\":[]}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"unfinished\"}}]}\n\n")]
    public async Task IncompleteOrMalformedResponsesFailAndCloseTheBody(string wire)
    {
        using var handler = new Handler(wire);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ModelException>(() => Consume(new MistralModel(client, new("model", "key")), new([Message.User("question")])));

        Assert.True(handler.Body!.Closed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(-.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void RejectsTemperatureOutsideThePinnedMistralRange(double temperature)
    {
        using var client = new HttpClient();

        Assert.Throws<ArgumentOutOfRangeException>(() => new MistralModel(client, new("model", "key") { Temperature = temperature }));
    }

    [Fact]
    public async Task AlreadyCancelledCallsDoNotStartHttp()
    {
        using var handler = new Handler("");
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Consume(new MistralModel(client, new("model", "key")), new([]), cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("too large for model", false)]
    [InlineData("maximum context length", false)]
    [InlineData("too large for model", true)]
    [InlineData("maximum context length", true)]
    public async Task NativeOverflowPhrasesProduceTheContextOverflowContract(string phrase, bool httpFailure)
    {
        var error = JsonSerializer.Serialize(new { error = new { message = phrase } });
        using var handler = new Handler(httpFailure ? error : Frames(error), status: httpFailure ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ContextWindowOverflowException>(() => Consume(new MistralModel(client, new("model", "key")), new([Message.User("question")])));

        Assert.Contains(phrase, failure.Message);
        Assert.True(handler.Body!.Closed);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CompleteResponseBoundsAreEnforcedBeforeJsonMaterialization()
    {
        using var handler = new Handler(new string(' ', 129), json: true);
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ModelException>(() => Consume(new MistralModel(client, new("model", "key") { Stream = false, MaxResponseBytes = 128 }), new([])));

        Assert.Contains("MaxResponseBytes", failure.Message);
        Assert.True(handler.Body!.Closed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredOutputUsesOneForcedNonStreamingCallAndTheSharedTypedSchemaDecoder(bool customDecoder)
    {
        using var handler = new Handler(StructuredWire("strands_structured_output", "{\"n\":42}"), json: true);
        using var client = new HttpClient(handler);
        var model = new MistralModel(client, new("model", "key"));
        var options = customDecoder ? AnswerOptions() with { Deserializer = value => new(value.GetProperty("n").GetInt32() + 1) } : AnswerOptions();

        var answer = await model.InvokeStructuredAsync(new([Message.User("question")]), options);

        Assert.Equal(customDecoder ? 43 : 42, answer.N);
        var body = Assert.Single(handler.Requests);
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.False(body.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal("any", body.GetProperty("tool_choice").GetString());
        Assert.Equal("strands_structured_output", Assert.Single(body.GetProperty("tools").EnumerateArray()).GetProperty("function").GetProperty("name").GetString());
        Assert.True(handler.Body!.Closed);
    }

    [Theory]
    [InlineData("strands_structured_output", "{\"n\":\"wrong\"}")]
    [InlineData("unrequested_tool", "{\"n\":42}")]
    public async Task StructuredOutputRejectsInvalidOrUnrequestedCallsWithoutRetrying(string name, string arguments)
    {
        using var handler = new Handler(StructuredWire(name, arguments), json: true);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<StructuredOutputException>(() => new MistralModel(client, new("model", "key")).InvokeStructuredAsync(new([Message.User("question")]), AnswerOptions()));

        Assert.Single(handler.Requests);
        Assert.True(handler.Body!.Closed);
    }

    private sealed record Answer(int N);
    private static StructuredOutputOptions<Answer> AnswerOptions() => new()
    {
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { n = new { type = "integer" } }, required = new[] { "n" } })
    };
    private static string StructuredWire(string name, string arguments) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { index = 0, message = new { role = "assistant", content = "", tool_calls = new[] { new { id = "output", type = "function", function = new { name, arguments } } } }, finish_reason = "tool_calls" } }
    });

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
