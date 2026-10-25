using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class OllamaStructuredOutputTests
{
    [Fact]
    public async Task UsesOneNativeSchemaRequestWithCurrentOptionsAndNoOrdinaryToolSelection()
    {
        var handler = new Handler("""{"message":{"role":"assistant","content":"  {\"value\":42}  "},"done":true}""");
        using var client = new HttpClient(handler) { BaseAddress = new("http://ollama.test/") };
        var model = new OllamaModel(client, new("before") { Format = JsonSerializer.SerializeToElement("json") });
        model.UpdateConfig(options => options with { ModelId = "after", Temperature = 0.2 });
        var request = new ModelRequest([Message.User("extract")], "instructions", [new("unused", "unused", JsonSerializer.SerializeToElement(new { type = "object" }))]) { ToolChoice = new(ToolChoiceMode.Tool, "unused") };

        var result = await model.InvokeStructuredAsync(request, Options());

        Assert.Equal(42, result.Value);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(new Uri("http://ollama.test/api/chat"), handler.Endpoint);
        Assert.Equal("after", handler.Body.GetProperty("model").GetString());
        Assert.False(handler.Body.GetProperty("stream").GetBoolean());
        Assert.Equal("object", handler.Body.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(0.2, handler.Body.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.Equal(0, handler.Body.GetProperty("tools").GetArrayLength());
        Assert.Equal("instructions", handler.Body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.True(handler.Content.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData("{\"value\":\"wrong\"}")]
    [InlineData("{\"other\":1}")]
    [InlineData("not JSON")]
    [InlineData("")]
    public async Task InvalidResponsesFailWithoutRetry(string text)
    {
        var handler = new Handler(JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done = true }));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new OllamaModel(client, new("model")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.Equal(1, handler.Calls);
        Assert.True(handler.Content.Disposed);
    }

    [Fact]
    public async Task UnexpectedToolCallsCannotBecomeAValidatedOutput()
    {
        var handler = new Handler("""{"message":{"content":"{\"value\":42}","tool_calls":[{"function":{"name":"side_effect","arguments":{}}}]},"done":true}""");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new OllamaModel(client, new("model")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CustomDeserializationAndPreCancelledCallsUseTheSharedContracts()
    {
        var handler = new Handler("""{"message":{"content":"{\"value\":42}"},"done":true}""");
        using var client = new HttpClient(handler);
        var model = new OllamaModel(client, new("model"));
        var failure = new InvalidOperationException("custom decoder");
        var error = await Assert.ThrowsAsync<StructuredOutputException>(() => model.InvokeStructuredAsync(new([Message.User("extract")]), Options() with { Deserializer = _ => throw failure }));
        Assert.Same(failure, error.InnerException);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.InvokeStructuredAsync(new([Message.User("extract")]), Options(), cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"message\":{\"content\":\"{}\"},\"done\":false}")]
    [InlineData("{\"message\":{\"role\":\"user\",\"content\":\"{}\"},\"done\":true}")]
    [InlineData("{\"message\":{\"content\":42},\"done\":true}")]
    public async Task MalformedNativeEnvelopesAreProtocolFailures(string text)
    {
        var handler = new Handler(text);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => new OllamaModel(client, new("model")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.True(handler.Content.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompleteResponseBoundsCanBeUpdatedWithoutPublishingInvalidLimits()
    {
        var handler = new Handler("""{"message":{"content":"{\"value\":42}"},"done":true}""");
        using var client = new HttpClient(handler);
        var model = new OllamaModel(client, new("model"));
        model.UpdateConfig(options => options with { MaxResponseBytes = 8 });
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(options => options with { MaxResponseBytes = 0 }));
        Assert.Equal(8, model.GetConfig().MaxResponseBytes);
        var error = await Assert.ThrowsAsync<ModelException>(() => model.InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.Contains("MaxResponseBytes", error.Message);
        Assert.True(handler.Content.Disposed);
    }

    [Fact]
    public async Task NativeOverflowErrorsRetainTheCommonTypedException()
    {
        var handler = new Handler("""{"error":"the prompt is longer than the context length"}""");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ContextWindowOverflowException>(() => new OllamaModel(client, new("model")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.True(handler.Content.Disposed);
    }

    [Theory]
    [InlineData("\"length\"")]
    [InlineData("42")]
    public async Task ValidJsonCannotHideATruncatedOrMalformedFinishReason(string reason)
    {
        var handler = new Handler("{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"value\\\":42}\"},\"done\":true,\"done_reason\":" + reason + "}");
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<StructuredOutputException>(() => new OllamaModel(client, new("model")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.True(handler.Content.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task JsonValidationRemainsPrimaryWhenResponseCleanupAlsoFails()
    {
        var cleanup = new IOException("response cleanup");
        var handler = new Handler("""{"message":{"content":"broken JSON"},"done":true}""", cleanup);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<StructuredOutputException>(() => new OllamaModel(client, new("model")).InvokeStructuredAsync(new([Message.User("extract")]), Options()));
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.True(handler.Content.Disposed);
    }

    private static StructuredOutputOptions<Answer> Options() => new()
    {
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
    };
    private sealed record Answer(int Value);
    private sealed class Handler(string text, Exception? cleanup = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public JsonElement Body { get; private set; }
        public Uri? Endpoint { get; private set; }
        public TrackedContent Content { get; } = new(text, cleanup);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Endpoint = request.RequestUri;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Body = body.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = Content };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TrackedContent(string text, Exception? cleanup) : StringContent(text, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { var first = !Disposed; Disposed = true; base.Dispose(disposing); if (first && cleanup is not null) throw cleanup; }
    }
}
