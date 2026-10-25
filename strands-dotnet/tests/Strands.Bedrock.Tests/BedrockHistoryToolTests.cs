using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockHistoryToolTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AbsentCurrentToolsUseTheHistoryFallbackOnTheOfficialWire(bool streaming, bool emptyTools)
    {
        await using var server = new HistoryServer(streaming);
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new() { ModelId = "fixture-model", Streaming = streaming, RequestTimeout = TimeSpan.FromSeconds(15) });
        var input = JsonSerializer.SerializeToElement(new { item = 7 });
        var messages = new List<Message>
        {
            Message.User("Look up item seven."),
            new(Role.Assistant, [new ToolUseBlock("history-7", "historical_lookup", input)]),
            new(Role.User, [new ToolResultBlock("history-7", "saved result")]),
            Message.User("Summarize the saved result.")
        };
        IReadOnlyList<ToolSpec>? tools = emptyTools ? Array.Empty<ToolSpec>() : null;
        var request = new ModelRequest(messages) { Tools = tools };
        var text = new StringBuilder();
        await foreach (var item in model.StreamAsync(request))
            if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
        await server.Completion;

        Assert.Equal("summary", text.ToString());
        Assert.Equal(streaming ? "/model/fixture-model/converse-stream" : "/model/fixture-model/converse", server.Target);
        using var payload = JsonDocument.Parse(server.Body);
        var root = payload.RootElement;
        var history = root.GetProperty("messages");
        Assert.Equal(4, history.GetArrayLength());
        Assert.Equal("historical_lookup", history[1].GetProperty("content")[0].GetProperty("toolUse").GetProperty("name").GetString());
        Assert.Equal("history-7", history[2].GetProperty("content")[0].GetProperty("toolResult").GetProperty("toolUseId").GetString());
        Assert.Equal("saved result", history[2].GetProperty("content")[0].GetProperty("toolResult").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Summarize the saved result.", history[3].GetProperty("content")[0].GetProperty("text").GetString());
        output.WriteLine(JsonSerializer.Serialize(new { OfficialAwsHttp = true, Streaming = streaming, EmptyTools = emptyTools, server.Target, server.Signed, Request = root }));

        Assert.True(root.TryGetProperty("toolConfig", out var config), "The actual AWS wire request must include the history fallback toolConfig.");
        var specification = Assert.Single(config.GetProperty("tools").EnumerateArray()).GetProperty("toolSpec");
        Assert.Equal("noop", specification.GetProperty("name").GetString());
        Assert.Equal("This is a fake tool that MUST be completely ignored.", specification.GetProperty("description").GetString());
        var schema = specification.GetProperty("inputSchema").GetProperty("json");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.Equal(2, schema.EnumerateObject().Count());
        Assert.False(config.TryGetProperty("toolChoice", out _));
        Assert.Same(tools, request.Tools);
        Assert.True(tools is null || tools.Count == 0);
        Assert.Equal(input, Assert.IsType<ToolUseBlock>(messages[1].Content[0]).Input);
    }

    [Theory]
    [InlineData(false, "text")]
    [InlineData(true, "text")]
    [InlineData(false, "literalJson")]
    [InlineData(true, "literalJson")]
    [InlineData(false, "reasoning")]
    [InlineData(true, "reasoning")]
    [InlineData(false, "cache")]
    [InlineData(true, "cache")]
    public async Task NonToolHistoryDoesNotCreateAConfiguration(bool streaming, string kind)
    {
        IReadOnlyList<Message> messages = kind switch
        {
            "literalJson" => [Message.User("{\"toolUse\":{\"name\":\"lookup\"},\"toolResult\":{}}")],
            "reasoning" => [new(Role.Assistant, [new ReasoningBlock("toolUse and toolResult", "signed")]), Message.User("summary")],
            "cache" => [new(Role.User, [new TextBlock("toolUse"), new CachePointBlock("1h")])],
            _ => [Message.User("The words toolUse and toolResult are only text.")]
        };
        var wire = await CaptureAsync(new(messages), streaming, new() { CacheTools = true });
        Assert.False(wire.TryGetProperty("toolConfig", out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CurrentSpecificationsWinInTheirOriginalOrderIncludingAnApplicationNoop(bool streaming, bool noop)
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"item":{"type":"integer"}},"required":["item"]}""");
        var tools = new List<ToolSpec> { new("second", "Second selected tool", schema), new(noop ? "noop" : "first", "Application definition", schema) };
        var request = new ModelRequest(History(), Tools: tools);
        var wire = await CaptureAsync(request, streaming);
        var specifications = wire.GetProperty("toolConfig").GetProperty("tools");
        Assert.Equal(new[] { "second", noop ? "noop" : "first" }, specifications.EnumerateArray().Select(value => value.GetProperty("toolSpec").GetProperty("name").GetString()));
        var selected = specifications[1].GetProperty("toolSpec");
        Assert.Equal("Application definition", selected.GetProperty("description").GetString());
        Assert.Equal("integer", selected.GetProperty("inputSchema").GetProperty("json").GetProperty("properties").GetProperty("item").GetProperty("type").GetString());
        Assert.Same(tools, request.Tools);
        Assert.Equal(2, tools.Count);
        Assert.Equal(schema.GetRawText(), tools[1].InputSchema.GetRawText());
    }

    [Theory]
    [InlineData(false, "default")]
    [InlineData(true, "default")]
    [InlineData(false, "auto")]
    [InlineData(true, "auto")]
    [InlineData(false, "any")]
    [InlineData(true, "any")]
    [InlineData(false, "tool")]
    [InlineData(true, "tool")]
    public async Task FallbackPreservesTheExactRequestedChoice(bool streaming, string mode)
    {
        var choice = mode switch { "auto" => new ToolChoice(ToolChoiceMode.Auto), "any" => new ToolChoice(ToolChoiceMode.Any), "tool" => new ToolChoice(ToolChoiceMode.Tool, "historical_lookup"), _ => null };
        var wire = await CaptureAsync(new(History()) { ToolChoice = choice }, streaming);
        var config = wire.GetProperty("toolConfig");
        Assert.Equal("noop", config.GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
        if (mode == "default") Assert.False(config.TryGetProperty("toolChoice", out _));
        else
        {
            var native = config.GetProperty("toolChoice");
            Assert.Equal(mode, Assert.Single(native.EnumerateObject()).Name);
            if (mode == "tool") Assert.Equal("historical_lookup", native.GetProperty("tool").GetProperty("name").GetString());
            else Assert.Empty(native.GetProperty(mode).EnumerateObject());
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task NativeNoneRemainsAuthoritativeForAbsentEmptyAndNonemptySpecifications(bool streaming, int kind)
    {
        IReadOnlyList<ToolSpec>? tools = kind switch { 0 => null, 1 => Array.Empty<ToolSpec>(), _ => [new("current", "Current tool", JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}"))] };
        var wire = await CaptureAsync(new(History(), Tools: tools) { ToolChoice = new(ToolChoiceMode.None) }, streaming, new() { CacheTools = true });
        Assert.False(wire.TryGetProperty("toolConfig", out _));
        Assert.Equal("historical_lookup", wire.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("toolUse").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EitherHistoricalBlockSelectsTheFallbackWithoutAssertingConversationValidity(bool resultOnly)
    {
        ContentBlock block = resultOnly ? new ToolResultBlock("orphan", "prior") : new ToolUseBlock("orphan", "lookup", JsonSerializer.Deserialize<JsonElement>("{}"));
        using var mapper = new BedrockRequestMapper();
        var native = mapper.Map(new([new(resultOnly ? Role.User : Role.Assistant, [block])]), new());
        Assert.Equal("noop", Assert.Single(native.ToolConfig.Tools).ToolSpec.Name);
    }

    [Theory]
    [InlineData(false, false, "5m")]
    [InlineData(true, false, "5m")]
    [InlineData(false, true, "5m")]
    [InlineData(true, true, "5m")]
    [InlineData(false, false, "1h")]
    [InlineData(true, false, "1h")]
    [InlineData(false, true, "1h")]
    [InlineData(true, true, "1h")]
    public async Task StrictSchemaAndCacheUseTheOrdinaryMappingAfterTheFallback(bool streaming, bool strict, string ttl)
    {
        var wire = await CaptureAsync(new(History()), streaming, new() { StrictTools = strict, CacheTools = true, CacheTtl = ttl });
        var tools = wire.GetProperty("toolConfig").GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        var spec = tools[0].GetProperty("toolSpec");
        Assert.Equal("noop", spec.GetProperty("name").GetString());
        Assert.Equal(strict, spec.TryGetProperty("strict", out var flag));
        if (strict)
        {
            Assert.True(flag.GetBoolean());
            Assert.False(spec.GetProperty("inputSchema").GetProperty("json").GetProperty("additionalProperties").GetBoolean());
        }
        else Assert.False(spec.GetProperty("inputSchema").GetProperty("json").TryGetProperty("additionalProperties", out _));
        Assert.Equal("default", tools[1].GetProperty("cachePoint").GetProperty("type").GetString());
        Assert.Equal(ttl, tools[1].GetProperty("cachePoint").GetProperty("ttl").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedMappingDoesNotAccumulateToolsOrLeakFallbackIntoATextOnlyRequest(bool strict)
    {
        using var mapper = new BedrockRequestMapper();
        var request = new ModelRequest(History(), Tools: Array.Empty<ToolSpec>());
        var options = new BedrockModelOptions { StrictTools = strict, CacheTools = true };
        var first = mapper.Map(request, options);
        var second = mapper.Map(request, options);
        Assert.NotSame(first.ToolConfig, second.ToolConfig);
        Assert.NotSame(first.ToolConfig.Tools, second.ToolConfig.Tools);
        Assert.Equal(2, first.ToolConfig.Tools.Count);
        Assert.Equal(2, second.ToolConfig.Tools.Count);
        Assert.Null(mapper.Map(new([Message.User("text")]), options).ToolConfig);
        Assert.Empty(request.Tools!);
    }

    [Theory]
    [InlineData(false, "false")]
    [InlineData(true, "false")]
    [InlineData(false, "0")]
    [InlineData(true, "0")]
    public async Task RichCachedHistoryAndFalsyStructuredResultsRemainIntactAndLocalMetadataStaysLocal(bool streaming, string structured)
    {
        var value = JsonSerializer.Deserialize<JsonElement>(structured);
        var metadata = JsonSerializer.Deserialize<JsonElement>("{\"internalMarker\":\"keep-local\"}");
        var result = new ToolResultBlock("history-7", [new TextBlock("answer"), new JsonBlock(JsonSerializer.Deserialize<JsonElement>("{\"kept\":true}"))], structuredContent: value) { Metadata = metadata };
        var request = new ModelRequest([
            Message.User("start"),
            new(Role.Assistant, [new ReasoningBlock("earlier reasoning", "original-signature"), new ToolUseBlock("history-7", "historical_lookup", JsonSerializer.Deserialize<JsonElement>("{}"))]),
            new(Role.User, [new TextBlock("evidence"), new ImageBlock("image/png", "AQID"), new CachePointBlock("1h"), result])]);
        var before = JsonSerializer.Serialize(request);
        var wire = await CaptureAsync(request, streaming);
        var messages = wire.GetProperty("messages");
        Assert.Equal("original-signature", messages[1].GetProperty("content")[0].GetProperty("reasoningContent").GetProperty("reasoningText").GetProperty("signature").GetString());
        var user = messages[2].GetProperty("content");
        Assert.Equal("AQID", user[1].GetProperty("image").GetProperty("source").GetProperty("bytes").GetString());
        Assert.Equal("1h", user[2].GetProperty("cachePoint").GetProperty("ttl").GetString());
        var content = user[3].GetProperty("toolResult").GetProperty("content");
        Assert.True(content[1].GetProperty("json").GetProperty("kept").GetBoolean());
        Assert.Equal(structured, content[2].GetProperty("json").GetRawText());
        Assert.DoesNotContain("internalMarker", wire.GetRawText());
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("keep-local", result.Metadata!.Value.GetProperty("internalMarker").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAgentDoesNotAcquireACallableNoopWhenItsCurrentRegistryIsEmpty(bool streaming)
    {
        await using var server = new HistoryServer(streaming);
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new() { ModelId = "fixture-model", Streaming = streaming });
        await using var agent = new Agent(new AgentOptions { Model = model, Messages = History() });
        Assert.Empty(agent.Tools);
        var result = await agent.InvokeAsync("Summarize now.");
        await server.Completion;
        Assert.Equal("summary", result.Message!.Text);
        Assert.Empty(agent.Tools);
        using var wire = JsonDocument.Parse(server.Body);
        Assert.Equal("noop", wire.RootElement.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
        Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>(), tool => tool.Name == "noop");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCountingUsesTheSameFallbackAndPreservesNone(bool none)
    {
        await using var server = new HistoryServer(false, countTokens: true);
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new() { ModelId = "fixture-model", UseNativeTokenCount = true, TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });
        Assert.Equal(9, await model.CountTokensAsync(new(History()) { ToolChoice = none ? new(ToolChoiceMode.None) : null }));
        await server.Completion;
        using var wire = JsonDocument.Parse(server.Body);
        Assert.Equal("/model/fixture-model/count-tokens", server.Target);
        var converse = wire.RootElement.GetProperty("input").GetProperty("converse");
        Assert.Equal(!none, converse.TryGetProperty("toolConfig", out var config));
        if (!none) Assert.Equal("noop", config.GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledRequestsRetainTheExactCallerTokenAndDoNotReachHttp(bool streaming)
    {
        await using var server = new HistoryServer(streaming);
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new() { Streaming = streaming });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model, new(History()), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(server.Signed);
        Assert.Empty(server.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeServiceFailuresKeepTheirCauseWithTheFallbackRequest(bool streaming)
    {
        await using var server = new HistoryServer(streaming, error: true);
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, new() { ModelId = "fixture-model", Streaming = streaming });
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, new(History())));
        await server.Completion;
        Assert.IsType<Amazon.BedrockRuntime.Model.ValidationException>(error.InnerException);
        Assert.Contains("fixture validation failure", error.Message);
        using var wire = JsonDocument.Parse(server.Body);
        Assert.Equal("noop", wire.RootElement.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingAModelKeepsItsBorrowedClientUsableForAnotherFallback(bool streaming)
    {
        await using var server = new HistoryServer(streaming, requests: 2);
        using var client = server.CreateClient();
        using (var first = new BedrockModel(client, new() { ModelId = "fixture-model", Streaming = streaming }))
            Assert.Equal("summary", await CollectAsync(first, new(History())));
        using (var second = new BedrockModel(client, new() { ModelId = "fixture-model", Streaming = streaming }))
            Assert.Equal("summary", await CollectAsync(second, new(History())));
        await server.Completion;
        Assert.Equal(2, server.Bodies.Count);
        Assert.All(server.Bodies, body =>
        {
            using var wire = JsonDocument.Parse(body);
            Assert.Single(wire.RootElement.GetProperty("toolConfig").GetProperty("tools").EnumerateArray());
        });
    }

    private async Task<JsonElement> CaptureAsync(ModelRequest request, bool streaming, BedrockModelOptions? options = null)
    {
        await using var server = new HistoryServer(streaming);
        using var client = server.CreateClient();
        using var model = new BedrockModel(client, (options ?? new()) with { ModelId = "fixture-model", Streaming = streaming });
        Assert.Equal("summary", await CollectAsync(model, request));
        await server.Completion;
        using var payload = JsonDocument.Parse(server.Body);
        output.WriteLine(JsonSerializer.Serialize(new { OfficialAwsHttp = true, Streaming = streaming, server.Target, server.Signed, Request = payload.RootElement }));
        return payload.RootElement.Clone();
    }

    private static IReadOnlyList<Message> History() => [
        Message.User("Look up item seven."),
        new(Role.Assistant, [new ToolUseBlock("history-7", "historical_lookup", JsonSerializer.Deserialize<JsonElement>("{\"item\":7}"))]),
        new(Role.User, [new ToolResultBlock("history-7", "saved result")]),
        Message.User("Summarize the saved result.")];

    private static async Task<string> CollectAsync(IModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var item in model.StreamAsync(request, cancellationToken))
            if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
        return text.ToString();
    }

    private sealed class HistoryServer : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly bool _streaming;
        private readonly bool _countTokens;
        private readonly bool _error;
        private readonly int _requests;
        private readonly string _url;
        private TcpClient? _accepted;
        private bool _closed;
        internal Task Completion { get; }
        internal string Target { get; private set; } = "";
        internal string Body { get; private set; } = "";
        internal bool Signed { get; private set; }
        internal List<string> Bodies { get; } = [];

        internal HistoryServer(bool streaming, bool countTokens = false, bool error = false, int requests = 1)
        {
            _streaming = streaming;
            _countTokens = countTokens;
            _error = error;
            _requests = requests;
            _listener.Start();
            _url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            Completion = ServeAsync();
        }

        internal AmazonBedrockRuntimeClient CreateClient() => new(new BasicAWSCredentials("fixture", "fixture-secret"), new AmazonBedrockRuntimeConfig
        {
            ServiceURL = _url,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(15)
        });

        private async Task ServeAsync()
        {
            using var registration = _stop.Token.Register(_listener.Stop);
            try
            {
                for (var iteration = 0; iteration < _requests; iteration++)
                {
                    using var socket = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_closed) return;
                        _accepted = socket;
                    }
                    using var stream = socket.GetStream();
                    using var header = new MemoryStream();
                    var one = new byte[1];
                    while (true)
                    {
                        if (await stream.ReadAsync(one, 0, 1, _stop.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
                        header.WriteByte(one[0]);
                        var bytes = header.GetBuffer();
                        var length = (int)header.Length;
                        if (length >= 4 && bytes[length - 4] == 13 && bytes[length - 3] == 10 && bytes[length - 2] == 13 && bytes[length - 1] == 10) break;
                        if (length > 65536) throw new InvalidDataException("The loopback request header exceeded its bound.");
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    Target = first[1];
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1))
                    {
                        var colon = line.IndexOf(':');
                        if (colon >= 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                    }
                    Signed = headers.TryGetValue("Authorization", out var signature) && signature.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal);
                    if (!Signed || !headers.TryGetValue("Content-Length", out var rawLength) || !int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out var bodyLength) || bodyLength is < 0 or > 65536)
                        throw new InvalidDataException("The loopback fixture requires a signed bounded AWS request.");
                    if (headers.TryGetValue("Expect", out var expect) && string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
                        await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n")).ConfigureAwait(false);
                    var body = new byte[bodyLength];
                    for (var offset = 0; offset < body.Length;)
                    {
                        var count = await stream.ReadAsync(body, offset, body.Length - offset, _stop.Token).ConfigureAwait(false);
                        if (count == 0) throw new EndOfStreamException();
                        offset += count;
                    }
                    Body = Encoding.UTF8.GetString(body);
                    Bodies.Add(Body);
                    var response = _error ? Encoding.UTF8.GetBytes("{\"message\":\"fixture validation failure\"}") : _countTokens ? Encoding.UTF8.GetBytes("{\"inputTokens\":9}") : _streaming ? StreamResponse() : Encoding.UTF8.GetBytes("""{"output":{"message":{"role":"assistant","content":[{"text":"summary"}]}},"stopReason":"end_turn","usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5},"metrics":{"latencyMs":1}}""");
                    var contentType = _streaming && !_error ? "application/vnd.amazon.eventstream" : "application/json";
                    var status = _error ? "400 Bad Request\r\nx-amzn-ErrorType: ValidationException" : "200 OK";
                    await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Type: " + contentType + "\r\nContent-Length: " + response.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n")).ConfigureAwait(false);
                    await WriteAsync(stream, response).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (_stop.IsCancellationRequested && error is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException) { }
        }

        private Task WriteAsync(Stream stream, byte[] bytes) => stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token);

        private static byte[] StreamResponse()
        {
            using var output = new MemoryStream();
            foreach (var (type, json) in new[]
            {
                ("messageStart", """{"role":"assistant"}"""),
                ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"summary"}}"""),
                ("contentBlockStop", """{"contentBlockIndex":0}"""),
                ("messageStop", """{"stopReason":"end_turn"}"""),
                ("metadata", """{"usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5},"metrics":{"latencyMs":1}}""")
            })
            {
                var messageType = new EventStreamHeader(":message-type"); messageType.SetString("event");
                var eventType = new EventStreamHeader(":event-type"); eventType.SetString(type);
                var contentType = new EventStreamHeader(":content-type"); contentType.SetString("application/json");
                var frame = new EventStreamMessage(new List<IEventStreamHeader> { messageType, eventType, contentType }, Encoding.UTF8.GetBytes(json)).ToByteArray();
                output.Write(frame, 0, frame.Length);
            }
            return output.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            TcpClient? accepted;
            lock (_gate) { _closed = true; accepted = _accepted; }
            _stop.Cancel();
            _listener.Stop();
            accepted?.Dispose();
            try { await Completion.ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }
}
