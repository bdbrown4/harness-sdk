using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Native = Amazon.BedrockRuntime.Model;

namespace Strands.Bedrock.Tests;

public sealed class BedrockNonStreamingTests
{
    [Fact]
    public async Task ConverseUsesTheOfficialNonStreamingTransportAndTheSameRequestOptions()
    {
        using var client = new Client();
        using var model = new BedrockModel(client, new() { Streaming = false, MaxTokens = 42, ServiceTier = "flex", Guardrail = new() { Identifier = "guard", Version = "1", StreamProcessingMode = "async" }, RequestMetadata = new Dictionary<string, string> { ["trace"] = "value" } });
        var events = await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")], "system"));

        Assert.Equal("answer", Assert.Single(events.OfType<ModelContentBlockDeltaEvent>()).Text);
        var request = Assert.Single(client.ConverseRequests);
        Assert.Equal(0, client.StreamRequests);
        Assert.Equal(42, request.InferenceConfig.MaxTokens);
        Assert.Equal("system", Assert.Single(request.System).Text);
        Assert.Equal("flex", request.ServiceTier.Type.Value);
        Assert.Equal("value", request.RequestMetadata["trace"]);
        Assert.Equal("guard", request.GuardrailConfig.GuardrailIdentifier);
        Assert.Equal("end_turn", Assert.Single(events.OfType<ModelMessageStopEvent>()).StopReason);
        Assert.Equal(3, Assert.Single(events.OfType<ModelMetadataEvent>()).Usage.InputTokens);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task ReasoningAndToolsRetainIndexesSignaturesAndExactJsonNumbers()
    {
        using var opaque = new BedrockModelTests.TrackingStream([1, 2, 3]);
        using var client = new Client
        {
            Response = Response([
                new() { ReasoningContent = new() { ReasoningText = new() { Text = "reason", Signature = "signature" } } },
                new() { ReasoningContent = new() { RedactedContent = opaque } },
                new() { ToolUse = new() { ToolUseId = "call", Name = "lookup", Input = JsonSerializer.Deserialize<Amazon.Runtime.Documents.Document>("{\"id\":9007199254740993,\"list\":[true,null,\"value\"]}") } }
            ], "tool_use")
        };
        using var model = new BedrockModel(client, new() { Streaming = false });
        var events = await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")]));

        Assert.Equal("reason", Assert.Single(events.OfType<ModelContentBlockDeltaEvent>(), item => item.Reasoning is not null).Reasoning);
        Assert.Equal("signature", string.Concat(events.OfType<ModelContentBlockDeltaEvent>().Select(item => item.Signature)));
        var opaqueDelta = Assert.Single(events.OfType<ModelContentBlockDeltaEvent>(), item => item.RedactedReasoningBytes is not null);
        Assert.Equal(1, opaqueDelta.Index);
        Assert.Equal(new byte[] { 1, 2, 3 }, opaqueDelta.RedactedReasoningBytes);
        var opaqueStop = Assert.Single(events.OfType<ModelContentBlockStopEvent>(), item => item.ContentBlock is RedactedReasoningBlock);
        Assert.Equal(1, opaqueStop.Index);
        Assert.Equal("AQID", Assert.IsType<RedactedReasoningBlock>(opaqueStop.ContentBlock).Data);
        var tool = Assert.Single(events.OfType<ModelContentBlockStartEvent>(), item => item.ToolUse is not null);
        Assert.Equal(2, tool.Index);
        Assert.Equal("call", tool.ToolUse!.ToolUseId);
        using var input = JsonDocument.Parse(Assert.Single(events.OfType<ModelContentBlockDeltaEvent>(), item => item.ToolInput is not null).ToolInput!);
        Assert.Equal(9007199254740993L, input.RootElement.GetProperty("id").GetInt64());
        Assert.True(opaque.Disposed);
    }

    [Fact]
    public async Task AmbiguousNativeContentIsRejected()
    {
        using var client = new Client { Response = Response([new() { Text = "ambiguous", ToolUse = new() { ToolUseId = "call", Name = "lookup" } }]) };
        using var model = new BedrockModel(client, new() { Streaming = false });
        await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")])));
    }

    [Fact]
    public async Task CallerCancellationAfterAcceptedResponseKeepsTheOriginalToken()
    {
        using var caller = new CancellationTokenSource();
        using var client = new Client { OnConverse = caller.Cancel };
        using var model = new BedrockModel(client, new() { Streaming = false });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")]), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task NativeThrottlingRemainsTypedWithTheOriginalCause()
    {
        var primary = new ThrottlingException("throttled");
        using var client = new Client { Failure = primary };
        using var model = new BedrockModel(client, new() { Streaming = false });
        var error = await Assert.ThrowsAsync<ModelThrottledException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")])));
        Assert.Same(primary, error.InnerException);
    }

    [Fact]
    public async Task CleanupFailuresRemainTypedAfterOtherwiseSuccessfulContent()
    {
        var cleanup = new IOException("response cleanup");
        using var client = new Client { Response = Response([new() { ReasoningContent = new() { RedactedContent = new ThrowingStream(cleanup) } }]) };
        using var model = new BedrockModel(client, new() { Streaming = false });
        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")])));
        Assert.Same(cleanup, error.InnerException);
    }

    [Fact]
    public async Task InvalidUnionsRetainTheirPrimaryFailureWhenOwnedCleanupAlsoFails()
    {
        var cleanup = new IOException("response cleanup");
        using var client = new Client { Response = Response([new() { Text = "ambiguous", ReasoningContent = new() { RedactedContent = new ThrowingStream(cleanup) } }]) };
        using var model = new BedrockModel(client, new() { Streaming = false });
        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")])));
        Assert.Contains("exactly one", error.Message);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task EarlyEnumerationDisposalClosesUnconsumedResponseMedia()
    {
        using var media = new BedrockModelTests.TrackingStream([1, 2, 3]);
        using var client = new Client { Response = Response([new() { Image = new() { Format = "png", Source = new() { Bytes = media } } }]) };
        using var model = new BedrockModel(client, new() { Streaming = false });
        await using (var iterator = model.StreamAsync(new([Strands.Agents.Message.User("question")])).GetAsyncEnumerator())
        {
            Assert.True(await iterator.MoveNextAsync());
            Assert.IsType<ModelMessageStartEvent>(iterator.Current);
        }
        Assert.True(media.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task BinaryBoundsAndNativeImageProjectionUseTheSharedParser()
    {
        using var image = new BedrockModelTests.TrackingStream([1, 2, 3]);
        using var client = new Client { Response = Response([new() { Image = new() { Format = "png", Source = new() { Bytes = image } } }]) };
        using var model = new BedrockModel(client, new() { Streaming = false, MaximumBufferedContentBytes = 2 });
        await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")])));
        Assert.True(image.Disposed);
    }

    [Fact]
    public async Task CitationMetadataAndGeneratedTextUseTheNormalContentContract()
    {
        using var client = new Client { Response = Response([new() { CitationsContent = new() { Content = [new() { Text = "answer" }], Citations = [new() { Location = new() { DocumentChar = new() { DocumentIndex = 1, Start = 2, End = 7 } }, SourceContent = [new() { Text = "evidence" }], Title = "report", Source = "document" }] } }]) };
        using var model = new BedrockModel(client, new() { Streaming = false });
        var events = await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")]));
        Assert.Equal("answer", Assert.Single(events.OfType<ModelContentBlockDeltaEvent>(), item => item.Text is not null).Text);
        var citation = Assert.Single(Assert.Single(events.OfType<ModelContentBlockDeltaEvent>(), item => item.Citations.Count > 0).Citations);
        Assert.Equal(new DocumentCharCitationLocation(1, 2, 7), citation.Location);
        Assert.Equal("evidence", Assert.Single(citation.SourceContent).Text);
    }

    [Fact]
    public async Task ConfigurationUpdatesSelectTheTransportOnlyForSubsequentRequests()
    {
        using var client = new Client();
        using var model = new BedrockModel(client, new() { Streaming = false });
        await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("first")]));
        model.UpdateConfig(old => old with { Streaming = true });
        await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("second")]));
        Assert.Single(client.ConverseRequests);
        Assert.Equal(1, client.StreamRequests);
    }

    [Fact]
    public async Task RequestDeadlineCancelsTheNativeCallAndRetainsATimeoutCause()
    {
        using var client = new Client { ResponseHandler = async token => { await Task.Delay(Timeout.Infinite, token); return Response([]); } };
        using var model = new BedrockModel(client, new() { Streaming = false, RequestTimeout = TimeSpan.FromMilliseconds(100) });
        var error = await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")])));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Single(client.ConverseRequests);
    }

    [Fact]
    public async Task ModelDisposalCancelsAnEnteredNativeCallAndRetainsTheBorrowedClient()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new Client { ResponseHandler = async token => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return Response([]); } };
        using var model = new BedrockModel(client, new() { Streaming = false, RequestTimeout = null });
        var operation = BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")]));
        await BedrockModelTests.Bounded(entered.Task, TimeSpan.FromSeconds(10));
        model.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BedrockModelTests.Bounded(operation, TimeSpan.FromSeconds(10)));
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task InlineImagesUseTheSameTypedContentAndDisposeTheirOwnedSource()
    {
        using var image = new BedrockModelTests.TrackingStream([1, 2, 3]);
        using var client = new Client { Response = Response([new() { Image = new() { Format = "png", Source = new() { Bytes = image } } }]) };
        using var model = new BedrockModel(client, new() { Streaming = false });
        var events = await BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")]));
        var content = Assert.IsType<Strands.Agents.ImageBlock>(Assert.Single(events.OfType<ModelContentBlockStartEvent>()).ContentBlock);
        Assert.Equal("image/png", content.MediaType);
        Assert.Equal("AQID", content.Data);
        Assert.True(image.Disposed);
    }

    [Theory]
    [InlineData("user")]
    [InlineData(null)]
    public async Task InvalidResponseRolesAreProtocolFailures(string? role)
    {
        var response = Response([new() { Text = "answer" }]);
        response.Output.Message.Role = role;
        using var client = new Client { Response = response };
        using var model = new BedrockModel(client, new() { Streaming = false });
        await Assert.ThrowsAsync<ModelException>(() => BedrockModelTests.CollectAsync(model, new([Strands.Agents.Message.User("question")])));
    }

    [Fact]
    public async Task NativeToolRequestsRunThroughTheRealAgentLoop()
    {
        var calls = 0;
        using var client = new Client
        {
            ResponseHandler = _ => Task.FromResult(++calls == 1
            ? Response([new() { ToolUse = new() { ToolUseId = "call", Name = "lookup", Input = JsonSerializer.Deserialize<Amazon.Runtime.Documents.Document>("{\"id\":7}") } }], "tool_use")
            : Response([new() { Text = "complete" }]))
        };
        using var model = new BedrockModel(client, new() { Streaming = false, ModelId = "anthropic.claude-test" });
        var effects = 0;
        await using var agent = new Agent(new() { Model = model, Tools = [new FunctionTool(new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object", properties = new { id = new { type = "integer" } }, required = new[] { "id" } })), (input, _, _) => { effects++; return new ValueTask<string>("item:" + input.GetProperty("id").GetInt32()); })] });
        var result = await agent.InvokeAsync("question");
        Assert.Equal("complete", result.Message!.Text);
        Assert.Equal(1, effects);
        Assert.Equal("item:7", Assert.Single(client.ConverseRequests[1].Messages.Last().Content).ToolResult.Content[0].Text);
        Assert.Equal(0, client.StreamRequests);
    }

    private sealed class ThrowingStream(Exception error) : MemoryStream([1, 2, 3])
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); throw error; }
    }

    internal static ConverseResponse Response(List<Native.ContentBlock> content, string stop = "end_turn") => new()
    {
        Output = new ConverseOutput { Message = new Native.Message { Role = ConversationRole.Assistant, Content = content } },
        StopReason = stop,
        Usage = new TokenUsage { InputTokens = 3, OutputTokens = 2, TotalTokens = 5 }
    };

    internal sealed class Client() : AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public List<ConverseRequest> ConverseRequests { get; } = [];
        public int StreamRequests { get; private set; }
        public ConverseResponse Response { get; init; } = BedrockNonStreamingTests.Response([new() { Text = "answer" }]);
        public Exception? Failure { get; init; }
        public Action? OnConverse { get; init; }
        public Func<CancellationToken, Task<ConverseResponse>>? ResponseHandler { get; init; }
        public bool Disposed { get; private set; }
        public override Task<ConverseResponse> ConverseAsync(ConverseRequest request, CancellationToken cancellationToken = default)
        {
            ConverseRequests.Add(request);
            OnConverse?.Invoke();
            if (ResponseHandler is { } handler) return handler(cancellationToken);
            return Failure is { } error ? Task.FromException<ConverseResponse>(error) : Task.FromResult(Response);
        }
        public override Task<ConverseStreamResponse> ConverseStreamAsync(ConverseStreamRequest request, CancellationToken cancellationToken = default)
        {
            StreamRequests++;
            return Task.FromResult(new ConverseStreamResponse { Stream = new ConverseStreamOutput(BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"wrong transport"}}"""), ("contentBlockStop", """{"contentBlockIndex":0}"""), ("messageStop", """{"stopReason":"end_turn"}"""))) });
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
