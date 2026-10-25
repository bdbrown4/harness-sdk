using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;
using Reply = Strands.Bedrock.Tests.BedrockRecoveryWireFixture.Reply;

namespace Strands.Bedrock.Tests;

public sealed class BedrockNovaToolResultCompositionTests(ITestOutputHelper output)
{
    private const string Raw = """{ "value" : 9007199254740993, "雪" : "😀" }""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountTokensAndBothConverseModesUseTheSameNormalizedGraph(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming) with { UseNativeTokenCount = true });
        var request = Request();
        Assert.Equal("summary", await Collect(model, request));
        Assert.Equal(9, await model.CountTokensAsync(request));
        await wire.Completion;
        Assert.Equal(2, wire.Requests.Length);
        var conversation = wire.Requests[0].Body.GetProperty("messages");
        var counted = wire.Requests[1].Body.GetProperty("input").GetProperty("converse").GetProperty("messages");
        Assert.Equal(conversation.GetRawText(), counted.GetRawText());
        AssertNormalized(conversation);
        Assert.EndsWith("/count-tokens", wire.Requests[1].Target);
        Emit(wire, "count-and-converse");
    }

    [Fact]
    public async Task LocalHeuristicDoesNotApplyOrSendTheProjection()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(false));
        var count = await model.CountTokensAsync(Request());
        model.UpdateConfig(options => options with { ConvertNovaToolResultJsonToText = false });
        Assert.Equal(count, await model.CountTokensAsync(Request()));
        Assert.Empty(wire.Requests);
        await wire.Completion;
    }

    [Theory]
    [InlineData(false, "disable-option")]
    [InlineData(true, "disable-option")]
    [InlineData(false, "change-family")]
    [InlineData(true, "change-family")]
    [InlineData(false, "case-sensitive-id")]
    [InlineData(true, "case-sensitive-id")]
    public async Task AtomicConfigPublicationCannotChangeAnAdmittedProjection(bool streaming, string change)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply())
        {
            BeforeReply = index =>
            {
                if (index != 0) return Task.CompletedTask;
                entered.TrySetResult(true);
                return release.Task;
            }
        };
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var first = Collect(model, Request());
        try
        {
            await Ready(entered.Task);
            model.UpdateConfig(options => change switch
            {
                "disable-option" => options with { ConvertNovaToolResultJsonToText = false },
                "change-family" => options with { ModelId = "other.model" },
                _ => options with { ModelId = "AMAZON.NOVA-pro-v1:0" }
            });
            release.TrySetResult(true);
            Assert.Equal("summary", await first);
            Assert.Equal("summary", await Collect(model, Request()));
            await wire.Completion;
            Assert.Equal(2, wire.Requests.Length);
            AssertNormalized(wire.Requests[0].Body.GetProperty("messages"));
            var second = ResultBlocks(wire.Requests[1].Body.GetProperty("messages"));
            Assert.All(second, block => Assert.True(block.TryGetProperty("json", out _)));
            Assert.Equal(change != "disable-option", model.GetConfig().ConvertNovaToolResultJsonToText);
            Emit(wire, "admitted-options-" + change);
        }
        finally
        {
            release.TrySetResult(true);
            try { await first; } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedRecoveryReusesOriginalNormalizedGraphWithReasoningCacheAndGuardPolicies(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError),
            new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming) with
        {
            RecoverStaleThinkingBlocks = true,
            RecoverToolResultTurns = true,
            FilterRejectedReasoningContent = true,
            UseNativeTokenCount = true,
            CacheLastMessage = true,
            Guardrail = new() { Identifier = "fixture-guard", Version = "1", GuardLatestUserMessage = true },
            AdditionalModelRequestFields = JsonSerializer.Deserialize<JsonElement>("""{"thinking":{"type":"adaptive"},"kept":"雪"}""")
        });
        var basic = Request();
        var request = basic with
        {
            Messages = [Message.User("lookup"),
            new(Role.Assistant, [new ReasoningBlock("drop for Nova"), new RedactedReasoningBlock("AAECAw=="),
                new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { input = 7 }))]),
            basic.Messages[0], new(Role.User, [new TextBlock("summarize"), new ImageBlock("image/png", "AQID")])]
        };
        Assert.Equal("summary", await Collect(model, request));
        Assert.Equal(4, wire.Requests.Length);
        Assert.Equal(9, await model.CountTokensAsync(request));
        await wire.Completion;
        var attempts = wire.Requests.Take(4).Select(row => row.Body).ToArray();
        Assert.Equal(5, wire.Requests.Length);
        foreach (var body in attempts)
        {
            var messages = body.GetProperty("messages");
            AssertNormalized(messages);
            var all = messages.EnumerateArray().SelectMany(message => message.GetProperty("content").EnumerateArray()).ToArray();
            Assert.DoesNotContain(all, block => block.TryGetProperty("reasoningContent", out var reasoning) && reasoning.TryGetProperty("reasoningText", out _));
            Assert.Equal("AAECAw==", Assert.Single(all, block => block.TryGetProperty("reasoningContent", out var reasoning) && reasoning.TryGetProperty("redactedContent", out _))
                .GetProperty("reasoningContent").GetProperty("redactedContent").GetString());
            Assert.Contains(all, block => block.TryGetProperty("guardContent", out _));
            Assert.Contains(all, block => block.TryGetProperty("cachePoint", out _));
            Assert.Equal("noop", Assert.Single(body.GetProperty("toolConfig").GetProperty("tools").EnumerateArray()).GetProperty("toolSpec").GetProperty("name").GetString());
        }
        Assert.Equal(attempts[0].GetProperty("messages").GetRawText(), attempts[1].GetProperty("messages").GetRawText());
        Assert.Equal(attempts[2].GetProperty("messages").GetRawText(), attempts[3].GetProperty("messages").GetRawText());
        Assert.Equal(4, attempts[0].GetProperty("messages").GetArrayLength());
        Assert.Equal(5, attempts[2].GetProperty("messages").GetArrayLength());
        var counted = wire.Requests[4].Body.GetProperty("input").GetProperty("converse");
        Assert.Equal(attempts[3].GetProperty("messages").GetRawText(), counted.GetProperty("messages").GetRawText());
        Assert.Equal(Raw, Assert.IsType<JsonBlock>(Assert.IsType<ToolResultBlock>(basic.Messages[0].Content[0]).ContentBlocks[0]).Json.GetRawText());
        Assert.False(model.GetConfig().AdditionalModelRequestFields!.Value.GetProperty("thinking").TryGetProperty("block_binding", out _));
        Emit(wire, "original-binding-turn-binding-graph");
    }

    [Fact]
    public async Task AlreadyCanceledCallerRetainsItsTokenAndSendsNothing()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(false));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(model, Request(), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Empty(wire.Requests);
        await wire.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquiredMalformedResponseDoesNotBecomeAProjectionRetry(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Malformed: true));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming) with { RecoverStaleThinkingBlocks = true, RecoverToolResultTurns = true });
        await Assert.ThrowsAsync<ModelException>(() => Collect(model, Request()));
        await wire.Completion;
        Assert.Single(wire.Requests);
        AssertNormalized(wire.Requests[0].Body.GetProperty("messages"));
        Emit(wire, "malformed-no-retry");
    }

    [Fact]
    public async Task MidstreamValidationAfterObservedPartialTextNeverRetries()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(StreamFault: true)) { BeforeStreamFault = observed.Task };
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(true) with { RecoverStaleThinkingBlocks = true, RecoverToolResultTurns = true });
        var seen = new StringBuilder();
        try
        {
            await Assert.ThrowsAsync<ModelException>(async () =>
            {
                await foreach (var item in model.StreamAsync(Request()))
                    if (item is ModelContentBlockDeltaEvent { Text: { } text }) { seen.Append(text); observed.TrySetResult(true); }
            });
        }
        finally { observed.TrySetResult(true); }
        Assert.Equal("partial", seen.ToString());
        await wire.Completion;
        Assert.Single(wire.Requests);
        Emit(wire, "partial-no-retry");
    }

    [Fact]
    public async Task BorrowedClientRemainsUnchangedAndUsableAfterModelDisposal()
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply());
        using var client = wire.CreateClient();
        var url = client.Config.ServiceURL;
        var timeout = client.Config.Timeout;
        using (var model = new BedrockModel(client, Options(false))) Assert.Equal("summary", await Collect(model, Request()));
        var response = await client.ConverseAsync(new Amazon.BedrockRuntime.Model.ConverseRequest
        {
            ModelId = "other-model",
            Messages = [new() { Role = "user", Content = [new() { Text = "direct" }] }]
        });
        Assert.Equal("summary", response.Output.Message.Content[0].Text);
        await wire.Completion;
        Assert.Equal(url, client.Config.ServiceURL);
        Assert.Equal(timeout, client.Config.Timeout);
        Assert.Equal(2, wire.Requests.Length);
        Emit(wire, "borrowed-reuse");
    }

    [Fact]
    public async Task StrictNativeCountDoesNotAcquireRecoveryRetryBehavior()
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(false) with
        {
            UseNativeTokenCount = true,
            TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw,
            RecoverStaleThinkingBlocks = true,
            RecoverToolResultTurns = true,
            AdditionalModelRequestFields = JsonSerializer.Deserialize<JsonElement>("""{"thinking":{"type":"adaptive"}}""")
        });
        var error = await Assert.ThrowsAsync<ModelException>(async () => await model.CountTokensAsync(Request()));
        Assert.IsType<Amazon.BedrockRuntime.Model.ValidationException>(error.InnerException);
        await wire.Completion;
        var sent = Assert.Single(wire.Requests);
        AssertNormalized(sent.Body.GetProperty("input").GetProperty("converse").GetProperty("messages"));
        Emit(wire, "count-format-only-no-retry");
    }

    private static BedrockModelOptions Options(bool streaming) => new()
    {
        ModelId = "us.amazon.nova-pro-v1:0",
        Streaming = streaming,
        ConvertNovaToolResultJsonToText = true,
        RequestTimeout = TimeSpan.FromSeconds(15)
    };

    private static ModelRequest Request()
    {
        using var json = JsonDocument.Parse(Raw);
        return new([new(Role.User, [new ToolResultBlock("call", [new JsonBlock(json.RootElement)], structuredContent: json.RootElement)])]);
    }

    private static JsonElement[] ResultBlocks(JsonElement messages) => messages.EnumerateArray()
        .SelectMany(message => message.GetProperty("content").EnumerateArray())
        .Where(block => block.TryGetProperty("toolResult", out _))
        .SelectMany(block => block.GetProperty("toolResult").GetProperty("content").EnumerateArray()).ToArray();

    private static void AssertNormalized(JsonElement messages)
    {
        var blocks = ResultBlocks(messages);
        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block =>
        {
            Assert.False(block.TryGetProperty("json", out _));
            Assert.Equal(Raw, block.GetProperty("text").GetString());
        });
    }

    private static async Task<string> Collect(IModel model, ModelRequest request, CancellationToken caller = default)
    {
        var text = new StringBuilder();
        await foreach (var item in model.StreamAsync(request, caller))
            if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
        return text.ToString();
    }

    private static async Task Ready(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        await task;
    }

    private void Emit(BedrockRecoveryWireFixture wire, string phase) => output.WriteLine(JsonSerializer.Serialize(new
    {
        Phase = "b04-composition-" + phase,
        Requests = wire.Requests,
        Signed = wire.Requests.All(request => request.Signed)
    }));
}
