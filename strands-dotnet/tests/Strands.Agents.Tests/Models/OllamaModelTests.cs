using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class OllamaModelTests
{
    [Fact]
    public async Task FormatsThePinnedFlattenedConversationAndExplicitOptions()
    {
        var handler = new Handler("{\"message\":{\"role\":\"assistant\",\"content\":\"reply\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":4,\"eval_count\":2}\n");
        using var client = new HttpClient(handler) { BaseAddress = new("http://ollama.test/") };
        var model = new OllamaModel(client, new("model") { MaxTokens = 30, Temperature = .2, TopP = .9, StopSequences = ["stop"], Options = JsonSerializer.SerializeToElement(new { num_ctx = 8192, num_predict = 1 }), AdditionalArgs = JsonSerializer.SerializeToElement(new { think = true, model = "ignored" }) });
        var tool = new ToolSpec("add", "Adds integers.", JsonSerializer.SerializeToElement(new { type = "object" }));
        var messages = new Message[] { new(Role.User, [new TextBlock("inspect"), new ImageBlock("image/png", "AQID")]), new(Role.Assistant, [new ToolUseBlock("call", "add", JsonSerializer.SerializeToElement(new { a = 1 }))]), new(Role.User, [new ToolResultBlock("call", "done")]) };

        var events = await CollectAsync(model.StreamAsync(new(messages, "system", [tool])));

        Assert.Equal(new Uri("http://ollama.test/api/chat"), handler.Endpoint);
        var request = handler.Request;
        Assert.Equal("model", request.GetProperty("model").GetString());
        Assert.True(request.GetProperty("stream").GetBoolean());
        Assert.True(request.GetProperty("think").GetBoolean());
        Assert.Equal(30, request.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(8192, model.Metadata.ContextWindowLimit);
        var wire = request.GetProperty("messages");
        Assert.Equal(5, wire.GetArrayLength());
        Assert.Equal("system", wire[0].GetProperty("role").GetString());
        Assert.Equal("AQID", wire[2].GetProperty("images")[0].GetString());
        Assert.Equal("add", wire[3].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", wire[4].GetProperty("role").GetString());
        Assert.Equal(new Usage(4, 2), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.Equal(StopReasons.EndTurn, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
    }

    [Fact]
    public async Task NativeToolCallsProduceACompleteAgentToolCycle()
    {
        var handler = new Handler("{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"add\",\"arguments\":{\"a\":2,\"b\":3}}}]},\"done\":true,\"done_reason\":\"stop\"}\n", "{\"message\":{\"role\":\"assistant\",\"content\":\"5\"},\"done\":true}\n");
        using var client = new HttpClient(handler);
        var tool = new FunctionTool(new("add", "Adds integers.", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => new((input.GetProperty("a").GetInt32() + input.GetProperty("b").GetInt32()).ToString()));
        await using var agent = new Agent(new() { Model = new OllamaModel(client, new("model")), Tools = [tool] });

        var result = await agent.InvokeAsync("add");

        Assert.Equal("5", result.ToString());
        Assert.Equal(2, result.Turns);
        Assert.Equal(2, handler.Calls);
        var call = Assert.Single(agent.Messages[1].Content.OfType<ToolUseBlock>());
        Assert.Equal(call.ToolUseId, Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).ToolUseId);
    }

    [Theory]
    [InlineData("{\"message\":{\"role\":\"assistant\",\"content\":\"partial\"},\"done\":false}\n")]
    [InlineData("{invalid}\n")]
    public async Task MissingTerminalAndMalformedNdjsonFail(string body)
    {
        using var client = new HttpClient(new Handler(body));
        var model = new OllamaModel(client, new("model"));
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model.StreamAsync(new([Message.User("question")]))));
    }

    [Theory]
    [InlineData("{\"message\":{\"role\":42},\"done\":true}")]
    [InlineData("{\"message\":{\"tool_calls\":[null]},\"done\":true}")]
    [InlineData("{\"message\":{\"content\":null},\"done\":true}")]
    [InlineData("{\"message\":{\"content\":\"\"},\"done\":true,\"eval_count\":-1}")]
    [InlineData("{\"message\":{\"content\":\"\"},\"done\":true,\"eval_count\":9223372036854775807,\"prompt_eval_count\":1}")]
    public async Task MalformedTypedFieldsAndOverflowAreModelProtocolErrors(string body)
    {
        using var client = new HttpClient(new Handler(body));
        var model = new OllamaModel(client, new("model"));
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model.StreamAsync(new([Message.User("question")]))));
    }

    [Fact]
    public async Task OneByteUtf8ReadsHandleBomCrLfAndAnUnterminatedFinalLine()
    {
        var body = new TrackedStream("\uFEFF\r\n{\"message\":{\"role\":\"assistant\",\"content\":\"café🙂\"},\"done\":false}\r\n{\"message\":{\"content\":\"!\"},\"done\":true}");
        var handler = new Handler() { ContentFactory = () => new StreamContent(body) };
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new OllamaModel(client, new("model")) });

        Assert.Equal("café🙂!", (await agent.InvokeAsync("question")).ToString());
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(400, "the prompt is longer than the context length", true)]
    [InlineData(500, "other provider failure", false)]
    public async Task HttpErrorsRetainOllamaStringDetailAndOverflowClassification(int status, string detail, bool overflow)
    {
        using var client = new HttpClient(new Handler(JsonSerializer.Serialize(new { error = detail })) { Status = (HttpStatusCode)status, MediaType = "application/json" });
        var error = await Assert.ThrowsAnyAsync<ModelException>(() => CollectAsync(new OllamaModel(client, new("model")).StreamAsync(new([Message.User("question")]))));
        Assert.Equal(overflow, error is ContextWindowOverflowException);
        Assert.Contains(detail, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedStreamRetainsItsPrimaryErrorWhenDisposalFails()
    {
        var body = new TrackedStream("{invalid}\n") { ThrowOnDispose = true };
        using var client = new HttpClient(new Handler() { ContentFactory = () => new StreamContent(body) });
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new OllamaModel(client, new("model")).StreamAsync(new([Message.User("question")]))));
        Assert.Contains(body.Failure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
    }

    [Fact]
    public async Task ParametersAndStopSequencesAreDetachedBeforeCallerMutation()
    {
        var handler = new Handler("{\"done\":true}");
        using var client = new HttpClient(handler);
        var document = JsonDocument.Parse("{\"temperature\":0.3,\"num_ctx\":4096}");
        string[] stops = ["original"];
        var model = new OllamaModel(client, new("model") { Options = document.RootElement, StopSequences = stops });
        document.Dispose();
        stops[0] = "changed";

        await CollectAsync(model.StreamAsync(new([Message.User("question")])));

        Assert.Equal("original", handler.Request.GetProperty("options").GetProperty("stop")[0].GetString());
        Assert.Equal(.3, handler.Request.GetProperty("options").GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task UnsupportedContentAndPreCancelledCallsPerformNoHttpRequests()
    {
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var model = new OllamaModel(client, new("model"));
        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(model.StreamAsync(new([new Message(Role.User, [new AudioBlock("audio/wav", "AQID")])]))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model.StreamAsync(new([Message.User("question")]), cancellation.Token)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationStopsWaitingForAnUncooperativeBodyOpenAndClosesItsLateStream()
    {
        var content = new WaitingContent();
        using var client = new HttpClient(new Handler() { ContentFactory = () => content });
        using var cancellation = new CancellationTokenSource();
        var execution = CollectAsync(new OllamaModel(client, new("model")).StreamAsync(new([Message.User("question")]), cancellation.Token));
        Assert.Same(content.Started.Task, await Task.WhenAny(content.Started.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        cancellation.Cancel();
        var completed = await Task.WhenAny(execution, Task.Delay(TimeSpan.FromMilliseconds(500)));
        // Always release the intentionally uncooperative fixture, even when the cancellation contract fails.
        content.Complete();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Same(execution, completed);
        Assert.Same(content.Body.Closed.Task, await Task.WhenAny(content.Body.Closed.Task, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    private sealed class WaitingContent : HttpContent
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Stream> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LateBody Body { get; } = new();
        internal void Complete() => _completion.TrySetResult(Body);
        protected override Task<Stream> CreateContentReadStreamAsync() { Started.TrySetResult(true); return _completion.Task; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class LateBody : MemoryStream
    {
        internal TaskCompletionSource<bool> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Closed.TrySetResult(true); }
    }

    private sealed class TrackedStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        internal bool Disposed;
        internal bool ThrowOnDispose;
        internal FormatException Failure { get; } = new("body cleanup failed");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);
#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer.Slice(0, Math.Min(1, buffer.Length)), cancellationToken);
#endif
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); if (ThrowOnDispose) throw Failure; }
    }

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private sealed class Handler(params string[] bodies) : HttpMessageHandler
    {
        internal JsonElement Request;
        internal Uri? Endpoint;
        internal int Calls;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string MediaType = "application/x-ndjson";
        internal Func<HttpContent>? ContentFactory;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Request = document.RootElement.Clone();
            var index = Calls++;
            var content = ContentFactory?.Invoke() ?? new StringContent(bodies[index], Encoding.UTF8, MediaType);
            content.Headers.ContentType = new(MediaType);
            return new(Status) { Content = content };
        }
    }
}
