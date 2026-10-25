using System.Net;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class OpenAIResponsesTests
{
    [Fact]
    public async Task StreamsNativeResponsesTextAndUsage()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("responses-model", "key"));
        var events = await Collect(model, new([Message.User("hello")]));
        Assert.Equal(new ModelMessageStartEvent(), events[0]);
        Assert.Equal("hello", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(value => value.Text)));
        Assert.Equal(new Usage(3, 2, ReportedTotalTokens: 5), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.Equal(StopReasons.EndTurn, Assert.IsType<ModelMessageStopEvent>(events[^1]).StopReason);
        Assert.Equal("https://api.openai.com/v1/responses", handler.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(ToolChoiceMode.Auto, "auto")]
    [InlineData(ToolChoiceMode.Any, "required")]
    [InlineData(ToolChoiceMode.None, "none")]
    [InlineData(ToolChoiceMode.Tool, "function")]
    public async Task FormatsResponsesWireOptionsAndAllToolChoices(ToolChoiceMode mode, string expected)
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler) { BaseAddress = new("https://example.test/v1/") };
        using var owned = JsonDocument.Parse("""{"type":"json_schema","name":"answer","schema":{"type":"object"},"strict":true}""");
        var model = new OpenAIResponsesModel(client, new("model", "key")
        {
            MaxTokens = 80,
            Temperature = 0.5,
            TopP = 0.9,
            Store = false,
            TextFormat = owned.RootElement,
            Reasoning = JsonSerializer.SerializeToElement(new { effort = "low", summary = "auto" }),
            Params = JsonSerializer.SerializeToElement(new { model = "ignored", stream = false, store = true, metadata = new { request = "caller" } }),
            CacheConfig = new() { CacheKey = "route", Ttl = "24h" }
        });
        owned.Dispose();
        var tool = new ToolSpec("lookup", "Find data", JsonSerializer.SerializeToElement(new { type = "object" }));
        await Collect(model, new([Message.User("go")], "instructions", [tool]) { ToolChoice = new(mode, mode == ToolChoiceMode.Tool ? "lookup" : null) });
        var body = JsonSerializer.Deserialize<JsonElement>(handler.Body!);
        Assert.Equal("https://example.test/v1/responses", handler.Uri!.AbsoluteUri);
        Assert.Equal("model", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean()); Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal("instructions", body.GetProperty("instructions").GetString());
        Assert.Equal(80, body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("json_schema", body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("low", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("route", body.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("24h", body.GetProperty("prompt_cache_retention").GetString());
        Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("name").GetString());
        var choice = body.GetProperty("tool_choice");
        Assert.Equal(expected, mode == ToolChoiceMode.Tool ? choice.GetProperty("type").GetString() : choice.GetString());
        if (mode == ToolChoiceMode.Tool) Assert.Equal("lookup", choice.GetProperty("name").GetString());
    }

    [Fact]
    public async Task StreamsFunctionArgumentsAndPreservesModelToolModelRoundTrip()
    {
        using var handler = new Handler(FunctionResponse(), TextResponse());
        using var client = new HttpClient(handler);
        var tool = new FunctionTool(new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" })),
            (input, _, _) => ValueTask.FromResult("found:" + input.GetProperty("value").GetInt32()));
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")), Tools = [tool] });
        Assert.Equal("hello", (await agent.InvokeAsync("go")).ToString());
        var input = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input");
        Assert.Equal("call", input[1].GetProperty("call_id").GetString());
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
        Assert.Equal("{\"value\":42}", input[1].GetProperty("arguments").GetString());
        Assert.Equal("function_call_output", input[2].GetProperty("type").GetString());
        Assert.Equal("found:42", input[2].GetProperty("output").GetString());
    }

    [Fact]
    public async Task RetainsAnnotatedTextAndRefusalPartsForExactReplayAndSessionRestore()
    {
        var annotations = "[{\"type\":\"url_citation\",\"url\":\"https://example.test\",\"title\":\"source\",\"start_index\":0,\"end_index\":5}]";
        var response = TextResponse().Replace("\"annotations\":[]", "\"annotations\":" + annotations, StringComparison.Ordinal);
        using var handler = new Handler(response, TextResponse());
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")) });
        await agent.InvokeAsync("go");
        var block = Assert.IsType<ResponsesContentBlock>(agent.Messages[1].Content[0]);
        Assert.Equal("https://example.test", block.Part.GetProperty("annotations")[0].GetProperty("url").GetString());
        var copy = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(agent.Messages[1]))!;
        Assert.Equal("hello", copy.Text);
        Assert.Equal(block.Part.GetRawText(), Assert.IsType<ResponsesContentBlock>(copy.Content[0]).Part.GetRawText());
        await agent.InvokeAsync("next");
        var replay = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input")[1];
        Assert.Equal("m1", replay.GetProperty("id").GetString());
        Assert.Equal(annotations, replay.GetProperty("content")[0].GetProperty("annotations").GetRawText());
    }

    [Fact]
    public async Task PreservesCompletedEncryptedReasoningRatherThanIncompleteAddedPayload()
    {
        var reasoning = Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"reason","type":"reasoning","summary":[],"encrypted_content":"partial"}}""") +
            Frame("response.reasoning_summary_text.delta", """{"type":"response.reasoning_summary_text.delta","output_index":0,"item_id":"reason","summary_index":0,"delta":"consider"}""") +
            Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"reason","type":"reasoning","summary":[{"type":"summary_text","text":"consider"}],"encrypted_content":"complete"}}""");
        var created = Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""");
        var completed = Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed"}}""");
        using var handler = new Handler(created + reasoning + completed, TextResponse());
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")) });
        await agent.InvokeAsync("go");
        var block = Assert.IsType<ResponsesReasoningBlock>(agent.Messages[1].Content[0]);
        Assert.Equal("complete", block.Item.GetProperty("encrypted_content").GetString());
        Assert.Empty(agent.Messages[1].Text);
        await agent.InvokeAsync("next");
        Assert.Equal("complete", JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input")[1].GetProperty("encrypted_content").GetString());
    }

    [Fact]
    public async Task RejectsFinalContentOverrideThatRewritesStreamedText()
    {
        var part = JsonSerializer.SerializeToElement(new { type = "output_text", text = "rewritten", annotations = Array.Empty<object>() });
        var events = AgentTests.Text("original");
        events[3] = new ModelContentBlockStopEvent(0) { ContentBlock = new ResponsesContentBlock("m", 0, part) };
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(events) });
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("go"));
        Assert.Single(agent.Messages);
    }

    [Theory]
    [InlineData("response.created", "response.other")]
    [InlineData("\"output_index\":0", "\"output_index\":-1")]
    [InlineData("\"item_id\":\"m1\"", "\"item_id\":\"wrong\"")]
    [InlineData("\"input_tokens\":3", "\"input_tokens\":-1")]
    public async Task RejectsMalformedNativeStreams(string original, string replacement)
    {
        using var handler = new Handler(TextResponse().Replace(original, replacement, StringComparison.Ordinal));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => Collect(new OpenAIResponsesModel(client, new("model", "key")), new([Message.User("go")])));
    }

    [Fact]
    public async Task RejectsUnsupportedSignedAnthropicReasoningBeforeSendingRequest()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key"));
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(model, new([new(Role.Assistant, [new ReasoningBlock("secret", "anthropic-signature")])])));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task RetainsRefusalTypeWhileDisplayingItsExplanation()
    {
        var refusal = TextResponse().Replace("response.output_text.delta", "response.refusal.delta", StringComparison.Ordinal).Replace("\"output_text\"", "\"refusal\"", StringComparison.Ordinal).Replace("\"text\":", "\"refusal\":", StringComparison.Ordinal).Replace("hello", "cannot assist", StringComparison.Ordinal);
        using var handler = new Handler(refusal, TextResponse());
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")) });
        Assert.Equal("cannot assist", (await agent.InvokeAsync("go")).ToString());
        Assert.Equal("refusal", Assert.IsType<ResponsesContentBlock>(agent.Messages[1].Content[0]).Part.GetProperty("type").GetString());
        await agent.InvokeAsync("next");
        Assert.Equal("refusal", JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input")[1].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task TokenLimitedFunctionResponseNeverExecutesLocalTools()
    {
        var response = FunctionResponse().Replace("response.completed", "response.incomplete", StringComparison.Ordinal).Replace("\"status\":\"completed\"", "\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}", StringComparison.Ordinal);
        using var handler = new Handler(response);
        using var client = new HttpClient(handler);
        var called = false;
        var tool = new FunctionTool(new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { called = true; return ValueTask.FromResult("side effect"); });
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")), Tools = [tool] });
        Assert.Equal(StopReasons.MaxTokens, (await agent.InvokeAsync("go")).StopReason);
        Assert.False(called);
        Assert.Equal(ToolResultStatus.Error, Assert.IsType<ToolResultBlock>(agent.Messages.Last().Content[0]).Status);
    }

    [Fact]
    public async Task FormatsRichToolResultsAsNativeContentArrayWithoutLosingStructuredData()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var result = new ToolResultBlock("call", [new TextBlock("visible"), new ImageBlock("image/png", "AA=="), new DocumentBlock("application/pdf", "AQ==", "file.pdf")], ToolResultStatus.Error, JsonSerializer.SerializeToElement(new { value = 42 }));
        await Collect(new OpenAIResponsesModel(client, new("model", "key")), new([new(Role.User, [result])]));
        var output = JsonSerializer.Deserialize<JsonElement>(handler.Body!).GetProperty("input")[0].GetProperty("output");
        Assert.Equal("[ERROR]", output[0].GetProperty("text").GetString());
        Assert.Equal("visible", output[1].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,AA==", output[2].GetProperty("image_url").GetString());
        Assert.Equal("file.pdf", output[3].GetProperty("filename").GetString());
        Assert.Equal("{\"value\":42}", output[4].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("error", "rate_limit_exceeded", true)]
    [InlineData("response.failed", "context_length_exceeded", false)]
    public async Task ClassifiesNativeErrorEvents(string type, string code, bool throttled)
    {
        var data = type == "error" ? JsonSerializer.Serialize(new { type, code, message = "rejected" }) : JsonSerializer.Serialize(new { type, response = new { error = new { code, message = "rejected" } } });
        using var handler = new Handler(Frame(type, data));
        using var client = new HttpClient(handler);
        var action = () => Collect(new OpenAIResponsesModel(client, new("model", "key")), new([Message.User("go")]));
        if (throttled) await Assert.ThrowsAsync<ModelThrottledException>(action);
        else await Assert.ThrowsAsync<ContextWindowOverflowException>(action);
    }

    [Theory]
    [InlineData("response.completed", "response.in_progress")]
    [InlineData("\"status\":\"completed\",\"usage\"", "\"status\":\"in_progress\",\"usage\"")]
    [InlineData("\"output_index\":0", "\"output_index\":\"zero\"")]
    [InlineData("\"usage\":{\"input_tokens\":3,\"output_tokens\":2,\"total_tokens\":5}", "\"usage\":[]")]
    public async Task RejectsIncompleteOrInconsistentTermination(string original, string replacement)
    {
        using var handler = new Handler(TextResponse().Replace(original, replacement, StringComparison.Ordinal));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => Collect(new OpenAIResponsesModel(client, new("model", "key")), new([Message.User("go")])));
    }

    [Fact]
    public async Task PreservesOpaqueProviderToolOutputWithoutExecutingItLocally()
    {
        var stream = Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""") +
            Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"web","type":"web_search_call","status":"in_progress"}}""") +
            Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"web","type":"web_search_call","status":"completed","action":{"type":"search","query":"strands"}}}""") +
            Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed"}}""");
        using var handler = new Handler(stream, TextResponse());
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")) });
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("go")).StopReason);
        Assert.IsType<ResponsesOutputItemBlock>(agent.Messages[1].Content[0]);
        await agent.InvokeAsync("next");
        Assert.Equal("strands", JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input")[1].GetProperty("action").GetProperty("query").GetString());
    }

    [Fact]
    public async Task CancellationBeforeSendingRetainsCallerTokenAndBorrowedClient()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var events = model.StreamAsync(new([Message.User("go")]), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask());
        await events.DisposeAsync();
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(handler.Bodies);
        Assert.NotEmpty(await Collect(model, new([Message.User("still usable")])));
    }

    [Fact]
    public async Task PreservesNativeMessageMetadataAndMixedInputOrder()
    {
        var response = TextResponse().Replace("\"status\":\"completed\",\"content\"", "\"status\":\"completed\",\"phase\":\"final_answer\",\"content\"", StringComparison.Ordinal);
        using var handler = new Handler(response, TextResponse());
        using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "key"));
        var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("go");
        var native = Assert.IsType<ResponsesContentBlock>(agent.Messages[1].Content[0]);
        await Collect(model, new([new(Role.Assistant, [native, new TextBlock("following")])]));
        var items = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input");
        Assert.Equal("message", items[0].GetProperty("type").GetString());
        Assert.Equal("final_answer", items[0].GetProperty("phase").GetString());
        Assert.Equal("following", items[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task RetainsEmptyNativeMessageItemForReplay()
    {
        var stream = Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""") +
            Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"empty","type":"message","role":"assistant","status":"in_progress","content":[]}}""") +
            Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"empty","type":"message","role":"assistant","status":"completed","content":[]}}""") +
            Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed"}}""");
        using var handler = new Handler(stream, TextResponse());
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")) });
        await agent.InvokeAsync("go");
        Assert.IsType<ResponsesOutputItemBlock>(Assert.Single(agent.Messages[1].Content));
        await agent.InvokeAsync("next");
        Assert.Equal("empty", JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input")[1].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("input")]
    [InlineData("kind")]
    public async Task RejectsFinalOverrideThatChangesToolIdentityInputOrKind(string changed)
    {
        var input = JsonSerializer.SerializeToElement(new { value = 1 });
        ContentBlock replacement = changed == "kind" ? new TextBlock("not a tool") : new ToolUseBlock(changed == "id" ? "other" : "call", changed == "name" ? "other" : "lookup", changed == "input" ? JsonSerializer.SerializeToElement(new { value = 2 }) : input);
        ModelStreamEvent[] events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call", "lookup", input)), new ModelContentBlockStopEvent(0) { ContentBlock = replacement }, new ModelMessageStopEvent(StopReasons.ToolUse)];
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(events) });
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("go"));
        Assert.Single(agent.Messages);
    }

    [Fact]
    public async Task DecodesFragmentedUtf8CrLfAndMultilineSse()
    {
        var sse = "\uFEFF: keepalive\r\n\r\n" + TextResponse().Replace("hello", "hé😀", StringComparison.Ordinal)
            .Replace("data: {\"type\":\"response.created\",", "data: {\"type\":\"response.created\",\ndata: ", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        using var body = new ChunkedStream(sse);
        using var handler = new StreamHandler(body);
        using var client = new HttpClient(handler);
        var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")) });
        Assert.Equal("hé😀", (await agent.InvokeAsync("go")).ToString());
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task EarlyEnumeratorDisposalClosesOwnedResponseButLeavesClientBorrowed()
    {
        using var body = new ChunkedStream(TextResponse());
        using var handler = new StreamHandler(body);
        using var client = new HttpClient(handler);
        var stream = new OpenAIResponsesModel(client, new("model", "key")).StreamAsync(new([Message.User("go")])).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        await stream.DisposeAsync();
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task CancellationInterruptsBodyThatIgnoresReadToken()
    {
        using var body = new HangingStream(Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}"""));
        using var handler = new StreamHandler(body);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var stream = new OpenAIResponsesModel(client, new("model", "key")).StreamAsync(new([Message.User("go")]), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var pending = stream.MoveNextAsync().AsTask();
        await body.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await stream.DisposeAsync();
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task InterleavedFunctionsKeepOutputOrderAndCallCorrelation()
    {
        static string Add(int index, string id) => Frame("response.output_item.added", JsonSerializer.Serialize(new { type = "response.output_item.added", output_index = index, item = new { id = "f" + index, type = "function_call", call_id = id, name = "lookup", arguments = "" } }));
        static string Delta(int index, string delta) => Frame("response.function_call_arguments.delta", JsonSerializer.Serialize(new { type = "response.function_call_arguments.delta", output_index = index, item_id = "f" + index, delta }));
        static string Done(int index, string id, int number) => Frame("response.output_item.done", JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = index, item = new { id = "f" + index, type = "function_call", call_id = id, name = "lookup", arguments = "{\"value\":" + number + "}" } }));
        var stream = Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""") + Add(0, "first") + Add(1, "second") +
            Delta(1, "{\"value\":") + Delta(0, "{\"value\":1}") + Delta(1, "2}") + Done(1, "second", 2) + Done(0, "first", 1) +
            Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed"}}""");
        using var handler = new Handler(stream, TextResponse());
        using var client = new HttpClient(handler);
        var tool = new FunctionTool(new("lookup", "Find", JsonSerializer.SerializeToElement(new { type = "object" })), (input, _, _) => ValueTask.FromResult(input.GetRawText()));
        await using var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")), Tools = [tool] });
        await agent.InvokeAsync("go");
        Assert.Equal(["first", "second"], agent.Messages[1].Content.OfType<ToolUseBlock>().Select(value => value.ToolUseId));
        Assert.Equal([1, 2], agent.Messages[1].Content.OfType<ToolUseBlock>().Select(value => value.Input.GetProperty("value").GetInt32()));
        var items = JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input");
        Assert.Equal("first", items[3].GetProperty("call_id").GetString());
        Assert.Equal("second", items[4].GetProperty("call_id").GetString());
    }

    [Fact]
    public async Task CompletedNativePartsCloneProducerJsonBeforeItsLifetimeEnds()
    {
        using var producer = JsonDocument.Parse("""{"type":"message","id":"m","role":"assistant","status":"completed","phase":"final_answer","content":[{"type":"output_text","text":"visible","annotations":[{"type":"url_citation","url":"https://example.test"}]}]}""");
        var block = new ResponsesContentBlock("m", 0, producer.RootElement.GetProperty("content")[0], item: producer.RootElement);
        ModelStreamEvent[] events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("visible") }, new ModelContentBlockStopEvent(0) { ContentBlock = block }, new ModelMessageStopEvent(StopReasons.EndTurn)];
        var agent = new Agent(new() { Model = new AgentTests.ScriptedModel(events) });
        producer.Dispose();
        await agent.InvokeAsync("go");
        var retained = Assert.IsType<ResponsesContentBlock>(agent.Messages[1].Content[0]);
        Assert.Equal("final_answer", retained.Item!.Value.GetProperty("phase").GetString());
        Assert.Equal("https://example.test", retained.Part.GetProperty("annotations")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task RetainsResponseMetadataOutsideModelInputWithoutDuplicatingOutput()
    {
        var response = TextResponse().Replace("\"status\":\"completed\",\"usage\"", "\"status\":\"completed\",\"service_tier\":\"default\",\"usage\"", StringComparison.Ordinal);
        using var handler = new Handler(response, TextResponse());
        using var client = new HttpClient(handler);
        await using var agent = new Agent(new() { Model = new OpenAIResponsesModel(client, new("model", "key")) });
        await agent.InvokeAsync("go");
        var metadata = agent.Messages[1].Metadata!.Value.GetProperty("openaiResponses");
        Assert.Equal("r1", metadata.GetProperty("id").GetString());
        Assert.Equal("default", metadata.GetProperty("service_tier").GetString());
        Assert.False(metadata.TryGetProperty("output", out _));
        await agent.InvokeAsync("again");
        Assert.False(JsonSerializer.Deserialize<JsonElement>(handler.Bodies[1]).GetProperty("input")[1].TryGetProperty("openaiResponses", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "text/event-stream", true)]
    [InlineData(HttpStatusCode.OK, "application/json", false)]
    public async Task RejectsHttpErrorsAndNonSseResponsesAndClosesBody(HttpStatusCode status, string mediaType, bool throttled)
    {
        using var body = new ChunkedStream("""{"error":{"code":"rate_limit_exceeded","message":"busy"}}""");
        using var handler = new StreamHandler(body, status, mediaType);
        using var client = new HttpClient(handler);
        var action = () => Collect(new OpenAIResponsesModel(client, new("model", "key")), new([Message.User("go")]));
        if (throttled) await Assert.ThrowsAsync<ModelThrottledException>(action);
        else await Assert.ThrowsAsync<ModelException>(action);
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public void ExposesModelIdentityAndEffectiveConfiguredOutputAllowance()
    {
        using var handler = new Handler(TextResponse());
        using var client = new HttpClient(handler);
        var parameters = JsonSerializer.SerializeToElement(new { max_output_tokens = 90 });
        Assert.Equal(new ModelMetadata("model", MaxOutputTokens: 90), new OpenAIResponsesModel(client, new("model", "key") { Params = parameters }).Metadata);
        Assert.Equal(new ModelMetadata("model", MaxOutputTokens: 25), new OpenAIResponsesModel(client, new("model", "key") { Params = parameters, MaxTokens = 25 }).Metadata);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplaceMalformedStreamError()
    {
        var body = new ThrowingDisposeStream("data: not-json\n\n");
        using var handler = new StreamHandler(body);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelException>(() => Collect(new OpenAIResponsesModel(client, new("model", "key")), new([Message.User("go")])));
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
        Assert.Contains(body.Failure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
    }

    private sealed class ThrowingDisposeStream(string content) : ChunkedStream(content)
    {
        internal IOException Failure { get; } = new("body close failed");
        protected override void Dispose(bool disposing) { base.Dispose(disposing); throw Failure; }
    }

    private class ChunkedStream(string content) : Stream
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

    private sealed class HangingStream(string content) : ChunkedStream(content)
    {
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = Read(buffer, offset, count);
            if (read != 0) return read;
            Waiting.TrySetResult(true);
            await _closed.Task;
            throw new ObjectDisposedException(nameof(HangingStream));
        }
        protected override void Dispose(bool disposing) { _closed.TrySetResult(0); base.Dispose(disposing); }
    }

    private sealed class StreamHandler(Stream content, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/event-stream") : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StreamContent(content) };
            response.Content.Headers.ContentType = new(mediaType);
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    internal static string Frame(string type, string json) => "event: " + type + "\ndata: " + json + "\n\n";
    internal static string TextResponse() =>
        Frame("response.created", """{"type":"response.created","response":{"id":"r1","status":"in_progress"}}""") +
        Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"in_progress","content":[]}}""") +
        Frame("response.content_part.added", """{"type":"response.content_part.added","output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""") +
        Frame("response.output_text.delta", """{"type":"response.output_text.delta","output_index":0,"item_id":"m1","content_index":0,"delta":"hello"}""") +
        Frame("response.content_part.done", """{"type":"response.content_part.done","output_index":0,"item_id":"m1","content_index":0,"part":{"type":"output_text","text":"hello","annotations":[]}}""") +
        Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"m1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hello","annotations":[]}]}}""") +
        Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed","usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}}""");
    internal static async Task<List<ModelStreamEvent>> Collect(IModel model, ModelRequest request)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var value in model.StreamAsync(request)) events.Add(value);
        return events;
    }
    internal static string FunctionResponse() => Frame("response.created", """{"type":"response.created","response":{"id":"r1"}}""") +
        Frame("response.output_item.added", """{"type":"response.output_item.added","output_index":0,"item":{"id":"f1","type":"function_call","call_id":"call","name":"lookup","arguments":""}}""") +
        Frame("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":0,"item_id":"f1","delta":"{\"value\":"}""") +
        Frame("response.function_call_arguments.delta", """{"type":"response.function_call_arguments.delta","output_index":0,"item_id":"f1","delta":"42}"}""") +
        Frame("response.function_call_arguments.done", """{"type":"response.function_call_arguments.done","output_index":0,"item_id":"f1","arguments":"{\"value\":42}"}""") +
        Frame("response.output_item.done", """{"type":"response.output_item.done","output_index":0,"item":{"id":"f1","type":"function_call","call_id":"call","name":"lookup","arguments":"{\"value\":42}"}}""") +
        Frame("response.completed", """{"type":"response.completed","response":{"id":"r1","status":"completed"}}""");
    internal sealed class Handler(params string[] content) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal Uri? Uri { get; private set; }
        internal string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(Body);
            return new(HttpStatusCode.OK) { Content = new StringContent(content[Math.Min(Bodies.Count - 1, content.Length - 1)], Encoding.UTF8, "text/event-stream") };
        }
    }
}
