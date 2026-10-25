using System.Net.Http;
using System.Text.Json;
using Strands.Agents.Models;
using static Strands.Agents.Tests.Models.LlamaApiTestTransport;

namespace Strands.Agents.Tests.Models;

public sealed class LlamaApiModelTests
{
    [Fact]
    public async Task NativeMetaProtocolAndAllSourceGenerationSettingsProduceACompleteResult()
    {
        var wire = TextStream("hello 雪") + Frame("""{"event":{"event_type":"metrics","delta":{"type":"text","text":""},"metrics":[{"metric":"num_prompt_tokens","value":10.0},{"metric":"num_completion_tokens","value":2.0},{"metric":"num_total_tokens","value":12.0}]}}""");
        using var transport = new LlamaApiTestTransport(wire); using var client = new HttpClient(transport);
        var model = new LlamaApiModel(client, new("meta-model", "secret") { RepetitionPenalty = 1.1, Temperature = 0.2, TopP = 0.9, MaxCompletionTokens = 30, TopK = 10 });
        var events = await CollectAsync(model, new([Message.User("hello")], "instructions")); var completed = Complete(events);
        Assert.Equal("hello 雪", completed.Message.Text); Assert.Equal(StopReasons.EndTurn, completed.StopReason);
        var usage = Assert.Single(events.OfType<ModelMetadataEvent>()); Assert.Equal(10, usage.Usage.InputTokens); Assert.Equal(2, usage.Usage.OutputTokens); Assert.Equal(12, usage.Usage.TotalTokens); Assert.True(usage.UsageReported); Assert.Null(usage.PerformanceMetrics);
        var body = Assert.Single(transport.Bodies); Assert.Equal("meta-model", body.GetProperty("model").GetString()); Assert.True(body.GetProperty("stream").GetBoolean()); Assert.Empty(body.GetProperty("tools").EnumerateArray());
        Assert.Equal(1.1, body.GetProperty("repetition_penalty").GetDouble()); Assert.Equal(0.2, body.GetProperty("temperature").GetDouble()); Assert.Equal(0.9, body.GetProperty("top_p").GetDouble()); Assert.Equal(30, body.GetProperty("max_completion_tokens").GetInt32()); Assert.Equal(10, body.GetProperty("top_k").GetInt32());
        Assert.Equal("instructions", body.GetProperty("messages")[0].GetProperty("content").GetString()); Assert.Equal("Bearer secret", Assert.Single(transport.Authorizations)); Assert.Equal(new Uri("https://api.llama.com/v1/chat/completions"), transport.Endpoint);
    }

    [Fact]
    public async Task ToolCallsKeepTheFirstArgumentFragmentAndOmittedSubsequentIdentity()
    {
        var wire = Frame("""{"event":{"event_type":"start","delta":{"type":"text","text":""}}}""") +
            Frame("""{"event":{"event_type":"progress","delta":{"type":"tool_call","id":"call-1","function":{"name":"lookup","arguments":"{\"key\":"}}}}""") +
            Frame("""{"event":{"event_type":"complete","delta":{"type":"tool_call","function":{"arguments":"7}"}},"stop_reason":"tool_calls"}}""");
        using var transport = new LlamaApiTestTransport(wire); using var client = new HttpClient(transport); var model = new LlamaApiModel(client, new("meta", "key"));
        var completed = Complete(await CollectAsync(model, new([Message.User("lookup")])));
        var call = Assert.Single(completed.Message.Content.OfType<ToolUseBlock>()); Assert.Equal("call-1", call.ToolUseId); Assert.Equal("lookup", call.Name); Assert.Equal(7, call.Input.GetProperty("key").GetInt32()); Assert.Equal(StopReasons.ToolUse, completed.StopReason);
    }

    [Theory]
    [InlineData("stop", StopReasons.EndTurn)]
    [InlineData("length", StopReasons.MaxTokens)]
    public async Task CleanEofAfterNativeCompletionDoesNotRequireAnOpenAiTerminator(string reason, string expected)
    {
        using var transport = new LlamaApiTestTransport(TextStream(reason: reason)); using var client = new HttpClient(transport);
        var events = await CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([Message.User("input")]));
        Assert.Equal(expected, Complete(events).StopReason); Assert.Empty(events.OfType<ModelMetadataEvent>());
    }

    [Fact]
    public void TypedConfigurationUpdatesValidateAndExposeAnImmutableRedactedSnapshot()
    {
        using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport); var model = new LlamaApiModel(client, new("old", "secret")); var before = model.GetConfig();
        model.UpdateConfig(options => options with { ModelId = "new", MaxCompletionTokens = 20 });
        Assert.Equal("old", before.ModelId); Assert.Equal("new", model.GetConfig().ModelId); Assert.Equal(20, model.Metadata.MaxOutputTokens); Assert.DoesNotContain("secret", model.GetConfig().ToString());
    }

    [Fact]
    public async Task UserImagesAssistantTextAndCorrelatedJsonToolResultsKeepTheirSourceShape()
    {
        using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport) { BaseAddress = new Uri("https://example.test/meta/") };
        var input = JsonSerializer.SerializeToElement(new { query = "東京" }); var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var result = new ToolResultBlock("call", "display") { ContentBlocks = [new TextBlock("text"), new JsonBlock(input)], StructuredContent = input };
        await CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([
            new(Role.User, [new TextBlock("look"), new ImageBlock("image/png", "AQID")]),
            new(Role.Assistant, [new TextBlock("first"), new TextBlock("second"), new ToolUseBlock("call", "lookup", input)]),
            new(Role.User, [result])], Tools: [new("lookup", "Look up data", schema)]));
        var messages = Assert.Single(transport.Bodies).GetProperty("messages");
        Assert.Equal("data:image/png;base64,AQID", messages[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("firstsecond", messages[1].GetProperty("content").GetProperty("text").GetString());
        var arguments = JsonSerializer.Deserialize<JsonElement>(messages[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!); Assert.Equal("東京", arguments.GetProperty("query").GetString());
        Assert.Equal("call", messages[2].GetProperty("tool_call_id").GetString()); Assert.Equal(2, messages[2].GetProperty("content").GetArrayLength()); Assert.Equal("text", messages[2].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(new Uri("https://example.test/meta/chat/completions"), transport.Endpoint);
    }

    [Fact]
    public async Task ACorrelatedToolResultPreservesTheSourceTextJsonAndInlineImageUnionInOrder()
    {
        using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport);
        var value = JsonSerializer.SerializeToElement(new { key = "東京" });
        var result = new ToolResultBlock("call", "display") { ContentBlocks = [new TextBlock("before"), new JsonBlock(value), new ImageBlock("image/png", "AQID"), new TextBlock("after")] };
        await CollectAsync(new LlamaApiModel(client, new("meta", "key")), new([new(Role.User, [result])]));
        var message = Assert.Single(Assert.Single(transport.Bodies).GetProperty("messages").EnumerateArray());
        Assert.Equal("call", message.GetProperty("tool_call_id").GetString()); var blocks = message.GetProperty("content"); Assert.Equal(4, blocks.GetArrayLength());
        Assert.Equal("before", blocks[0].GetProperty("text").GetString()); Assert.Equal("東京", JsonSerializer.Deserialize<JsonElement>(blocks[1].GetProperty("text").GetString()!).GetProperty("key").GetString());
        Assert.Equal("data:image/png;base64,AQID", blocks[2].GetProperty("image_url").GetProperty("url").GetString()); Assert.Equal("after", blocks[3].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("text-first")]
    [InlineData("image-first")]
    [InlineData("single-image")]
    [InlineData("empty-first")]
    public async Task PythonAssistantProjectionExplicitlyRetainsOnlyTheFirstSupportedBlock(string kind)
    {
        using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport);
        ContentBlock[] content = kind switch
        {
            "text-first" => [new TextBlock("first"), new ImageBlock("image/png", "AQID"), new TextBlock("discarded")],
            "image-first" => [new ImageBlock("image/png", "AQID"), new TextBlock("discarded")],
            "single-image" => [new ImageBlock("image/png", "AQID")],
            _ => [new TextBlock(""), new TextBlock("discarded")]
        };
        var input = JsonSerializer.SerializeToElement(new { key = 7 });
        var history = new Message(Role.Assistant, [.. content, new ToolUseBlock("correlated", "lookup", input)]);
        await CollectAsync(new LlamaApiModel(client, new("meta", "key") { AssistantContentPolicy = LlamaApiAssistantContentPolicy.PythonFirstBlock }), new([history]));
        var assistant = Assert.Single(Assert.Single(transport.Bodies).GetProperty("messages").EnumerateArray());
        var first = assistant.GetProperty("content");
        if (kind is "image-first" or "single-image") Assert.Equal("data:image/png;base64,AQID", first.GetProperty("image_url").GetProperty("url").GetString());
        else Assert.Equal(kind == "empty-first" ? "" : "first", first.GetProperty("text").GetString());
        var call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray()); Assert.Equal("correlated", call.GetProperty("id").GetString());
        Assert.Equal(7, JsonSerializer.Deserialize<JsonElement>(call.GetProperty("function").GetProperty("arguments").GetString()!).GetProperty("key").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PythonAssistantProjectionValidatesUnsupportedMediaEvenAfterTheSelectedFirstBlock(bool unsupportedFirst)
    {
        var credentials = 0; using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport);
        ContentBlock[] content = unsupportedFirst ? [new AudioBlock("audio/wav", "AQID"), new TextBlock("text")] : [new TextBlock("selected"), new AudioBlock("audio/wav", "AQID")];
        var model = new LlamaApiModel(client, new("meta", _ => { credentials++; return new ValueTask<string>("key"); }) { AssistantContentPolicy = LlamaApiAssistantContentPolicy.PythonFirstBlock });
        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(model, new([new(Role.Assistant, content)])));
        Assert.Equal(0, credentials); Assert.Empty(transport.Bodies);
    }

    [Fact]
    public void AnUnknownAssistantProjectionPolicyIsRejectedBeforeAnySideEffects()
    {
        var credentials = 0; using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport);
        var options = new LlamaApiModelOptions("meta", _ => { credentials++; return new ValueTask<string>("key"); }) { AssistantContentPolicy = (LlamaApiAssistantContentPolicy)999 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlamaApiModel(client, options)); Assert.Equal(0, credentials); Assert.Empty(transport.Bodies);
        var model = new LlamaApiModel(client, options with { AssistantContentPolicy = LlamaApiAssistantContentPolicy.ConcatenatedText });
        Assert.Throws<ArgumentOutOfRangeException>(() => model.UpdateConfig(current => current with { AssistantContentPolicy = (LlamaApiAssistantContentPolicy)999 }));
        Assert.Equal(LlamaApiAssistantContentPolicy.ConcatenatedText, model.GetConfig().AssistantContentPolicy); Assert.Equal(0, credentials);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("reasoning")]
    [InlineData("guard")]
    [InlineData("s3")]
    [InlineData("url")]
    [InlineData("tool-image-url")]
    public async Task UnsupportedContentFailsBeforeCredentialCallbacksOrTransport(string kind)
    {
        ContentBlock block = kind switch
        {
            "audio" => new AudioBlock("audio/wav", "AQID"),
            "reasoning" => new ReasoningBlock("private"),
            "guard" => new GuardContentBlock("selected"),
            "s3" => new ImageBlock("image/png", new S3Location("s3://bucket/key")),
            "url" => new ImageBlock("image/png", new Uri("https://example.test/image.png")),
            _ => new ToolResultBlock("call", "image") { ContentBlocks = [new ImageBlock("image/png", new Uri("https://example.test/image.png"))] }
        };
        var credentials = 0; using var transport = new LlamaApiTestTransport(TextStream()); using var client = new HttpClient(transport);
        var model = new LlamaApiModel(client, new("meta", _ => { credentials++; return new ValueTask<string>("key"); }));
        await Assert.ThrowsAsync<NotSupportedException>(() => CollectAsync(model, new([new(Role.User, [block])])));
        Assert.Equal(0, credentials); Assert.Empty(transport.Bodies);
    }

    [Fact]
    public async Task ReconfigurationDoesNotChangeAnEnteredRequestAndInvalidUpdatesLeaveTheDriverIntact()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new LlamaApiTestTransport(TextStream()) { BeforeResponse = async (_, _) => { entered.TrySetResult(true); await release.Task; } }; using var client = new HttpClient(transport);
        var model = new LlamaApiModel(client, new("old", "old-key")); var request = new ModelRequest([Message.User("input")]);
        var pending = CollectAsync(model, request); Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));
        try { model.UpdateConfig(options => options with { ModelId = "new", ApiKey = "new-key" }); }
        finally { release.TrySetResult(true); }
        await pending; transport.BeforeResponse = null; await CollectAsync(model, request);
        Assert.Equal(new[] { "old", "new" }, transport.Bodies.Select(body => body.GetProperty("model").GetString())); Assert.Equal(new[] { "Bearer old-key", "Bearer new-key" }, transport.Authorizations);
        Assert.Throws<ArgumentException>(() => model.UpdateConfig(options => options with { ModelId = " " })); Assert.Equal("new", model.GetConfig().ModelId);
    }

    internal static async Task<List<ModelStreamEvent>> CollectAsync(IModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        var result = new List<ModelStreamEvent>(); await foreach (var value in model.StreamAsync(request, cancellationToken)) result.Add(value); return result;
    }
    internal static (Message Message, string StopReason) Complete(IEnumerable<ModelStreamEvent> events)
    { var accumulator = new ModelStreamAccumulator(); foreach (var value in events) accumulator.Process(value); return accumulator.Complete(); }
}
