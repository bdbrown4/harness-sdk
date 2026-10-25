using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using Xunit.Abstractions;

namespace Strands.Agents.Tests.Models;

public sealed class OrdinaryExplicitEmptyToolResultConformanceTests(ITestOutputHelper output)
{
    private const string Sentinel = "DISPLAY_MUST_NOT_BECOME_WIRE";

    [Theory]
    [InlineData("responses")]
    [InlineData("ollama")]
    [InlineData("writer-scalar")]
    [InlineData("writer-vision")]
    [InlineData("llama-api")]
    [InlineData("llama-cpp")]
    public async Task ExplicitNonemptyUnicodeContentKeepsAuthorityOverDisplayText(string provider)
    {
        var result = Empty() with { ContentBlocks = [new TextBlock("wire-value 雪")] };
        var body = await CaptureAsync(provider, result);
        AssertTextResult(body, provider, "wire-value 雪");
        Assert.DoesNotContain(Sentinel, body.GetRawText());
        Assert.Equal(Sentinel, result.Content);
        Assert.Equal("wire-value 雪", Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
    }

    [Theory]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("ollama", false)]
    [InlineData("ollama", true)]
    [InlineData("writer-scalar", false)]
    [InlineData("writer-scalar", true)]
    [InlineData("writer-vision", false)]
    [InlineData("writer-vision", true)]
    [InlineData("llama-api", false)]
    [InlineData("llama-api", true)]
    [InlineData("llama-cpp", false)]
    [InlineData("llama-cpp", true)]
    public async Task SeparateStructuredObjectAndJsonNullKeepEachExistingProviderPolicy(string provider, bool jsonNull)
    {
        var raw = jsonNull ? "null" : "{\"value\":3}";
        using var document = JsonDocument.Parse(raw);
        var result = Empty() with { StructuredContent = document.RootElement.Clone() };
        Assert.True(result.StructuredContent.HasValue);
        var body = await CaptureAsync(provider, result);
        if (provider is "responses" or "llama-api") AssertTextResult(body, provider, raw);
        else AssertExplicitEmptyResult(body, provider);
        Assert.DoesNotContain(Sentinel, body.GetRawText());
        Assert.Equal(jsonNull ? JsonValueKind.Null : JsonValueKind.Object, result.StructuredContent.GetValueOrDefault().ValueKind);
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("writer-scalar")]
    [InlineData("writer-vision")]
    [InlineData("llama-api")]
    [InlineData("llama-cpp")]
    public async Task ExplicitEmptyErrorRetainsTheExistingShapeWithoutAnInventedPrefix(string provider)
    {
        var body = await CaptureAsync(provider, Empty() with { Status = ToolResultStatus.Error });
        AssertExplicitEmptyResult(body, provider);
        Assert.DoesNotContain(Sentinel, body.GetRawText());
        Assert.DoesNotContain("[ERROR]", body.GetRawText());
    }

    [Theory]
    [InlineData("writer-scalar")]
    [InlineData("writer-vision")]
    public async Task WriterStillOmitsOrdinaryEmptyMessagesBesideACorrelatedEmptyResult(string provider)
    {
        var result = Empty();
        var body = await CaptureAsync(provider, result, [
            Message.User("question"), new(Role.User, []), new(Role.Assistant, []),
            new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [result])]);
        var messages = body.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal(new[] { "user", "assistant", "tool" }, messages.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        AssertExplicitEmptyResult(body, provider);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("ollama")]
    [InlineData("writer-scalar")]
    [InlineData("writer-vision")]
    [InlineData("llama-api")]
    [InlineData("llama-cpp")]
    public async Task LegacyEmptyDisplayRetainsItsPriorProviderSpecificWirePolicy(string provider)
    {
        var result = new ToolResultBlock("call", "");
        Assert.False(result.HasExplicitContent);
        var body = await CaptureAsync(provider, result);
        if (provider == "writer-scalar") Assert.DoesNotContain(body.GetProperty("messages").EnumerateArray(), IsTool);
        else AssertTextResult(body, provider, "");
    }

    private static ToolResultBlock Empty() => new("call", Sentinel)
    {
        HasExplicitContent = true,
        Metadata = JsonSerializer.SerializeToElement(new { localOnlyMetadata = "must-not-leak" })
    };

    private static void AssertExplicitEmptyResult(JsonElement body, string provider)
    {
        if (provider == "ollama")
        {
            Assert.DoesNotContain(body.GetProperty("messages").EnumerateArray(), IsTool);
            return;
        }
        var item = Assert.Single(body.GetProperty("messages").EnumerateArray(), IsTool);
        Assert.Equal("call", item.GetProperty("tool_call_id").GetString());
        if (provider == "writer-scalar") Assert.Equal("", item.GetProperty("content").GetString());
        else Assert.Empty(item.GetProperty("content").EnumerateArray());
    }

    private static void AssertTextResult(JsonElement body, string provider, string expected)
    {
        if (provider == "responses")
        {
            var item = Assert.Single(body.GetProperty("input").EnumerateArray(), IsFunctionOutput);
            Assert.Equal("call", item.GetProperty("call_id").GetString());
            Assert.Equal(expected, item.GetProperty("output").GetString());
            return;
        }
        var tool = Assert.Single(body.GetProperty("messages").EnumerateArray(), IsTool);
        if (provider != "ollama") Assert.Equal("call", tool.GetProperty("tool_call_id").GetString());
        var content = tool.GetProperty("content");
        if (provider is "ollama" or "writer-scalar") Assert.Equal(expected, content.GetString());
        else Assert.Equal(expected, Assert.Single(content.EnumerateArray()).GetProperty("text").GetString());
    }

    private async Task<JsonElement> CaptureAsync(string provider, ToolResultBlock result, IReadOnlyList<Message>? history = null)
    {
        using var handler = new Capture(Wire(provider));
        using var client = new HttpClient(handler) { BaseAddress = new("http://127.0.0.1:1/") };
        IModel model = provider switch
        {
            "responses" => new OpenAIResponsesModel(client, new("fixture-model", "fixture-key")),
            "ollama" => new OllamaModel(client, new("fixture-model")),
            "writer-scalar" => new WriterModel(client, new("palmyra-x4", "fixture-key")),
            "writer-vision" => new WriterModel(client, new("palmyra-x5", "fixture-key")),
            "llama-api" => new LlamaApiModel(client, new("fixture-model", "fixture-key")),
            "llama-cpp" => new LlamaCppModel(client),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var request = new ModelRequest(history ?? [
            Message.User("question"),
            new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [result])]);
        var ended = false;
        await foreach (var item in model.StreamAsync(request)) if (item is ModelMessageStopEvent { StopReason: StopReasons.EndTurn }) ended = true;
        Assert.True(ended);
        var body = Assert.Single(handler.Bodies);
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "ordinary-explicit-empty-conformance-wire", Provider = provider, result.HasExplicitContent, result.Status, Requests = handler.Bodies.Count, Body = body }));
        Assert.DoesNotContain("localOnlyMetadata", body.GetRawText());
        return body;
    }

    private static bool IsTool(JsonElement item) => item.TryGetProperty("role", out var role) && role.GetString() == "tool";
    private static bool IsFunctionOutput(JsonElement item) => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output";
    private static string Frame(string json) => "data: " + json + "\n\n";
    private static string ResponseFrame(string type, string json) => "event: " + type + "\n" + Frame(json);

    private static string Wire(string provider) => provider switch
    {
        "responses" =>
            ResponseFrame("response.created", """{"type":"response.created","response":{"id":"r1","status":"in_progress"}}""") +
            ResponseFrame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"in_progress","content":[]}}""") +
            ResponseFrame("response.content_part.added", """{"type":"response.content_part.added","output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""") +
            ResponseFrame("response.output_text.delta", """{"type":"response.output_text.delta","output_index":0,"item_id":"m1","content_index":0,"delta":"hello"}""") +
            ResponseFrame("response.content_part.done", """{"type":"response.content_part.done","output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"hello","annotations":[]}}""") +
            ResponseFrame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello","annotations":[]}]}}""") +
            ResponseFrame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed","usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}"""),
        "ollama" => """{"message":{"role":"assistant","content":"hello"},"done":true,"done_reason":"stop","prompt_eval_count":4,"eval_count":2}""" + "\n",
        "llama-api" => Frame("""{"event":{"event_type":"start","delta":{"type":"text","text":""}}}""") +
            Frame("""{"event":{"event_type":"progress","delta":{"type":"text","text":"hello"}}}""") +
            Frame("""{"event":{"event_type":"complete","delta":{"type":"text","text":""},"stop_reason":"stop"}}"""),
        _ => Frame("""{"choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":"stop"}]}""") + Frame("[DONE]")
    };

    private sealed class Capture(string response) : HttpMessageHandler
    {
        internal List<JsonElement> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Bodies.Add(document.RootElement.Clone());
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(response, Encoding.UTF8, response.StartsWith("{", StringComparison.Ordinal) ? "application/x-ndjson" : "text/event-stream") };
        }
    }
}
