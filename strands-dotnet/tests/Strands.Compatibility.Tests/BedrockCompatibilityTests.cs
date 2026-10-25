using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Message = Strands.Agents.Message;

namespace Strands.Compatibility.Tests;

public sealed class BedrockCompatibilityTests
{
    [Fact]
    public async Task AutomaticCachePrecedesEphemeralTrailingContentInTheNativeRequest()
    {
        using var body = Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new FixtureClient(body);
        using var model = new BedrockModel(client, new() { CacheLastMessage = true, CacheTtl = "1h" });
        var message = new Message(Role.User, [new TextBlock("stable"), new TextBlock("dynamic")]);

        await foreach (var _ in model.StreamAsync(new ModelRequest([message]) { DynamicTrailingBlocks = 1 })) { }

        var content = Assert.Single(Assert.Single(client.Requests).Messages).Content;
        Assert.Equal(3, content.Count);
        Assert.Equal("stable", content[0].Text);
        Assert.NotNull(content[1].CachePoint);
        Assert.Equal("1h", content[1].CachePoint.Ttl.Value);
        Assert.Equal("dynamic", content[2].Text);
        Assert.Equal(2, message.Content.Count);
    }

    [Fact]
    public async Task NativeEventDecoderCompletesToolsAndReplaysSignedReasoning()
    {
        using var first = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"text":"thought"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"reasoningContent":{"signature":"signed"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("contentBlockStart", """{"contentBlockIndex":1,"start":{"toolUse":{"toolUseId":"call","name":"lookup"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"toolUse":{"input":"{\"id\":2}"}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":1}"""),
            ("messageStop", """{"stopReason":"tool_use"}"""));
        using var second = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"Café ☕"}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""),
            ("metadata", """{"usage":{"inputTokens":4,"outputTokens":2,"totalTokens":6,"cacheReadInputTokens":3,"cacheWriteInputTokens":1}}"""));
        using var client = new FixtureClient(first, second);
        using var model = new BedrockModel(client, new BedrockModelOptions { ModelId = "anthropic.claude-test", CacheSystemPrompt = true });
        var tool = new FunctionTool(new ToolSpec("lookup", "Look up an item", JsonSerializer.SerializeToElement(new { type = "object" })),
            (input, _, _) => new ValueTask<string>("item:" + input.GetProperty("id").GetInt32()));
        var agent = new Agent(new AgentOptions { Model = model, Tools = new[] { tool }, SystemPrompt = "instructions" });

        var result = await WithDeadline(agent.InvokeAsync("Find item 2"));

        Assert.Equal("Café ☕", result.Message!.Text);
        Assert.Equal(2, result.Turns);
        Assert.Equal(new Usage(4, 2, 3, 1, 6), result.Usage);
        Assert.Equal("signed", client.Requests[1].Messages[1].Content[0].ReasoningContent.ReasoningText.Signature);
        Assert.Equal(2, client.Requests[1].Messages[1].Content[1].ToolUse.Input.AsDictionary()["id"].AsInt());
        Assert.Equal("item:2", client.Requests[1].Messages[2].Content[0].ToolResult.Content[0].Text);
        Assert.NotNull(client.Requests[0].System[1].CachePoint);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        model.Dispose();
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task CancellationDisposesTheNativeStreamAndRetainsCallerClient()
    {
        using var body = new BlockingBody();
        using var client = new FixtureClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        using var cancellation = new CancellationTokenSource();
        var operation = CollectAsync(model, cancellation.Token);
        await WithDeadline(body.ReadStarted.Task);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithDeadline(operation));
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task NativeImageChunksRemainLosslessThroughTheStandardAsset()
    {
        using var body = Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockStart", """{"contentBlockIndex":0,"start":{"image":{"format":"png"}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"image":{"source":{"bytes":"AQI="}}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"image":{"source":{"bytes":"AwQF"}}}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new FixtureClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());
        await using var agent = new Agent(new AgentOptions { Model = model });

        var result = await WithDeadline(agent.InvokeAsync("Draw an image"));

        var image = Assert.IsType<Strands.Agents.ImageBlock>(Assert.Single(result.Message!.Content));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, Convert.FromBase64String(image.Data));
        Assert.True(body.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task NativeMultipleToolCallsPreserveAssistantOrderAfterConcurrentCompletion()
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
        using var client = new FixtureClient(first, second);
        using var model = new BedrockModel(client, new BedrockModelOptions { ModelId = "anthropic.claude-test" });
        var secondCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionOrder = new List<string>();
        var tool = new FunctionTool(new ToolSpec("lookup", "Look up an item", JsonSerializer.SerializeToElement(new { type = "object" })), async (input, context, _) =>
        {
            if (context.ToolUse.ToolUseId == "first") await WithDeadline(secondCompleted.Task);
            completionOrder.Add(context.ToolUse.ToolUseId);
            if (context.ToolUse.ToolUseId == "second") secondCompleted.TrySetResult(true);
            return "item:" + input.GetProperty("id").GetInt32();
        });
        await using var agent = new Agent(new AgentOptions { Model = model, Tools = new[] { tool } });

        var result = await WithDeadline(agent.InvokeAsync("Find both items"));

        Assert.Equal("complete", result.Message!.Text);
        Assert.Equal(new[] { "second", "first" }, completionOrder);
        Assert.Equal(new[] { "first", "second" }, client.Requests[1].Messages[1].Content.Select(block => block.ToolUse.ToolUseId));
        Assert.Equal(new[] { 1, 2 }, client.Requests[1].Messages[1].Content.Select(block => block.ToolUse.Input.AsDictionary()["id"].AsInt()));
        Assert.Equal(new[] { "first", "second" }, client.Requests[1].Messages[2].Content.Select(block => block.ToolResult.ToolUseId));
        Assert.Equal(new[] { "item:1", "item:2" }, client.Requests[1].Messages[2].Content.Select(block => Assert.Single(block.ToolResult.Content).Text));
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task NativeProtocolFailureRetainsTheOriginalErrorWhenBodyDisposalFails()
    {
        using var wire = Wire(("messageStart", """{"role":"user"}"""));
        var cleanup = new IOException("body cleanup failed");
        using var body = new CleanupFailureStream(wire.ToArray(), cleanup);
        using var client = new FixtureClient(body);
        using var model = new BedrockModel(client, new BedrockModelOptions());

        var error = await Assert.ThrowsAsync<ModelException>(() => WithDeadline(CollectAsync(model, CancellationToken.None)));

        Assert.Contains("role", error.Message);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.True(body.Disposed);
        Assert.Equal(1, body.DisposeCalls);
        Assert.False(client.Disposed);
    }

    private static async Task<List<ModelStreamEvent>> CollectAsync(IModel model, CancellationToken cancellationToken)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in model.StreamAsync(new ModelRequest(new[] { Message.User("hello") }), cancellationToken)) result.Add(item);
        return result;
    }

    private static async Task<T> WithDeadline<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task) throw new TimeoutException("Bedrock fixture operation did not complete.");
        return await task;
    }

    private static TrackingStream Wire(params (string Type, string Json)[] events)
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
            var bytes = new EventStreamMessage(new List<IEventStreamHeader> { messageType, eventType, contentType }, Encoding.UTF8.GetBytes(json)).ToByteArray();
            data.Write(bytes, 0, bytes.Length);
        }
        return new(data.ToArray());
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class CleanupFailureStream(byte[] bytes, Exception cleanup) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public int DisposeCalls { get; private set; }
        protected override void Dispose(bool disposing)
        {
            DisposeCalls++;
            Disposed = true;
            base.Dispose(disposing);
            if (DisposeCalls == 1) throw cleanup;
        }
    }

    private sealed class FixtureClient(params Stream[] bodies) : AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public List<ConverseStreamRequest> Requests { get; } = new();
        public bool Disposed { get; private set; }
        public override Task<ConverseStreamResponse> ConverseStreamAsync(ConverseStreamRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConverseStreamResponse { Stream = new ConverseStreamOutput(bodies[Requests.Count - 1]) });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BlockingBody : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { ReadStarted.TrySetResult(true); return _read.Task; }
        protected override void Dispose(bool disposing) { Disposed = true; _read.TrySetException(new ObjectDisposedException(nameof(BlockingBody))); base.Dispose(disposing); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
