using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class ResponsesCompatibilityTests
{
    [Theory]
    [InlineData(ToolChoiceMode.Auto, "auto")]
    [InlineData(ToolChoiceMode.Any, "required")]
    [InlineData(ToolChoiceMode.None, "none")]
    [InlineData(ToolChoiceMode.Tool, "lookup")]
    public async Task FrameworkAssetUsesNativeResponsesWireAndFragmentedUtf8(ToolChoiceMode mode, string expected)
    {
        using var handler = new Handler(TextResponse("hé😀"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key") { MaxTokens = 50 });
        var request = new ModelRequest([Message.User("hello")], "system", [new("lookup", "Find", JsonSerializer.SerializeToElement(new { type = "object" }))]) { ToolChoice = new(mode, mode == ToolChoiceMode.Tool ? "lookup" : null) };
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(request)) events.Add(item);
        Assert.Equal("hé😀", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
        Assert.Equal(new Usage(2, 3, ReportedTotalTokens: 5), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        var body = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Bodies));
        Assert.Equal("https://api.openai.com/v1/responses", handler.Uri!.AbsoluteUri);
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal(50, body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(expected, mode == ToolChoiceMode.Tool ? body.GetProperty("tool_choice").GetProperty("name").GetString() : body.GetProperty("tool_choice").GetString());
        Assert.True(Assert.Single(handler.Streams).Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task FrameworkSessionRestoresEncryptedReasoningRefusalAndNativeMessageMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-responses-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new Handler(ReasoningAndRefusalResponse(), TextResponse("restored"));
            using var client = new HttpClient(handler);
            var model = new OpenAIResponsesModel(client, new("model", "key"));
            var store = new FileSessionStore(directory);
            await using (var agent = new Agent(new() { Model = model, SessionStore = store, SessionId = "responses" }))
                Assert.Equal("cannot assist", (await agent.InvokeAsync("go")).ToString());
            var snapshot = Assert.IsType<SessionSnapshot>(await store.LoadAsync("responses"));
            var reasoning = Assert.IsType<ResponsesReasoningBlock>(snapshot.Messages[1].Content[0]);
            Assert.Equal("complete-ciphertext", reasoning.Item.GetProperty("encrypted_content").GetString());
            var refusal = Assert.IsType<ResponsesContentBlock>(snapshot.Messages[1].Content[1]);
            Assert.Equal("refusal", refusal.Part.GetProperty("type").GetString());
            Assert.Equal("final_answer", refusal.Item!.Value.GetProperty("phase").GetString());
            await using (var restored = new Agent(new() { Model = model, SessionStore = store, SessionId = "responses" }))
                Assert.Equal("restored", (await restored.InvokeAsync("next")).ToString());
            var replay = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input");
            Assert.Equal("complete-ciphertext", replay[1].GetProperty("encrypted_content").GetString());
            Assert.Equal("final_answer", replay[2].GetProperty("phase").GetString());
            Assert.Equal("cannot assist", replay[2].GetProperty("content")[0].GetProperty("refusal").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FrameworkAgentExecutesCorrelatedFunctionAndReplaysItsNativeOutput()
    {
        var stream = Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""") +
            Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"f1","type":"function_call","call_id":"call","name":"lookup","arguments":""}}""") +
            Frame("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":0,"item_id":"f1","delta":"{\"value\":42}"}""") +
            Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"f1","type":"function_call","call_id":"call","name":"lookup","arguments":"{\"value\":42}"}}""") +
            Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed"}}""");
        using var handler = new Handler(stream, TextResponse("done"));
        using var client = new HttpClient(handler);
        var tool = new FunctionTool(new("lookup", "Find", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => new ValueTask<string>(input.GetProperty("value").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await using var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")), Tools = [tool] });
        Assert.Equal("done", (await agent.InvokeAsync("go")).ToString());
        var input = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input");
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
        Assert.Equal("call", input[2].GetProperty("call_id").GetString());
        Assert.Equal("42", input[2].GetProperty("output").GetString());
    }

    [Fact]
    public async Task FrameworkCancelledInvocationDoesNotSendOrDisposeBorrowedClient()
    {
        using var handler = new Handler(TextResponse("still usable"));
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await using var iterator = model.StreamAsync(new([Message.User("go")]), cancellation.Token).GetAsyncEnumerator();
        try { await iterator.MoveNextAsync(); Assert.Fail("The cancelled stream must throw."); }
        catch (OperationCanceledException error) { Assert.Equal(cancellation.Token, error.CancellationToken); }
        Assert.Empty(handler.Bodies);
        await using var agent = new Agent(new() { Model = model });
        Assert.Equal("still usable", (await agent.InvokeAsync("again")).ToString());
        Assert.False(handler.Disposed);
    }

    private static string Frame(string type, string json) => ": keepalive\r\nevent: " + type + "\r\ndata: " + json + "\r\n\r\n";
    private static string TextResponse(string text) => Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""") +
        Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"m1","type":"message","role":"assistant","status":"in_progress","content":[]}}""") +
        Frame("response.content_part.added", """{"type":"response.content_part.added","output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""") +
        Frame("response.output_text.delta", JsonSerializer.Serialize(new { type = "response.output_text.delta", output_index = 0, item_id = "m1", content_index = 0, delta = text })) +
        Frame("response.content_part.done", JsonSerializer.Serialize(new { type = "response.content_part.done", output_index = 0, item_id = "m1", content_index = 0, part = new { type = "output_text", text, annotations = Array.Empty<object>() } })) +
        Frame("response.output_item.done", JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 0, item = new { id = "m1", type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } } } })) +
        Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed","usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}}""");
    private static string ReasoningAndRefusalResponse() => Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""") +
        Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"reason","type":"reasoning","summary":[],"encrypted_content":"partial"}}""") +
        Frame("response.reasoning_summary_text.delta", """{"type":"response.reasoning_summary_text.delta","output_index":0,"item_id":"reason","summary_index":0,"delta":"consider"}""") +
        Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"reason","type":"reasoning","summary":[{"type":"summary_text","text":"consider"}],"encrypted_content":"complete-ciphertext"}}""") +
        Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":1,"item":{"id":"m1","type":"message","role":"assistant","status":"in_progress","content":[]}}""") +
        Frame("response.content_part.added", """{"type":"response.content_part.added","output_index":1,"item_id":"m1","content_index":0,"part":{"type":"refusal","refusal":""}}""") +
        Frame("response.refusal.delta", """{"type":"response.refusal.delta","output_index":1,"item_id":"m1","content_index":0,"delta":"cannot assist"}""") +
        Frame("response.content_part.done", """{"type":"response.content_part.done","output_index":1,"item_id":"m1","content_index":0,"part":{"type":"refusal","refusal":"cannot assist"}}""") +
        Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":1,"item":{"id":"m1","type":"message","role":"assistant","status":"completed","phase":"final_answer","content":[{"type":"refusal","refusal":"cannot assist"}]}}""") +
        Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed"}}""");

    private sealed class Handler(params string[] content) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal List<FragmentedStream> Streams { get; } = [];
        internal Uri? Uri { get; private set; }
        internal bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Bodies.Add(await request.Content!.ReadAsStringAsync());
            var stream = new FragmentedStream(content[Math.Min(Bodies.Count - 1, content.Length - 1)]); Streams.Add(stream);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class FragmentedStream(string content) : Stream
    {
        private readonly MemoryStream _source = new(Encoding.UTF8.GetBytes(content));
        internal bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, Math.Min(count, 1));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
        protected override void Dispose(bool disposing) { Disposed = true; _source.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
