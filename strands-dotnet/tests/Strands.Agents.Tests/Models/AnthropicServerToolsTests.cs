using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Memory;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class AnthropicServerToolsTests
{
    [Fact]
    public async Task SearchContentAndCitationsRemainVisibleAndReplayWithoutLocalToolExecution()
    {
        using var handler = new Replies(Search(), Text("continued"));
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new AnthropicModel(client, new("claude-test", "key")) });

        var first = await agent.InvokeAsync("search");
        Assert.Equal("A cited answer.", first.Message!.Text);
        Assert.Equal(StopReasons.EndTurn, first.StopReason);
        Assert.Empty(first.Message!.Content.OfType<ToolUseBlock>());
        Assert.Equal(3, first.Message!.Content.Count);
        var citations = Assert.IsType<CitationsBlock>(first.Message.Content[2]);
        Assert.Equal("Source", Assert.Single(citations.Citations).Title);
        Assert.Equal("example.test", Assert.IsType<WebCitationLocation>(citations.Citations[0].Location).Domain);
        var snapshot = JsonSerializer.Serialize(first.Message);
        Assert.Contains("web_search_result_location", snapshot);
        var restored = JsonSerializer.Deserialize<Message>(snapshot)!;
        Assert.Equal(first.Message!.Text, restored.Text);

        await agent.InvokeAsync("continue");

        var replay = handler.Bodies[1].GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("server_tool_use", replay[0].GetProperty("type").GetString());
        Assert.Equal("native-1", replay[0].GetProperty("id").GetString());
        Assert.Equal("Strands", replay[0].GetProperty("input").GetProperty("query").GetString());
        Assert.Equal("web_search_tool_result", replay[1].GetProperty("type").GetString());
        Assert.Equal("encrypted", replay[1].GetProperty("content")[0].GetProperty("encrypted_content").GetString());
        Assert.Equal("https://example.test/source", replay[2].GetProperty("citations")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task PausedServerTurnResumesInsideOneModelTurnAndAddsUsageOncePerResponse()
    {
        using var handler = new Replies(Paused(), Text("done", 20, 7));
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new AnthropicModel(client, new("claude-test", "key")) });

        var result = await agent.InvokeAsync("search");

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(1, result.Turns);
        Assert.Equal("Searching. done", result.Message!.Text);
        Assert.Equal(new Usage(30, 12, ReportedTotalTokens: 42), result.Usage);
        var continuation = handler.Bodies[1].GetProperty("messages");
        Assert.Equal(2, continuation.GetArrayLength());
        Assert.Equal("assistant", continuation[1].GetProperty("role").GetString());
        Assert.Equal("server_tool_use", continuation[1].GetProperty("content")[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task PausedServerTurnHasABoundedContinuationBudget()
    {
        using var handler = new Replies(Enumerable.Repeat(Paused(), 11).ToArray());
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new AnthropicModel(client, new("claude-test", "key")) });

        var error = await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("search"));

        Assert.Contains("10 continuations", error.Message);
        Assert.Equal(11, handler.Bodies.Count);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant);
    }

    [Fact]
    public async Task ProviderSearchFailureRemainsNativeContentAndDoesNotBecomeAClientToolCall()
    {
        var stream = Start(1)
            + Block(0, """{"type":"web_search_tool_result","tool_use_id":"native-1","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}}""")
            + Finish("end_turn", 2);
        using var handler = new Replies(stream);
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new AnthropicModel(client, new("claude-test", "key")) });

        var result = await agent.InvokeAsync("search");

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Empty(result.Message!.Content.OfType<ToolUseBlock>());
        Assert.Contains("max_uses_exceeded", JsonSerializer.Serialize(result.Message));
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    public async Task MalformedServerInputFailsBeforeContinuationOrHistoryCommit(string input)
    {
        var stream = Start(1)
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "server_tool_use", id = "native-1", name = "web_search", input = new { } } })
            + Delta(0, new { type = "input_json_delta", partial_json = input }) + Stop(0) + Finish("pause_turn", 1);
        using var handler = new Replies(stream);
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new AnthropicModel(client, new("claude-test", "key")) });

        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("search"));

        Assert.Single(handler.Bodies);
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant);
    }

    [Fact]
    public async Task IncompletePausedResponseDoesNotContinue()
    {
        using var handler = new Replies(Paused().Replace(Frame("message_stop", new { type = "message_stop" }), ""));
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new AnthropicModel(client, new("claude-test", "key")) });

        var error = await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("search"));

        Assert.Contains("message_stop", error.Message);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public void NativeContentIsDetachedAndClientToolRepresentationsCannotBeSmuggledIntoIt()
    {
        using var document = JsonDocument.Parse("""{"type":"text","text":"cited","citations":[{"type":"future_location","opaque":true}]}""");
        var block = new AnthropicContentBlock(document.RootElement);
        document.Dispose();

        Assert.Equal("cited", new Message(Role.Assistant, [block]).Text);
        Assert.True(block.Content.GetProperty("citations")[0].GetProperty("opaque").GetBoolean());
        Assert.Throws<ArgumentException>(() => new AnthropicContentBlock(JsonSerializer.SerializeToElement(new { type = "tool_use", id = "client", name = "unsafe", input = new { } })));
        Assert.Throws<ArgumentException>(() => new AnthropicContentBlock(JsonSerializer.SerializeToElement(new { type = "text", text = "x", citations = new object[] { 1 } })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CitedVisibleTextParticipatesInTokenEstimation(bool normalized)
    {
        using var handler = new Replies();
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-test", "key"));
        var block = CitedText(normalized);

        Assert.Equal(4, await model.CountTokensAsync(new ModelRequest([new Message(Role.Assistant, [block])])));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CitedVisibleTextIsIncludedInTheMemoryTranscript(bool normalized)
    {
        using var handler = new Replies(Text("[]"));
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-test", "key"));
        var extractor = new ModelExtractor(new() { Model = model });
        var block = CitedText(normalized);

        Assert.Empty(await extractor.ExtractAsync([new Message(Role.Assistant, [block])]));

        var transcript = handler.Bodies[0].GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("A cited answer.", transcript);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummarizationProjectsCitedTextIntoProviderNeutralInput(bool normalized)
    {
        using var handler = new Replies(Text("summary"));
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-test", "key"));
        var block = CitedText(normalized);

        Assert.Equal("summary", await ContextReductionMethods.SummarizeAsync([block], model));

        var input = handler.Bodies[0].GetProperty("messages")[0].GetProperty("content");
        Assert.Contains(input.EnumerateArray(), value => value.GetProperty("text").GetString() == "A cited answer.");
    }

    [Fact]
    public async Task ConfiguredServerToolsAreDetachedMergedAfterFunctionsAndCachedWithoutMutation()
    {
        using var document = JsonDocument.Parse("""{"type":"web_search_20250305","name":"web_search"}""");
        var configured = new List<JsonElement> { document.RootElement };
        using var handler = new Replies(Text("done"));
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-test", "key")
        {
            AnthropicTools = configured,
            Params = JsonSerializer.Deserialize<JsonElement>("""{"tools":[{"type":"web_fetch_20250910","name":"web_fetch"}]}"""),
            CacheConfig = new() { Tools = true }
        });
        configured.Clear(); document.Dispose();
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(new([Message.User("search")], Tools: [new("lookup", "lookup", JsonSerializer.SerializeToElement(new { type = "object" }))]))) events.Add(item);

        var tools = handler.Bodies[0].GetProperty("tools");
        Assert.Equal(new[] { "lookup", "web_search", "web_fetch" }, tools.EnumerateArray().Select(value => value.GetProperty("name").GetString()));
        Assert.False(tools[0].TryGetProperty("cache_control", out _));
        Assert.False(tools[1].TryGetProperty("cache_control", out _));
        Assert.Equal("ephemeral", tools[2].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public void ConfiguredServerToolsRejectFunctionDefinitionsAtConstruction()
    {
        using var client = new HttpClient();
        var function = JsonSerializer.SerializeToElement(new { name = "unsafe", input_schema = new { type = "object" } });

        Assert.Throws<ArgumentException>(() => new AnthropicModel(client, new("claude-test", "key") { AnthropicTools = [function] }));
    }

    [Theory]
    [InlineData("""{"type":"char_location","document_index":1,"start_char_index":2,"end_char_index":3,"file_id":"file","document_title":"Document","cited_text":"evidence"}""", typeof(DocumentCharCitationLocation))]
    [InlineData("""{"type":"page_location","document_index":1,"start_page_number":2,"end_page_number":3}""", typeof(DocumentPageCitationLocation))]
    [InlineData("""{"type":"content_block_location","document_index":1,"start_block_index":2,"end_block_index":3}""", typeof(DocumentChunkCitationLocation))]
    [InlineData("""{"type":"search_result_location","search_result_index":1,"start_block_index":2,"end_block_index":3,"source":"search"}""", typeof(SearchResultCitationLocation))]
    [InlineData("""{"type":"web_search_result_location","url":"https://example.test/source","title":"Source","cited_text":"evidence"}""", typeof(WebCitationLocation))]
    public async Task ProviderCitationLocationsStreamThroughTheNormalizedContract(string json, Type expected)
    {
        var stream = Start(1)
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "Answer" } })
            + Delta(0, new { type = "citations_delta", citation = JsonSerializer.Deserialize<JsonElement>(json) })
            + Stop(0) + Finish("end_turn", 1);
        using var handler = new Replies(stream);
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-test", "key"));
        var events = new List<ModelStreamEvent>();

        await foreach (var item in model.StreamAsync(new([Message.User("question")]))) events.Add(item);

        var citation = Assert.Single(Assert.Single(events.OfType<ModelContentBlockDeltaEvent>(), value => value.Citations.Count > 0).Citations);
        Assert.IsType(expected, citation.Location);
        Assert.IsType<CitationsBlock>(Assert.Single(events.OfType<ModelContentBlockStopEvent>()).ContentBlock);
    }

    [Fact]
    public async Task AcceptedBodyCleanupCannotReplaceThePrimaryParseFailure()
    {
        var body = new ThrowingBody("data: {broken\n\n");
        using var handler = new Replies { ContentFactory = () => new StreamContent(body) };
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new AnthropicModel(client, new("claude-test", "key")) });

        var error = await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("question"));

        Assert.Contains("JSON", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(body.Failure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", true)]
    [InlineData("text/plain", false)]
    public async Task ExplicitDocumentCitationOptionsAndContextReachNativeDocuments(string mediaType, bool enabled)
    {
        using var handler = new Replies(Text("answer"));
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-test", "key"));
        var source = new DocumentBlock(mediaType, Convert.ToBase64String(Encoding.UTF8.GetBytes("source")), "Report") { CitationsEnabled = enabled, Context = "Reference material" };
        var restored = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(new Message(Role.User, [source])))!;

        await foreach (var _ in model.StreamAsync(new([restored]))) { }

        var native = handler.Bodies[0].GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("document", native.GetProperty("type").GetString());
        Assert.Equal(enabled, native.GetProperty("citations").GetProperty("enabled").GetBoolean());
        Assert.Equal("Reference material", native.GetProperty("context").GetString());
        Assert.Equal("Report", native.GetProperty("title").GetString());
        Assert.Equal(mediaType == "text/plain" ? "source" : source.Data, native.GetProperty("source").GetProperty("data").GetString());
    }

    private static string Search() => Start(10)
        + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "server_tool_use", id = "native-1", name = "web_search", input = new { } } })
        + Delta(0, new { type = "input_json_delta", partial_json = "{\"query\":\"Strands\"}" })
        + Stop(0)
        + Block(1, """{"type":"web_search_tool_result","tool_use_id":"native-1","content":[{"type":"web_search_result","url":"https://example.test/source","title":"Source","encrypted_content":"encrypted"}]}""")
        + Frame("content_block_start", new { type = "content_block_start", index = 2, content_block = new { type = "text", text = "" } })
        + Delta(2, new { type = "text_delta", text = "A cited answer." })
        + Delta(2, new { type = "citations_delta", citation = new { type = "web_search_result_location", url = "https://example.test/source", title = "Source", cited_text = "Evidence", encrypted_index = "opaque" } })
        + Stop(2) + Finish("end_turn", 5);

    private static string Paused() => Start(10)
        + Block(0, """{"type":"text","text":"Searching. "}""")
        + Block(1, """{"type":"server_tool_use","id":"native-pause","name":"web_search","input":{}}""")
        + Frame("message_delta", new { type = "message_delta", delta = new { }, usage = new { output_tokens = 3 } })
        + Finish("pause_turn", 5);

    private static string Text(string text, int input = 1, int output = 1) => Start(input)
        + Block(0, JsonSerializer.Serialize(new { type = "text", text })) + Finish("end_turn", output);
    private static string Start(int input) => Frame("message_start", new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>(), usage = new { input_tokens = input } } });
    private static string Finish(string reason, int output) => Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = reason }, usage = new { output_tokens = output } }) + Frame("message_stop", new { type = "message_stop" });
    private static string Delta(int index, object delta) => Frame("content_block_delta", new { type = "content_block_delta", index, delta });
    private static string Stop(int index) => Frame("content_block_stop", new { type = "content_block_stop", index });
    private static string Block(int index, string content) => Frame("content_block_start", new { type = "content_block_start", index, content_block = JsonSerializer.Deserialize<JsonElement>(content) }) + Stop(index);
    private static string Frame(string name, object value) => $"event: {name}\ndata: {JsonSerializer.Serialize(value)}\n\n";
    private static ContentBlock CitedText(bool normalized) => normalized
        ? new CitationsBlock([new(new WebCitationLocation("https://example.test/source"))], [new("A cited answer.")])
        : new AnthropicContentBlock(JsonSerializer.SerializeToElement(new { type = "text", text = "A cited answer." }));

    private sealed class Replies(params string[] bodies) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public Func<HttpContent>? ContentFactory { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            var content = ContentFactory?.Invoke() ?? new StringContent(bodies[Bodies.Count - 1], Encoding.UTF8, "text/event-stream");
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private sealed class ThrowingBody(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public IOException Failure { get; } = new("accepted body disposal failed");
        protected override void Dispose(bool disposing) { base.Dispose(disposing); throw Failure; }
    }
}
