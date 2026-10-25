using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class GoogleCompatibilityTests
{
    [Theory]
    [InlineData(ToolChoiceMode.Auto, "AUTO")]
    [InlineData(ToolChoiceMode.Any, "ANY")]
    [InlineData(ToolChoiceMode.None, "NONE")]
    [InlineData(ToolChoiceMode.Tool, "ANY")]
    public async Task FrameworkAssetStreamsFragmentedUtf8AndMapsAllToolChoices(ToolChoiceMode mode, string expected)
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("gemini-2.5-flash", "key") { GenerationConfig = JsonSerializer.SerializeToElement(new { maxOutputTokens = 90 }) });
        var specification = new ToolSpec("lookup", "Find", JsonSerializer.SerializeToElement(new { type = "object" }));
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(new([Message.User("go")], "system", [specification]) { ToolChoice = new(mode, mode == ToolChoiceMode.Tool ? "lookup" : null) })) events.Add(item);
        Assert.Equal("hé😀", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
        Assert.Equal(new Usage(2, 3, 1, ReportedTotalTokens: 5), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.Contains(events.OfType<ModelContentBlockStopEvent>(), item => item.ContentBlock is GoogleContentBlock block && block.Part.TryGetProperty("thoughtSignature", out var signature) && signature.GetString() == "trailing");
        var body = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Bodies));
        Assert.Equal(90, body.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(expected, body.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());
        if (mode == ToolChoiceMode.Tool) Assert.Equal("lookup", body.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("allowedFunctionNames")[0].GetString());
        Assert.True(Assert.Single(handler.Streams).Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task FrameworkSessionRestoresSignedFunctionPartsCitationsAndTrailingSignature()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-google-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new Handler(FunctionResponse(), TextResponse(), TextResponse());
            using var client = new HttpClient(handler);
            var model = new GoogleModel(client, new("model", "key"));
            var store = new FileSessionStore(directory);
            var tool = new FunctionTool(new("lookup", "Find", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => new ValueTask<string>("found:" + input.GetProperty("value").GetInt32()));
            await using (var first = new Agent(new() { Model = model, SessionStore = store, SessionId = "google", Tools = [tool] }))
                Assert.Equal("hé😀", (await first.InvokeAsync("go")).ToString());
            var snapshot = Assert.IsType<SessionSnapshot>(await store.LoadAsync("google"));
            var call = Assert.IsType<ToolUseBlock>(snapshot.Messages[1].Content[0]);
            Assert.Equal("tool-signature", call.ProviderMetadata!.Value.GetProperty("google").GetProperty("thoughtSignature").GetString());
            Assert.Equal("trailing", Assert.IsType<GoogleContentBlock>(snapshot.Messages[3].Content[2]).Part.GetProperty("thoughtSignature").GetString());
            Assert.Equal("https://example.test", snapshot.Messages[3].Metadata!.Value.GetProperty("google").GetProperty("candidate").GetProperty("citationMetadata").GetProperty("citationSources")[0].GetProperty("uri").GetString());
            await using (var restored = new Agent(new() { Model = model, SessionStore = store, SessionId = "google", Tools = [tool] }))
                await restored.InvokeAsync("again");
            var replay = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[2]).GetProperty("contents");
            Assert.Equal("tool-signature", replay[1].GetProperty("parts")[0].GetProperty("thoughtSignature").GetString());
            Assert.Equal("lookup", replay[2].GetProperty("parts")[0].GetProperty("functionResponse").GetProperty("name").GetString());
            Assert.Equal("trailing", replay[3].GetProperty("parts")[2].GetProperty("thoughtSignature").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FrameworkNativeCountIsOptInAndUsesOnlyContentsWithEstimatedOverhead()
    {
        using var handler = new Handler("""{"totalTokens":17}""");
        using var client = new HttpClient(handler);
        var request = new ModelRequest([Message.User("12345678")], "12345678");
        Assert.Equal(4, await new GoogleModel(client, new("model", "key")).CountTokensAsync(request));
        Assert.Empty(handler.Bodies);
        Assert.Equal(19, await new GoogleModel(client, new("model", "key") { UseNativeTokenCount = true }).CountTokensAsync(request));
        var body = JsonSerializer.Deserialize<JsonElement>(Assert.Single(handler.Bodies));
        Assert.True(body.TryGetProperty("contents", out _));
        Assert.False(body.TryGetProperty("systemInstruction", out _));
        Assert.EndsWith(":countTokens", Assert.Single(handler.Uris).AbsoluteUri, StringComparison.Ordinal);
        Assert.True(Assert.Single(handler.Streams).Disposed);
    }

    [Theory]
    [InlineData("{\"totalTokens\":-1}")]
    [InlineData("not-json")]
    public async Task FrameworkInvalidNativeCountsFallBackToTheLocalEstimate(string response)
    {
        using var handler = new Handler(response);
        using var client = new HttpClient(handler);
        Assert.Equal(2, await new GoogleModel(client, new("model", "key") { UseNativeTokenCount = true }).CountTokensAsync(new([Message.User("12345678")])));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task FrameworkCancellationInterruptsReadAndRetainsCallerToken()
    {
        using var body = new HangingStream(Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"partial"}]}}]}"""));
        using var handler = new Handler(body);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        await using var iterator = new GoogleModel(client, new("model", "key")).StreamAsync(new([Message.User("go")]), cancellation.Token).GetAsyncEnumerator();
        for (var index = 0; index < 4; index++) Assert.True(await iterator.MoveNextAsync());
        var pending = CaptureCancellation(iterator);
        Assert.Same(body.Waiting.Task, await Task.WhenAny(body.Waiting.Task, Task.Delay(TimeSpan.FromSeconds(5))));
        cancellation.Cancel();
        Assert.Same(pending, await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))));
        var error = Assert.IsAssignableFrom<OperationCanceledException>(await pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task FrameworkEarlyDisposalClosesOwnedBodyAndLeavesClientBorrowed()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var iterator = new GoogleModel(client, new("model", "key")).StreamAsync(new([Message.User("go")])).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        await iterator.DisposeAsync();
        Assert.True(Assert.Single(handler.Streams).Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task FrameworkMalformedEmptyHttpResponseFailsAsProtocolErrorAndIsDisposed()
    {
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var _ in new GoogleModel(client, new("model", "key")).StreamAsync(new([Message.User("go")]))) { }
        });
        Assert.True(handler.Response.Disposed);
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        internal EmptyResponse Response { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult<HttpResponseMessage>(Response);
    }
    private sealed class EmptyResponse() : HttpResponseMessage(HttpStatusCode.OK)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static async Task<OperationCanceledException?> CaptureCancellation(IAsyncEnumerator<ModelStreamEvent> iterator)
    {
        try { await iterator.MoveNextAsync(); return null; }
        catch (OperationCanceledException error) { return error; }
    }
    private static string Frame(string json) => ": keepalive\r\ndata: " + json + "\r\n\r\n";
    private static string TextResponse() => "\uFEFF" + Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"consider","thought":true,"thoughtSignature":"reason"}]}}]}""") +
        Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"hé😀"},{"text":"","thoughtSignature":"trailing"}]},"finishReason":"STOP","citationMetadata":{"citationSources":[{"uri":"https://example.test"}]}}],"usageMetadata":{"promptTokenCount":2,"candidatesTokenCount":2,"thoughtsTokenCount":1,"totalTokenCount":5,"cachedContentTokenCount":1}}""");
    private static string FunctionResponse() => Frame("""{"candidates":[{"index":0,"content":{"role":"model","parts":[{"functionCall":{"id":"call","name":"lookup","args":{"value":42}},"thoughtSignature":"tool-signature"}]},"finishReason":"STOP"}]}""");

    private sealed class Handler : HttpMessageHandler
    {
        private readonly string[]? _responses;
        private readonly FragmentedStream? _body;
        internal Handler(params string[] responses) { _responses = responses; }
        internal Handler(FragmentedStream body) { _body = body; }
        internal List<string> Bodies { get; } = [];
        internal List<Uri> Uris { get; } = [];
        internal List<FragmentedStream> Streams { get; } = [];
        internal bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync());
            Uris.Add(request.RequestUri!);
            var stream = _body ?? new FragmentedStream(_responses![Math.Min(Bodies.Count - 1, _responses.Length - 1)]); Streams.Add(stream);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new(request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal) ? "application/json" : "text/event-stream");
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private class FragmentedStream(string content) : Stream
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

    private sealed class HangingStream(string content) : FragmentedStream(content)
    {
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = Read(buffer, offset, count);
            if (read != 0) return read;
            Waiting.TrySetResult(true); await _closed.Task;
            throw new ObjectDisposedException(nameof(HangingStream));
        }
        protected override void Dispose(bool disposing) { _closed.TrySetResult(0); base.Dispose(disposing); }
    }
}
