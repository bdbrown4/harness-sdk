using System.Net;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class ProviderTests
{
    [Fact]
    public async Task TokenLimitedToolResponseIsTerminalAndNeverExecutesTheTool()
    {
        var stream = OpenAIFrame("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"limited","type":"function","function":{"name":"save","arguments":"{}"}}]},"finish_reason":"length"}]}""") + OpenAIFrame("[DONE]");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        var executed = false;
        var tool = new FunctionTool(new("save", "Save a value", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => { executed = true; return ValueTask.FromResult("saved"); });
        var agent = new Agent(new() { Model = new OpenAIModel(client, new("model", "key")), Tools = [tool] });

        var result = await agent.InvokeAsync("save");

        Assert.Equal(StopReasons.MaxTokens, result.StopReason);
        Assert.False(executed);
        Assert.Equal(1, handler.CallCount);
        var reply = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[^1].Content));
        Assert.Equal("limited", reply.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, reply.Status);
        Assert.Contains(StopReasons.MaxTokens, reply.Content);
    }

    [Fact]
    public async Task OpenAITranslatesToolConversationAndStreamsInterleavedCalls()
    {
        var stream = OpenAIFrame("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"Checking "},"finish_reason":null}]}""")
            + OpenAIFrame("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","type":"function","function":{"name":"lookup","arguments":"{\"id\":"}},{"index":1,"id":"b","type":"function","function":{"name":"lookup","arguments":"{\"id\":2}"}}]},"finish_reason":null}]}""")
            + OpenAIFrame("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":"tool_calls"}]}""")
            + OpenAIFrame("""{"choices":[],"usage":{"prompt_tokens":15,"completion_tokens":4,"prompt_tokens_details":{"cached_tokens":6}}}""")
            + OpenAIFrame("[DONE]");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/v1/") };
        var model = new OpenAIModel(client, new("test-model", "test-key") { MaxTokens = 70, Temperature = 0.2 });
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var request = new ModelRequest([
            Message.User("Find it"),
            new(Role.Assistant, [new TextBlock("Looking"), new ToolUseBlock("previous", "lookup", schema)]),
            new(Role.User, [new TextBlock("More"), new ToolResultBlock("previous", "failed", ToolResultStatus.Error)])
        ], "Be helpful", [new("lookup", "Looks up an item", schema)]);

        var events = await CollectAsync(model, request);

        Assert.Equal(new Uri("https://example.test/v1/chat/completions"), handler.RequestUri);
        Assert.Equal("Bearer test-key", handler.Authorization);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(70, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("function", body.GetProperty("tools")[0].GetProperty("type").GetString());
        var messages = body.GetProperty("messages");
        Assert.Equal(new[] { "system", "user", "assistant", "tool", "user" }, messages.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("previous", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("[ERROR] failed", messages[3].GetProperty("content").GetString());
        Assert.Equal(new ModelMessageStartEvent(), events[0]);
        Assert.Equal(new[] { "a", "b" }, events.OfType<ModelContentBlockStartEvent>().Where(item => item.ToolUse is not null).Select(item => item.ToolUse!.ToolUseId));
        var toolStarts = events.OfType<ModelContentBlockStartEvent>().Where(item => item.ToolUse is not null).ToArray();
        Assert.Equal("{\"id\":1}", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Where(item => item.Index == toolStarts[0].Index).Select(item => item.ToolInput)));
        Assert.Equal("{\"id\":2}", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Where(item => item.Index == toolStarts[1].Index).Select(item => item.ToolInput)));
        Assert.Equal(3, events.OfType<ModelContentBlockStopEvent>().Count());
        Assert.Equal(new Usage(15, 4, 6), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.Equal(new ModelMessageStopEvent(StopReasons.ToolUse), events[^1]);
    }

    [Fact]
    public async Task OpenAIParsesMultilineSseCommentsCrLfAndUtf8()
    {
        const string stream = ": keep alive\r\nevent: message\r\ndata: {\"choices\":[{\"index\":0,\r\ndata: \"delta\":{\"content\":\"Café ☕\"},\"finish_reason\":\"stop\"}]}\r\n\r\ndata: [DONE]\r\n\r\n";
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        var events = await CollectAsync(new OpenAIModel(client, new("model", "key")), new([Message.User("Hi")]));
        Assert.Equal("Café ☕", Assert.Single(events.OfType<ModelContentBlockDeltaEvent>()).Text);
        Assert.Equal(new ModelMessageStopEvent(StopReasons.EndTurn), events[^1]);
    }

    [Theory]
    [InlineData("length", StopReasons.MaxTokens)]
    [InlineData("content_filter", StopReasons.ContentFiltered)]
    [InlineData("custom_reason", "custom_reason")]
    public async Task OpenAIMapsStopReasons(string nativeReason, string expected)
    {
        using var handler = new RecordingHandler(OpenAIFrame($$"""{"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"{{nativeReason}}"}]}""") + OpenAIFrame("[DONE]"));
        using var client = new HttpClient(handler);
        var events = await CollectAsync(new OpenAIModel(client, new("model", "key")), new([Message.User("Hi")]));
        Assert.Equal(expected, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
    }

    [Theory]
    [InlineData("data: {bad}\n\n")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n")]
    [InlineData("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]")]
    [InlineData("data: {\"choices\":[{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")]
    public async Task OpenAIRejectsMalformedOrTruncatedStreams(string stream)
    {
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new OpenAIModel(client, new("model", "key")), new([Message.User("Hi")])));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task OpenAIRejectsMalformedToolArguments()
    {
        var stream = OpenAIFrame("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"lookup","arguments":"{bad"}}]},"finish_reason":"tool_calls"}]}""") + OpenAIFrame("[DONE]");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new OpenAIModel(client, new("model", "key")), new([Message.User("Hi")])));
    }

    [Fact]
    public async Task ConvertsReasoningToAssistantTextUsingUpstreamChatContract()
    {
        using var handler = new RecordingHandler(OpenAIFrame("""{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""") + OpenAIFrame("[DONE]"));
        using var client = new HttpClient(handler);
        var request = new ModelRequest([new(Role.Assistant, [new ReasoningBlock("private", "signed")])]);
        await CollectAsync(new OpenAIModel(client, new("model", "key")), request);
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal("private", document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.DoesNotContain("signed", handler.Body!, StringComparison.Ordinal);
        Assert.Equal("Bearer key", handler.Authorization);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AnthropicTranslatesSignedReasoningAndToolResults()
    {
        var stream = AnthropicStart()
            + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""")
            + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Consider"}}""")
            + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"signature"}}""")
            + AnthropicFrame("content_block_stop", """{"type":"content_block_stop","index":0}""")
            + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call","name":"lookup","input":{}}}""")
            + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"id\":"}}""")
            + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"3}"}}""")
            + AnthropicFrame("content_block_stop", """{"type":"content_block_stop","index":1}""")
            + AnthropicFinish("tool_use");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-test", "anthropic-key") { Endpoint = new("https://example.test/custom"), MaxTokens = 5000, ThinkingBudgetTokens = 1500 });
        var request = new ModelRequest([
            new(Role.Assistant, [new ReasoningBlock("Thinking", "signed"), new ToolUseBlock("old", "lookup", JsonSerializer.SerializeToElement(new { id = 1 }))]),
            new(Role.User, [new ToolResultBlock("old", "unavailable", ToolResultStatus.Error)])
        ], "System", [new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" }))]);

        var events = await CollectAsync(model, request);

        Assert.Equal("anthropic-key", handler.ApiKey);
        Assert.Equal("2023-06-01", handler.AnthropicVersion);
        Assert.Equal(new Uri("https://example.test/custom"), handler.RequestUri);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("System", body.GetProperty("system").GetString());
        Assert.Equal(1500, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.Equal("signed", body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("signature").GetString());
        Assert.True(body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("is_error").GetBoolean());
        Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("Consider", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(item => item.Reasoning)));
        Assert.Equal("signature", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(item => item.Signature)));
        Assert.Equal("{\"id\":3}", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(item => item.ToolInput)));
        Assert.Equal(new Usage(10, 7, 3, 2, 22), Assert.Single(events.OfType<ModelMetadataEvent>()).Usage);
        Assert.Equal(new ModelMessageStopEvent(StopReasons.ToolUse), events[^1]);
    }

    [Fact]
    public async Task AnthropicStreamsInitialTextAndPreservesProviderIndexes()
    {
        var stream = AnthropicStart()
            + AnthropicFrame("ping", """{"type":"ping"}""")
            + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":4,"content_block":{"type":"text","text":"Hello"}}""")
            + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":4,"delta":{"type":"text_delta","text":" world"}}""")
            + AnthropicFrame("content_block_stop", """{"type":"content_block_stop","index":4}""")
            + AnthropicFinish("end_turn");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler) { BaseAddress = new("https://example.test/v1/") };
        var events = await CollectAsync(new AnthropicModel(client, new("model", "key")), new([Message.User("Hi")]));
        Assert.Equal(new Uri("https://example.test/v1/messages"), handler.RequestUri);
        Assert.Equal("Hello world", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(item => item.Text)));
        Assert.All(events.OfType<ModelContentBlockDeltaEvent>(), item => Assert.Equal(4, item.Index));
    }

    [Theory]
    [InlineData("max_tokens", StopReasons.MaxTokens)]
    [InlineData("stop_sequence", StopReasons.StopSequence)]
    [InlineData("refusal", "refusal")]
    public async Task AnthropicMapsStopReasons(string nativeReason, string expected)
    {
        using var handler = new RecordingHandler(AnthropicStart() + AnthropicFinish(nativeReason));
        using var client = new HttpClient(handler);
        var events = await CollectAsync(new AnthropicModel(client, new("model", "key")), new([Message.User("Hi")]));
        Assert.Equal(expected, Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
    }

    [Theory]
    [InlineData("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n")]
    [InlineData("event: message_start\ndata: {bad}\n\n")]
    [InlineData("")]
    public async Task AnthropicRejectsMalformedOrTruncatedStreams(string stream)
    {
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new AnthropicModel(client, new("model", "key")), new([Message.User("Hi")])));
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("text", "{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}")]
    [InlineData("thinking", "{\"type\":\"text_delta\",\"text\":\"oops\"}")]
    public async Task AnthropicRejectsDeltaThatDoesNotMatchBlock(string kind, string delta)
    {
        var stream = AnthropicStart()
            + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"KIND","text":"","thinking":""}}""".Replace("KIND", kind, StringComparison.Ordinal))
            + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":0,"delta":DELTA}""".Replace("DELTA", delta, StringComparison.Ordinal));
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new AnthropicModel(client, new("model", "key")), new([Message.User("Hi")])));
    }

    [Fact]
    public async Task AnthropicRejectsUnclosedAndUnsupportedBlocks()
    {
        using var handler = new RecordingHandler(AnthropicStart()
            + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"secret"}}"""));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(new AnthropicModel(client, new("model", "key")), new([Message.User("Hi")])));
    }

    [Theory]
    [InlineData(false, 429, "rate_limit_exceeded", typeof(ModelThrottledException))]
    [InlineData(true, 529, "overloaded_error", typeof(ModelThrottledException))]
    [InlineData(false, 400, "context_length_exceeded", typeof(ContextWindowOverflowException))]
    [InlineData(true, 400, "invalid_request_error", typeof(ContextWindowOverflowException))]
    [InlineData(false, 401, "invalid_api_key", typeof(ModelException))]
    public async Task ProvidersTranslateHttpErrorsWithoutRetry(bool anthropic, int status, string code, Type expected)
    {
        using var handler = new RecordingHandler("""{"error":{"type":"CODE","code":"CODE","message":"prompt is too long"}}""".Replace("CODE", code, StringComparison.Ordinal), (HttpStatusCode)status);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        var error = await Record.ExceptionAsync(() => CollectAsync(model, new([Message.User("Hi")])));
        Assert.IsType(expected, error);
        Assert.IsType<HttpRequestException>(error!.InnerException);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvidersTranslateInStreamErrors(bool anthropic)
    {
        var stream = anthropic ? AnthropicFrame("error", """{"type":"error","error":{"type":"overloaded_error","message":"Busy"}}""")
            : OpenAIFrame("""{"error":{"code":"rate_limit_exceeded","message":"Busy"}}""");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await Assert.ThrowsAsync<ModelThrottledException>(() => CollectAsync(model, new([Message.User("Hi")])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvidersYieldBeforeUpstreamCompletesAndDisposeOnConsumerExit(bool anthropic)
    {
        var prefix = anthropic ? AnthropicStart() : OpenAIFrame("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}""");
        using var body = new PausingStream(prefix);
        using var handler = new RecordingHandler(body);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await using (var enumerator = model.StreamAsync(new([Message.User("Hi")])).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsType<ModelMessageStartEvent>(enumerator.Current);
            Assert.False(body.Disposed);
        }
        Assert.True(body.Disposed);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvidersHonorCancellationDuringRead(bool anthropic)
    {
        using var body = new PausingStream(string.Empty);
        using var handler = new RecordingHandler(body);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model, new([Message.User("Hi")]), cancellation.Token));
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void OptionsRejectInvalidConfiguration()
    {
        using var client = new HttpClient();
        Assert.Throws<ArgumentException>(() => new OpenAIModel(client, new("", "key")));
        Assert.Throws<ArgumentException>(() => new AnthropicModel(client, new("model", "")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAIModel(client, new("model", "key") { MaxTokens = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnthropicModel(client, new("model", "key") { ThinkingBudgetTokens = 100 }));
        Assert.Throws<ArgumentException>(() => new OpenAIModel(client, new("model", "key") { Endpoint = new("relative", UriKind.Relative) }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonNumericIndexesProduceModelExceptions(bool anthropic)
    {
        var stream = anthropic ? AnthropicStart() + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":"zero","content_block":{"type":"text","text":""}}""")
            : OpenAIFrame("""{"choices":[{"index":"zero","delta":{},"finish_reason":"stop"}]}""");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, new([Message.User("Hi")])));
    }

    [Fact]
    public async Task OversizedSseLineFailsBeforeWaitingForItsTerminator()
    {
        using var body = new PausingStream("data: " + new string('x', 4 * 1024 * 1024));
        using var handler = new RecordingHandler(body);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var model = new OpenAIModel(client, new("model", "key"));
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, new([Message.User("Hi")]), cancellation.Token));
        Assert.Contains("size limit", error.Message, StringComparison.Ordinal);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task OpenAIPreservesReportedTotalTokens()
    {
        var stream = OpenAIFrame("""{"choices":[{"index":0,"delta":{"content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":17}}""") + OpenAIFrame("[DONE]");
        using var handler = new RecordingHandler(stream);
        using var client = new HttpClient(handler);
        var events = await CollectAsync(new OpenAIModel(client, new("model", "key")), new([Message.User("Hi")]));
        Assert.Equal(17, Assert.Single(events.OfType<ModelMetadataEvent>()).Usage.TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvidersCompleteNativeAgentToolLoop(bool anthropic)
    {
        var first = anthropic
            ? AnthropicStart()
                + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call","name":"lookup","input":{}}}""")
                + AnthropicFrame("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"id\":3}"}}""")
                + AnthropicFrame("content_block_stop", """{"type":"content_block_stop","index":0}""") + AnthropicFinish("tool_use")
            : OpenAIFrame("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call","function":{"name":"lookup","arguments":"{\"id\":3}"}}]},"finish_reason":"tool_calls"}]}""") + OpenAIFrame("[DONE]");
        var second = anthropic
            ? AnthropicStart()
                + AnthropicFrame("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"Found item:3"}}""")
                + AnthropicFrame("content_block_stop", """{"type":"content_block_stop","index":0}""") + AnthropicFinish("end_turn")
            : OpenAIFrame("""{"choices":[{"index":0,"delta":{"content":"Found item:3"},"finish_reason":"stop"}]}""") + OpenAIFrame("[DONE]");
        using var handler = new SequenceHandler(first, second);
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        var tool = new FunctionTool(new("lookup", "Looks up an item", JsonSerializer.SerializeToElement(new { type = "object" })),
            (input, _, _) => ValueTask.FromResult($"item:{input.GetProperty("id").GetInt32()}"));
        var agent = new Agent(new() { Model = model, Tools = [tool] });

        var result = await agent.InvokeAsync("Find item 3");

        Assert.Equal("Found item:3", result.Message!.Text);
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(2, result.Turns);
        Assert.Equal(4, agent.Messages.Count);
        Assert.Equal(2, handler.Bodies.Count);
        var secondRequest = JsonDocument.Parse(handler.Bodies[1]).RootElement;
        var toolReply = secondRequest.GetProperty("messages")[2];
        Assert.Equal("item:3", anthropic ? toolReply.GetProperty("content")[0].GetProperty("content").GetString() : toolReply.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvidersPreserveTransportErrors(bool anthropic)
    {
        using var handler = new FailedRequestHandler();
        using var client = new HttpClient(handler);
        IModel model = anthropic ? new AnthropicModel(client, new("model", "key")) : new OpenAIModel(client, new("model", "key"));
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, new([Message.User("Hi")])));
        Assert.Same(handler.Error, error.InnerException);
        Assert.Equal(1, handler.CallCount);
    }

    private static async Task<List<ModelStreamEvent>> CollectAsync(IModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(request, cancellationToken)) events.Add(item);
        return events;
    }

    private static string OpenAIFrame(string json) => $"data: {json}\n\n";
    private static string AnthropicFrame(string name, string json) => $"event: {name}\ndata: {json}\n\n";
    private static string AnthropicStart() => AnthropicFrame("message_start", """{"type":"message_start","message":{"role":"assistant","content":[],"usage":{"input_tokens":10,"output_tokens":1,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}}}""");
    private static string AnthropicFinish(string reason) => AnthropicFrame("message_delta", """{"type":"message_delta","delta":{"stop_reason":"REASON"},"usage":{"output_tokens":7}}""".Replace("REASON", reason, StringComparison.Ordinal)) + AnthropicFrame("message_stop", """{"type":"message_stop"}""");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Stream _body;
        private readonly HttpStatusCode _status;
        public RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : this(new MemoryStream(Encoding.UTF8.GetBytes(body)), status) { }
        public RecordingHandler(Stream body, HttpStatusCode status = HttpStatusCode.OK) { _body = body; _status = status; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }
        public string? AnthropicVersion { get; private set; }
        public int CallCount { get; private set; }
        public bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : null;
            AnthropicVersion = request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null;
            var response = new HttpResponseMessage(_status) { Content = new StreamContent(_body) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class FailedRequestHandler : HttpMessageHandler
    {
        public HttpRequestException Error { get; } = new("Connection lost");
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromException<HttpResponseMessage>(Error);
        }
    }

    private sealed class PausingStream(string prefix) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = _prefix.Read(buffer.Span);
            if (count != 0) return count;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; _prefix.Dispose(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
