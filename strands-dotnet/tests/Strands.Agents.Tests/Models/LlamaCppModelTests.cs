using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class LlamaCppModelTests
{
    [Fact]
    public async Task NativeCountingFallsBackWhenTheServerCannotFormatNativeContent()
    {
        using var handler = new Handler("unneeded");
        using var client = new HttpClient(handler);
        var request = new ModelRequest([new(Role.Assistant, [new ReasoningBlock("reasoning remains countable locally")])]);
        var expected = await new LlamaCppModel(client).CountTokensAsync(request);

        var actual = await new LlamaCppModel(client, new() { UseNativeTokenCount = true }).CountTokensAsync(request);

        Assert.Equal(expected, actual);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NativeCountingFallsBackOnAnUnrequestedBodyTimeoutAndClosesResponse()
    {
        using var stream = new TimeoutReadStream();
        using var handler = new StreamHandler(stream, json: true);
        using var client = new HttpClient(handler);
        var request = new ModelRequest([Message.User("sufficient input")]);
        var expected = await new LlamaCppModel(client).CountTokensAsync(request);

        var actual = await new LlamaCppModel(client, new() { UseNativeTokenCount = true }).CountTokensAsync(request);

        Assert.Equal(expected, actual);
        Assert.True(stream.Closed);
    }

    [Fact]
    public async Task ProtocolFailureRetainsItsCleanupFailureAndOriginalIdentity()
    {
        var cleanup = new IOException("response cleanup");
        using var stream = new ThrowingDisposeStream("data: {broken\n\n", cleanup);
        using var handler = new StreamHandler(stream);
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<ModelException>(() => Consume(new LlamaCppModel(client), new([Message.User("question")])));

        Assert.Contains("malformed JSON", failure.Message, StringComparison.Ordinal);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task EarlyDisposalPropagatesTheActualResponseCleanupException()
    {
        var cleanup = new IOException("response cleanup");
        using var handler = new StreamHandler(new ThrowingDisposeStream(Completed("answer"), cleanup));
        using var client = new HttpClient(handler);
        var values = new LlamaCppModel(client).StreamAsync(new([Message.User("question")])).GetAsyncEnumerator();
        Assert.True(await values.MoveNextAsync());

        var failure = await Assert.ThrowsAsync<IOException>(async () => await values.DisposeAsync());

        Assert.Same(cleanup, failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInterruptsBlockedBodyReadAndIsNeverConvertedToTokenFallback(bool counting)
    {
        using var stream = new BlockedReadStream();
        using var handler = new StreamHandler(stream, counting);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var model = new LlamaCppModel(client, new() { UseNativeTokenCount = counting });
        Task operation;
        if (counting) operation = model.CountTokensAsync(new([Message.User("question")]), cancellation.Token).AsTask();
        else operation = Consume(model, new([Message.User("question")]), cancellation.Token);
        await stream.Started.Task;

        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.True(stream.Closed);
    }

    [Fact]
    public async Task ServerToolCallsDetermineToolStopEvenWhenServerReportsOrdinaryStop()
    {
        using var handler = new Handler(Frames("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":"stop"}]}"""));
        using var client = new HttpClient(handler);

        var events = await Consume(new LlamaCppModel(client), new([Message.User("question")]));

        Assert.Equal(StopReasons.ToolUse, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        Assert.Equal("call", Assert.Single(events.OfType<ModelContentBlockStartEvent>(), value => value.ToolUse is not null).ToolUse!.ToolUseId);
    }

    [Theory]
    [InlineData("data: {broken\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"audio\":{}}}]}\n\n")]
    [InlineData("data: {\"choices\":[],\"choices\":[]}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"index\":0,\"delta\":{}}]}\n\n")]
    public async Task MalformedAndIncompleteStreamsFailWithoutRetryAndReleaseBody(string wire)
    {
        using var handler = new Handler(wire);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ModelException>(() => Consume(new LlamaCppModel(client), new([Message.User("question")])));

        Assert.Single(handler.Requests);
        Assert.True(Assert.Single(handler.Bodies).Closed);
    }

    [Theory]
    [InlineData("{\"tokens\":\"wrong\"}")]
    [InlineData("{\"tokens\":[-1]}")]
    [InlineData("{\"tokens\":[3.5]}")]
    [InlineData("broken")]
    public async Task InvalidNativeTokenizationFallsBackToEstimateAndClosesBody(string wire)
    {
        using var handler = new Handler(wire, json: true);
        using var client = new HttpClient(handler);
        var request = new ModelRequest([Message.User("sufficient input")]);
        var expected = await new LlamaCppModel(client).CountTokensAsync(request);

        var count = await new LlamaCppModel(client, new() { UseNativeTokenCount = true }).CountTokensAsync(request);

        Assert.Equal(expected, count);
        Assert.Single(handler.Requests);
        Assert.True(Assert.Single(handler.Bodies).Closed);
    }

    [Fact]
    public async Task EarlyConsumerDisposalReleasesResponseWithoutTakingHttpClientOwnership()
    {
        using var handler = new Handler(Completed("answer"));
        using var client = new HttpClient(handler);
        var model = new LlamaCppModel(client);
        await using (var values = model.StreamAsync(new([Message.User("first")])).GetAsyncEnumerator())
            Assert.True(await values.MoveNextAsync());

        Assert.True(Assert.Single(handler.Bodies).Closed);
        await Consume(model, new([Message.User("second")]));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("{\"unknown\":2}")]
    [InlineData("{\"temperature\":1,\"temperature\":2}")]
    [InlineData("{\"n\":2}")]
    [InlineData("{\"n\":\"wrong\"}")]
    [InlineData("{\"n\":null}")]
    [InlineData("{\"max_tokens\":\"wrong\"}")]
    [InlineData("{\"max_tokens\":null}")]
    public void UnrepresentableOrAmbiguousConfigurationFailsBeforeHttp(string parameters)
    {
        using var client = new HttpClient();
        using var document = JsonDocument.Parse(parameters);

        Assert.Throws<ArgumentException>(() => new LlamaCppModel(client, new() { Params = document.RootElement }));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"n\":\"wrong\"}")]
    public async Task StructuredOutputValidatesSchemaAndJsonWithoutRetry(string text)
    {
        using var handler = new Handler(Completed(text));
        using var client = new HttpClient(handler);
        var options = new StructuredOutputOptions<Answer> { Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { n = new { type = "integer" } }, required = new[] { "n" } }) };

        await Assert.ThrowsAsync<StructuredOutputException>(() => new LlamaCppModel(client).InvokeStructuredAsync(new([Message.User("question")]), options));

        Assert.Single(handler.Requests);
        Assert.True(Assert.Single(handler.Bodies).Closed);
    }

    [Fact]
    public void ConfigurationSnapshotSerializesUsingStandardWebJsonConventions()
    {
        using var client = new HttpClient();
        var model = new LlamaCppModel(client);

        var json = JsonSerializer.Serialize(model.GetConfig(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        Assert.Equal("default", document.RootElement.GetProperty("modelId").GetString());
    }

    [Fact]
    public async Task ConfigurationUpdatesAreAtomicAndDoNotRewriteAnEnteredStream()
    {
        using var handler = new Handler(Completed("answer"));
        using var client = new HttpClient(handler);
        var model = new LlamaCppModel(client, new("old"));
        await using (var active = model.StreamAsync(new([Message.User("first")])).GetAsyncEnumerator())
        {
            Assert.True(await active.MoveNextAsync());
            using var parameters = JsonDocument.Parse("""{"top_k":31,"max_tokens":9}""");
            model.UpdateConfig(options => options with { ModelId = "new", Params = parameters.RootElement });
            parameters.Dispose();
            while (await active.MoveNextAsync()) { }
        }
        await Consume(model, new([Message.User("second")]));

        Assert.Equal("old", handler.Requests[0].GetProperty("model").GetString());
        Assert.Equal("new", handler.Requests[1].GetProperty("model").GetString());
        Assert.Equal(31, handler.Requests[1].GetProperty("top_k").GetInt32());
        Assert.Equal("new", model.Metadata.ModelId);
        Assert.Equal(9, model.Metadata.MaxOutputTokens);
        Assert.Equal(31, model.GetConfig().Params!.Value.GetProperty("top_k").GetInt32());
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { Params = JsonSerializer.SerializeToElement(new { n = 2 }) }));
        Assert.Equal("new", model.GetConfig().ModelId);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "context window exceeded")]
    [InlineData(HttpStatusCode.BadRequest, "kv cache exhausted")]
    [InlineData(HttpStatusCode.BadRequest, "slot exhausted")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "busy")]
    public async Task ClassifiesPinnedServerCapacityFailuresAndClosesResponse(HttpStatusCode status, string detail)
    {
        using var handler = new Handler(JsonSerializer.Serialize(new { error = new { message = detail } }), json: true, status);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAnyAsync<ModelException>(() => Consume(new LlamaCppModel(client), new([Message.User("question")])));

        if (status == HttpStatusCode.ServiceUnavailable) Assert.IsType<ModelThrottledException>(error);
        else Assert.IsType<ContextWindowOverflowException>(error);
        Assert.True(Assert.Single(handler.Bodies).Closed);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SendsNativeSamplingAndSchemaParamsWithExplicitCachePrecedence()
    {
        using var handler = new Handler(Completed("answer"));
        using var client = new HttpClient(handler);
        using var source = JsonDocument.Parse("""{"grammar":"root ::= \"yes\"","top_k":17,"mirostat":2,"cache_prompt":false,"max_tokens":40}""");
        var model = new LlamaCppModel(client, new() { Params = source.RootElement, CacheConfig = new() });
        source.Dispose();

        await Consume(model, new([Message.User("question")], "instructions"));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:8080/v1/chat/completions", handler.Uris[0].AbsoluteUri);
        Assert.Equal("default", sent.GetProperty("model").GetString());
        Assert.Equal(17, sent.GetProperty("top_k").GetInt32());
        Assert.Equal(2, sent.GetProperty("mirostat").GetInt32());
        Assert.False(sent.GetProperty("cache_prompt").GetBoolean());
        Assert.Equal("root ::= \"yes\"", sent.GetProperty("grammar").GetString());
        Assert.Equal("instructions", sent.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.True(sent.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Empty(sent.GetProperty("tools").EnumerateArray());
        Assert.Equal(40, model.Metadata.MaxOutputTokens);
    }

    [Fact]
    public async Task KeepsRichToolResultsAndNativeMediaInTheirOriginalOrder()
    {
        using var handler = new Handler(Completed("answer"));
        using var client = new HttpClient(handler) { BaseAddress = new("http://llama.example:8081/custom/") };
        var request = new ModelRequest([
            new(Role.User, [new TextBlock("question"), new ImageBlock("image/png", "AQID"), new AudioBlock("audio/wav", "BAUG"), new DocumentBlock("application/pdf", "BwgJ", "report.pdf")]),
            new(Role.Assistant, [new ToolUseBlock("id", "lookup", JsonSerializer.SerializeToElement(new { query = "雪" }))]),
            new(Role.User, [new ToolResultBlock("id", [new JsonBlock(JsonSerializer.SerializeToElement(new { answer = 3 })), new ImageBlock("image/png", "AQID")])])
        ]);

        await Consume(new LlamaCppModel(client), request);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("http://llama.example:8081/v1/chat/completions", handler.Uris[0].AbsoluteUri);
        var input = sent.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(new[] { "text", "image_url", "input_audio", "file" }, input.EnumerateArray().Select(value => value.GetProperty("type").GetString()));
        Assert.Equal("data:image/png;base64,AQID", input[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("wav", input[2].GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal("report.pdf", input[3].GetProperty("file").GetProperty("filename").GetString());
        var result = sent.GetProperty("messages")[2];
        Assert.Equal("tool", result.GetProperty("role").GetString());
        Assert.Equal("id", result.GetProperty("tool_call_id").GetString());
        Assert.Equal("{\"answer\":3}", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("image_url", result.GetProperty("content")[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task BuffersToolsUntilTextEndsAndDrainsUsageAfterFinish()
    {
        using var handler = new Handler(Frames(
            """{"choices":[{"index":0,"delta":{"content":"before","tool_calls":[{"index":4,"id":"id","function":{"name":"lookup","arguments":"{\"query\":"}}]}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"after","tool_calls":[{"index":4,"function":{"arguments":"\"雪\"}"}}]},"finish_reason":"tool_calls"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":9,"completion_tokens":4,"total_tokens":13,"prompt_tokens_details":{"cached_tokens":6}}}"""));
        using var client = new HttpClient(handler);

        var events = await Consume(new LlamaCppModel(client), new([Message.User("question")]));

        var textStop = events.FindIndex(value => value is ModelContentBlockStopEvent { Index: 0 });
        var toolStart = events.FindIndex(value => value is ModelContentBlockStartEvent { ToolUse: not null });
        Assert.True(textStop < toolStart);
        Assert.Equal("beforeafter", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
        Assert.Equal("{\"query\":\"雪\"}", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.ToolInput)));
        Assert.Equal(StopReasons.ToolUse, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        var usage = Assert.Single(events.OfType<ModelMetadataEvent>()).Usage;
        Assert.Equal(13, usage.TotalTokens);
        Assert.Equal(6, usage.CacheReadInputTokens);
        Assert.True(handler.Bodies.All(body => body.Closed));
    }

    [Fact]
    public async Task NativeTokenizerIncludesChatTemplateMessagesAndToolsOnlyWhenOptedIn()
    {
        using var handler = new Handler("""{"tokens":[3,7,11]}""", json: true);
        using var client = new HttpClient(handler) { BaseAddress = new("http://llama.example:8080/") };
        var request = new ModelRequest([Message.User("question")], "instructions", [new("lookup", "Looks up", JsonSerializer.SerializeToElement(new { type = "object" }))]);
        var estimated = await new LlamaCppModel(client).CountTokensAsync(request);
        Assert.True(estimated > 0);
        Assert.Empty(handler.Requests);

        var count = await new LlamaCppModel(client, new() { UseNativeTokenCount = true }).CountTokensAsync(request);

        Assert.Equal(3, count);
        Assert.Equal("http://llama.example:8080/tokenize", Assert.Single(handler.Uris).AbsoluteUri);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("instructions", sent.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Single(sent.GetProperty("tools").EnumerateArray());
        Assert.False(sent.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task StructuredConstraintIsInvocationLocalAndValidatedThroughCustomDecoder()
    {
        using var handler = new Handler(Completed("{\"n\":4}"));
        using var client = new HttpClient(handler);
        var model = new LlamaCppModel(client, new() { Params = JsonSerializer.SerializeToElement(new { cache_prompt = false, temperature = .2 }) });
        var options = new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { n = new { type = "integer" } }, required = new[] { "n" } }),
            Deserializer = value => new(value.GetProperty("n").GetInt32() + 1)
        };

        var output = await model.InvokeStructuredAsync(new([Message.User("question")]), options);
        await Consume(model, new([Message.User("ordinary")]));

        Assert.Equal(5, output.N);
        Assert.True(handler.Requests[0].GetProperty("cache_prompt").GetBoolean());
        Assert.Equal("integer", handler.Requests[0].GetProperty("json_schema").GetProperty("properties").GetProperty("n").GetProperty("type").GetString());
        Assert.False(handler.Requests[1].GetProperty("cache_prompt").GetBoolean());
        Assert.False(handler.Requests[1].TryGetProperty("json_schema", out _));
    }

    private sealed record Answer(int N);
    private static string Completed(string content) => Frames(JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content }, finish_reason = "stop" } } }));
    private static string Frames(params string[] values) => string.Concat(values.Select(value => "data: " + value + "\n\n")) + "data: [DONE]\n\n";
    private static async Task<List<ModelStreamEvent>> Consume(IModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var value in model.StreamAsync(request, cancellationToken)) result.Add(value);
        return result;
    }
    private sealed class Handler(string wire, bool json = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<BodyStream> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Requests.Add(document.RootElement.Clone());
            Uris.Add(request.RequestUri!);
            var body = new BodyStream(wire);
            Bodies.Add(body);
            var content = new StreamContent(body);
            content.Headers.ContentType = new(json ? "application/json" : "text/event-stream");
            return new(status) { Content = content };
        }
    }
    private sealed class BodyStream(string wire) : MemoryStream(Encoding.UTF8.GetBytes(wire))
    {
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class ThrowingDisposeStream(string wire, Exception failure) : MemoryStream(Encoding.UTF8.GetBytes(wire))
    {
        private bool _closed;
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!_closed) { _closed = true; throw failure; }
        }
    }
    private sealed class StreamHandler(Stream stream, bool json = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new(json ? "application/json" : "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class BlockedReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return _read.Task;
        }
        protected override void Dispose(bool disposing) { Closed = true; _read.TrySetException(new ObjectDisposedException("body")); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class TimeoutReadStream : Stream
    {
        public bool Closed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(new TaskCanceledException("server read timeout"));
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
