using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class ProvidersCompatibilityTests
{
    [Fact]
    public async Task AnthropicCacheBoundaryLeavesTheEphemeralSuffixUncached()
    {
        using var handler = new SequenceHandler(WithBom(AnthropicStart() + AnthropicFinish("end_turn")));
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("model", "key") { CacheConfig = new() { Messages = true } });

        await foreach (var _ in model.StreamAsync(new ModelRequest([new(Role.User, [new TextBlock("stable"), new TextBlock("dynamic")])]) { DynamicTrailingBlocks = 1 })) { }

        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var content = document.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.True(content[0].TryGetProperty("cache_control", out _));
        Assert.False(content[1].TryGetProperty("cache_control", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeProvidersCompleteToolLoopAcrossSplitUtf8AndBom(bool anthropic)
    {
        var first = anthropic
            ? AnthropicStart()
                + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call","name":"lookup","input":{}}}""")
                + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"id\":3}"}}""")
                + AnthropicFrame("content_block_stop", """{"type":"content_block_stop","index":0}""") + AnthropicFinish("tool_use")
            : OpenAIFrame("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call","function":{"name":"lookup","arguments":"{\"id\":3}"}}]},"finish_reason":"tool_calls"}]}""") + OpenAIFrame("[DONE]");
        var second = anthropic
            ? AnthropicStart()
                + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"Café ☕"}}""")
                + AnthropicFrame("content_block_stop", """{"type":"content_block_stop","index":0}""") + AnthropicFinish("end_turn")
            : OpenAIFrame("""{"choices":[{"index":0,"delta":{"content":"Café ☕"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":3,"total_tokens":13}}""") + OpenAIFrame("[DONE]");
        using var handler = new SequenceHandler(WithBom(first), WithBom(second));
        using var client = new HttpClient(handler);
        var model = CreateModel(client, anthropic);
        var tool = new FunctionTool(new ToolSpec("lookup", "Look up an item", JsonSerializer.SerializeToElement(new { type = "object" })),
            (input, _, _) => new ValueTask<string>("item:" + input.GetProperty("id").GetInt32()));
        var agent = new Agent(new AgentOptions { Model = model, Tools = new[] { tool } });

        var result = await WithDeadline(agent.InvokeAsync("Find item 3"));

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("Café ☕", result.Message!.Text);
        Assert.Equal(2, result.Turns);
        Assert.Equal(4, agent.Messages.Count);
        Assert.Equal(2, handler.Bodies.Count);
        using var document = JsonDocument.Parse(handler.Bodies[1]);
        var nativeReply = document.RootElement.GetProperty("messages")[2];
        Assert.Equal("item:3", anthropic ? nativeReply.GetProperty("content")[0].GetProperty("content").GetString() : nativeReply.GetProperty("content").GetString());
        Assert.All(handler.Streams, stream => Assert.True(stream.Disposed));
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInterruptsBodyThatIgnoresReadToken(bool anthropic)
    {
        using var body = new BlockingBody();
        using var handler = new BodyHandler(body);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = CollectAsync(CreateModel(client, anthropic), cancellation.Token);
        await WithDeadline(body.ReadStarted.Task);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithDeadline(operation));
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyConsumerExitDisposesResponseBody(bool anthropic)
    {
        var prefix = anthropic ? AnthropicStart()
            : OpenAIFrame("""{"choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":null}]}""");
        using var body = new BlockingBody(Encoding.UTF8.GetBytes(prefix));
        using var handler = new BodyHandler(body);
        using var client = new HttpClient(handler);
        var iterator = CreateModel(client, anthropic).StreamAsync(new ModelRequest(new[] { Message.User("Hi") })).GetAsyncEnumerator();
        try
        {
            Assert.True(await WithDeadline(iterator.MoveNextAsync().AsTask()));
            Assert.IsType<ModelMessageStartEvent>(iterator.Current);
            Assert.False(body.Disposed);
        }
        finally { await iterator.DisposeAsync(); }
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MalformedAndTruncatedUtf8ProduceModelErrors(bool anthropic, bool truncated)
    {
        var invalidBytes = truncated ? new byte[] { 0xE2, 0x82 } : new byte[] { 0xC3, 0x28 };
        using var handler = new SequenceHandler(invalidBytes);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelException>(() => WithDeadline(CollectAsync(CreateModel(client, anthropic))));
        Assert.IsType<DecoderFallbackException>(error.InnerException);
        Assert.True(Assert.Single(handler.Streams).Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpThrottlingRemainsTypedOnEveryTarget(bool anthropic)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("""{"error":{"code":"rate_limit_exceeded","message":"Busy"}}"""));
        using var handler = new BodyHandler(body, (HttpStatusCode)429);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelThrottledException>(() => WithDeadline(CollectAsync(CreateModel(client, anthropic))));
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    private static IModel CreateModel(HttpClient client, bool anthropic) => anthropic
        ? new AnthropicModel(client, new AnthropicModelOptions("compat-model", "compat-key"))
        : new OpenAIModel(client, new OpenAIModelOptions("compat-model", "compat-key"));

    private static async Task<List<ModelStreamEvent>> CollectAsync(IModel model, CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(new ModelRequest(new[] { Message.User("Hi") }), cancellationToken)) events.Add(item);
        return events;
    }

    private static async Task<T> WithDeadline<T>(Task<T> operation)
    {
        var winner = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(operation, winner);
        return await operation;
    }

    private static byte[] WithBom(string value) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(value)).ToArray();
    private static string OpenAIFrame(string json) => "data: " + json + "\r\n\r\n";
    private static string AnthropicFrame(string type, string json) => "event: " + type + "\r\ndata: " + json + "\r\n\r\n";
    private static string AnthropicStart() => AnthropicFrame("message_start", """{"type":"message_start","message":{"role":"assistant","content":[],"usage":{"input_tokens":10,"output_tokens":0}}}""");
    private static string AnthropicFinish(string reason) => AnthropicFrame("message_delta", """{"type":"message_delta","delta":{"stop_reason":"REASON"},"usage":{"output_tokens":3}}""".Replace("REASON", reason))
        + AnthropicFrame("message_stop", """{"type":"message_stop"}""");

    private sealed class SequenceHandler(params byte[][] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public List<SplitByteStream> Streams { get; } = new();
        public bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(await request.Content!.ReadAsStringAsync());
            var stream = new SplitByteStream(responses[Bodies.Count - 1]);
            Streams.Add(stream);
            return Response(stream, HttpStatusCode.OK);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BodyHandler(Stream body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Response(body, status));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static HttpResponseMessage Response(Stream body, HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private sealed class SplitByteStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _content = new(bytes);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, Math.Min(count, 1));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }
        protected override void Dispose(bool disposing) { Disposed = true; _content.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingBody(byte[]? prefix = null) : Stream
    {
        private readonly MemoryStream _prefix = new(prefix ?? Array.Empty<byte>());
        private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = _prefix.Read(buffer, offset, count);
            if (read > 0) return Task.FromResult(read);
            ReadStarted.TrySetResult(true);
            return _pending.Task;
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _pending.TrySetException(new ObjectDisposedException(nameof(BlockingBody)));
            _prefix.Dispose();
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
