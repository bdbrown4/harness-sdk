using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticMessageCacheHonorsTheFirstExplicitBoundaryAndUsesConfiguredTtl(bool assistantTail)
    {
        using var body = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new() { CacheLastMessage = true, CacheTtl = "5m" });
        var source = new Strands.Agents.Message(Role.User, [new TextBlock("stable"), new Strands.Agents.CachePointBlock("1h"), new TextBlock("stable tail"), new Strands.Agents.CachePointBlock("1h"), new TextBlock("dynamic")]);
        var messages = new List<Strands.Agents.Message>
        {
            new(Role.User, [new TextBlock("earlier"), new Strands.Agents.CachePointBlock("1h")]),
            new(Role.Assistant, [new TextBlock("reply"), new Strands.Agents.CachePointBlock("1h")]), source
        };
        if (assistantTail) messages.Add(Strands.Agents.Message.Assistant("assistant tail"));

        await CollectAsync(model, new(messages) { DynamicTrailingBlocks = 1 });

        var native = Assert.Single(client.Requests).Messages;
        Assert.DoesNotContain(native[0].Content, block => block.CachePoint is not null);
        Assert.DoesNotContain(native[1].Content, block => block.CachePoint is not null);
        var point = Assert.Single(native[2].Content, block => block.CachePoint is not null);
        Assert.Same(point, native[2].Content[1]);
        Assert.Equal("5m", point.CachePoint.Ttl.Value);
        Assert.Equal("dynamic", native[2].Content.Last().Text);
        if (assistantTail) Assert.DoesNotContain(native[3].Content, block => block.CachePoint is not null);
        Assert.Equal(2, source.Content.OfType<Strands.Agents.CachePointBlock>().Count());
        Assert.All(source.Content.OfType<Strands.Agents.CachePointBlock>(), point => Assert.Equal("1h", point.Ttl));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AutomaticCacheKeepsNonPdfDocumentsOutsideTheCachedPrefix(bool leadingDocument, bool dynamic)
    {
        using var body = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new() { CacheLastMessage = true });
        var content = new List<Strands.Agents.ContentBlock>();
        if (!leadingDocument) content.Add(new TextBlock("stable"));
        content.Add(new Strands.Agents.DocumentBlock("text/plain", "aGVsbG8="));
        if (dynamic) content.Add(new TextBlock("dynamic"));

        await CollectAsync(model, new([new(Role.User, content)]) { DynamicTrailingBlocks = dynamic ? 1 : 0 });

        var native = Assert.Single(Assert.Single(client.Requests).Messages).Content;
        if (leadingDocument) Assert.DoesNotContain(native, block => block.CachePoint is not null);
        else
        {
            Assert.NotNull(native[1].CachePoint);
            Assert.NotNull(native[2].Document);
        }
    }

    [Fact]
    public async Task AnEntirelyDynamicUserMessageHasNoManagedCachePoint()
    {
        using var body = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new() { CacheLastMessage = true });

        await CollectAsync(model, new([Strands.Agents.Message.User("dynamic")]) { DynamicTrailingBlocks = 1 });

        Assert.Null(Assert.Single(Assert.Single(Assert.Single(client.Requests).Messages).Content).CachePoint);
    }

    [Fact]
    public async Task DisabledAutomaticCachingPreservesEveryExplicitPointAndItsTtl()
    {
        using var body = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new() { CacheLastMessage = false, CacheTtl = "5m" });

        await CollectAsync(model, new([new(Role.User, [new TextBlock("first"), new Strands.Agents.CachePointBlock("1h"), new TextBlock("second"), new Strands.Agents.CachePointBlock("1h")])]));

        var points = Assert.Single(Assert.Single(client.Requests).Messages).Content.Where(block => block.CachePoint is not null).ToArray();
        Assert.Equal(2, points.Length);
        Assert.All(points, point => Assert.Equal("1h", point.CachePoint.Ttl.Value));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task InvalidDynamicSuffixFailsBeforeCallingTheNativeClient(int count)
    {
        using var client = new StubClient();
        using var model = new BedrockModel(client, new() { CacheLastMessage = true });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CollectAsync(model, new([Strands.Agents.Message.User("one")]) { DynamicTrailingBlocks = count }));

        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task NativeStreamProducesIndexedTextUsageAndDisposesOnlyItsResponse()
    {
        using var body = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"Hi"}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""),
            ("metadata", """{"usage":{"inputTokens":5,"outputTokens":2,"totalTokens":7,"cacheReadInputTokens":3,"cacheWriteInputTokens":4},"metrics":{"latencyMs":1}}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions { ModelId = "test-model", MaxTokens = 17 });

        var events = await CollectAsync(model, new ModelRequest([Strands.Agents.Message.User("hello")], "instructions"));

        Assert.Collection(events,
            item => Assert.IsType<ModelMessageStartEvent>(item),
            item => Assert.Equal(0, Assert.IsType<ModelContentBlockStartEvent>(item).Index),
            item => Assert.Equal("Hi", Assert.IsType<ModelContentBlockDeltaEvent>(item).Text),
            item => Assert.Equal(0, Assert.IsType<ModelContentBlockStopEvent>(item).Index),
            item => Assert.Equal(StopReasons.EndTurn, Assert.IsType<ModelMessageStopEvent>(item).StopReason),
            item => Assert.Equal(new Usage(5, 2, 3, 4, 7), Assert.IsType<ModelMetadataEvent>(item).Usage));
        Assert.Equal("test-model", Assert.Single(client.Requests).ModelId);
        Assert.Equal(17, client.Requests[0].InferenceConfig.MaxTokens);
        Assert.Equal("instructions", Assert.Single(client.Requests[0].System).Text);
        Assert.Equal("hello", Assert.Single(Assert.Single(client.Requests[0].Messages).Content).Text);
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task FragmentedToolRequestCompletesNativeToolLoop()
    {
        using var first = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockStart", """{"contentBlockIndex":0,"start":{"toolUse":{"toolUseId":"call-1","name":"lookup"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"{\"id\":"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"2}"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"tool_use"}"""));
        using var second = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"done"}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(first, second);
        using var model = new BedrockModel(client, new BedrockModelOptions { ModelId = "anthropic.claude-test" });
        var tool = new FunctionTool(new ToolSpec("lookup", "Look up an item", JsonSerializer.SerializeToElement(new { type = "object" })),
            (input, _, _) => new ValueTask<string>("item:" + input.GetProperty("id").GetInt32()));
        var agent = new Agent(new AgentOptions { Model = model, Tools = [tool] });

        var result = await agent.InvokeAsync("Find item 2");

        Assert.Equal("done", result.Message!.Text);
        Assert.Equal(2, result.Turns);
        Assert.Equal("lookup", Assert.Single(client.Requests[0].ToolConfig.Tools).ToolSpec.Name);
        Assert.Equal(2, client.Requests[1].Messages[1].Content[0].ToolUse.Input.AsDictionary()["id"].AsInt());
        var reply = client.Requests[1].Messages[2].Content[0].ToolResult;
        Assert.Equal("call-1", reply.ToolUseId);
        Assert.Equal("item:2", Assert.Single(reply.Content).Text);
        Assert.Equal("success", reply.Status.Value);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task RichHistoryReplaysReasoningMediaJsonAndExplicitCacheBoundaries()
    {
        using var body = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"text":"thinking"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"signature":"sig-"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"signature":"end"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"reasoningContent":{"redactedContent":"AQID"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":1}"""),
            ("messageStop", """{"stopReason":"guardrail_intervened"}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions
        {
            ModelId = "anthropic.claude-test",
            Temperature = 0.25,
            TopP = 0.8,
            AdditionalModelRequestFields = JsonSerializer.SerializeToElement(new { thinking = new { type = "enabled", budget_tokens = 1024 } }),
            CacheSystemPrompt = true,
            CacheTools = true,
            CacheLastMessage = true,
            CacheTtl = "1h"
        });
        var json = JsonSerializer.SerializeToElement(new { camelCaseKey = 3 });
        var request = new ModelRequest([
            new Strands.Agents.Message(Role.Assistant, [new ReasoningBlock("earlier", "original-signature"), new RedactedReasoningBlock("AQID")]),
            new Strands.Agents.Message(Role.User, [new Strands.Agents.ImageBlock("image/jpeg", "AQID"), new Strands.Agents.AudioBlock("audio/wav", "AQID"), new Strands.Agents.DocumentBlock("application/pdf", "AQID", "Report"),
                new Strands.Agents.CachePointBlock("1h"), new Strands.Agents.ToolResultBlock("prior", [new TextBlock("answer"), new JsonBlock(json)], structuredContent: json)])
        ], "instructions", [new ToolSpec("lookup", "Look up", JsonSerializer.SerializeToElement(new { type = "object" }))])
        { ToolChoice = new Strands.Agents.ToolChoice(ToolChoiceMode.Tool, "lookup") };

        var events = await CollectAsync(model, request);

        Assert.Equal("thinking", Assert.Single(events.OfType<ModelContentBlockDeltaEvent>(), item => item.Reasoning is not null).Reasoning);
        Assert.Equal("sig-end", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(item => item.Signature)));
        Assert.Equal("AQID", Assert.IsType<RedactedReasoningBlock>(Assert.Single(events.OfType<ModelContentBlockStopEvent>(), item => item.ContentBlock is RedactedReasoningBlock).ContentBlock).Data);
        Assert.Equal("guardrail_intervened", Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        var native = Assert.Single(client.Requests);
        Assert.Equal("original-signature", native.Messages[0].Content[0].ReasoningContent.ReasoningText.Signature);
        Assert.Equal(new byte[] { 1, 2, 3 }, native.Messages[0].Content[1].ReasoningContent.RedactedContent.ToArray());
        Assert.Equal("jpeg", native.Messages[1].Content[0].Image.Format.Value);
        Assert.Equal("wav", native.Messages[1].Content[1].Audio.Format.Value);
        Assert.Equal("pdf", native.Messages[1].Content[2].Document.Format.Value);
        Assert.Equal("Report", native.Messages[1].Content[2].Document.Name);
        Assert.Equal("1h", native.Messages[1].Content[3].CachePoint.Ttl.Value);
        Assert.Equal(3, native.Messages[1].Content[4].ToolResult.Content[1].Json.AsDictionary()["camelCaseKey"].AsInt());
        Assert.Equal(3, native.Messages[1].Content[4].ToolResult.Content[2].Json.AsDictionary()["camelCaseKey"].AsInt());
        Assert.Equal("1h", native.System[1].CachePoint.Ttl.Value);
        Assert.Equal("1h", native.ToolConfig.Tools[1].CachePoint.Ttl.Value);
        Assert.Single(native.Messages[1].Content, block => block.CachePoint is not null);
        Assert.NotNull(native.Messages[1].Content.Last().ToolResult);
        Assert.Equal("lookup", native.ToolConfig.ToolChoice.Tool.Name);
        Assert.True(native.AdditionalModelRequestFields.IsNull());
        Assert.Equal(1024, model.GetConfig().AdditionalModelRequestFields!.Value.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    [Fact]
    public async Task DisposedModelRejectsRequestsAndRetainsCallerClient()
    {
        using var body = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(body);
        var model = new BedrockModel(client, new BedrockModelOptions());

        model.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => CollectAsync(model, new ModelRequest([Strands.Agents.Message.User("hello")])));
        Assert.Empty(client.Requests);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task StreamedImageChunksBecomeOneLosslessImageBlock()
    {
        using var body = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockStart", """{"contentBlockIndex":0,"start":{"image":{"format":"png"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"image":{"source":{"bytes":"AQI="}}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"image":{"source":{"bytes":"AwQF"}}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        await using var agent = new Agent(new AgentOptions { Model = model });

        var result = await agent.InvokeAsync("Draw an image");

        var image = Assert.IsType<Strands.Agents.ImageBlock>(Assert.Single(result.Message!.Content));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, Convert.FromBase64String(image.Data));
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task InterleavedToolInputsAndConcurrentCompletionKeepAssistantOrder()
    {
        using var first = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockStart", """{"contentBlockIndex":0,"start":{"toolUse":{"toolUseId":"first","name":"lookup"}}}"""),
            ("contentBlockStart", """{"contentBlockIndex":1,"start":{"toolUse":{"toolUseId":"second","name":"lookup"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"toolUse":{"input":"{\"id\":"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"{\"id\":"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"toolUse":{"input":"2}"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"toolUse":{"input":"1}"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":1}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"tool_use"}"""));
        using var second = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"complete"}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new StubClient(first, second);
        using var model = new BedrockModel(client, new BedrockModelOptions { ModelId = "anthropic.claude-test" });
        var secondCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionOrder = new List<string>();
        var tool = new FunctionTool(new ToolSpec("lookup", "Look up an item", JsonSerializer.SerializeToElement(new { type = "object" })), async (input, context, _) =>
        {
            if (context.ToolUse.ToolUseId == "first") await Bounded(secondCompleted.Task, TimeSpan.FromSeconds(5));
            completionOrder.Add(context.ToolUse.ToolUseId);
            if (context.ToolUse.ToolUseId == "second") secondCompleted.TrySetResult(true);
            return "item:" + input.GetProperty("id").GetInt32();
        });
        await using var agent = new Agent(new AgentOptions { Model = model, Tools = [tool] });

        var result = await agent.InvokeAsync("Find both items");

        Assert.Equal("complete", result.Message!.Text);
        Assert.Equal(new[] { "second", "first" }, completionOrder);
        var request = client.Requests[1];
        Assert.Equal(new[] { "first", "second" }, request.Messages[1].Content.Select(block => block.ToolUse.ToolUseId));
        Assert.Equal(new[] { 1, 2 }, request.Messages[1].Content.Select(block => block.ToolUse.Input.AsDictionary()["id"].AsInt()));
        Assert.Equal(new[] { "first", "second" }, request.Messages[2].Content.Select(block => block.ToolResult.ToolUseId));
        Assert.Equal(new[] { "item:1", "item:2" }, request.Messages[2].Content.Select(block => Assert.Single(block.ToolResult.Content).Text));
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.False(client.Disposed);
    }

    internal static async Task<List<ModelStreamEvent>> CollectAsync(IModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(request, cancellationToken)) events.Add(item);
        return events;
    }

    internal static async Task<T> Bounded<T>(Task<T> operation, TimeSpan timeout)
    {
        if (await Task.WhenAny(operation, Task.Delay(timeout)) != operation) throw new TimeoutException("The Bedrock test operation did not finish within its safety deadline.");
        return await operation;
    }

    internal static TrackingStream Wire(params (string Type, string Json)[] events)
    {
        using var data = new MemoryStream();
        foreach (var (type, json) in events)
        {
            var messageType = new EventStreamHeader(":message-type");
            messageType.SetString("event");
            var eventType = new EventStreamHeader(":event-type");
            eventType.SetString(type);
            var contentType = new EventStreamHeader(":content-type");
            contentType.SetString("application/json");
            var bytes = new EventStreamMessage([messageType, eventType, contentType], Encoding.UTF8.GetBytes(json)).ToByteArray();
            data.Write(bytes, 0, bytes.Length);
        }
        return new(data.ToArray());
    }

    internal sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    internal sealed class StubClient(params Stream[] bodies) : AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public List<ConverseStreamRequest> Requests { get; } = [];
        public bool Disposed { get; private set; }
        public Exception? Failure { get; init; }
        public ConverseStreamOutput? ResponseStream { get; init; }
        public override Task<ConverseStreamResponse> ConverseStreamAsync(ConverseStreamRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Failure is { } failure) return Task.FromException<ConverseStreamResponse>(failure);
            return Task.FromResult(new ConverseStreamResponse { Stream = ResponseStream ?? new ConverseStreamOutput(bodies[Requests.Count - 1]) });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
