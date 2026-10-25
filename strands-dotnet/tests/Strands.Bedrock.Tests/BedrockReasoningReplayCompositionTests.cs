using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;
using Reply = Strands.Bedrock.Tests.BedrockRecoveryWireFixture.Reply;

namespace Strands.Bedrock.Tests;

public sealed class BedrockReasoningReplayCompositionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialNativeCountAndConverseUseTheSameFilteredGraph(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming) with { UseNativeTokenCount = true });
        var request = Replay();
        Assert.Equal("summary", await CollectAsync(model, request));
        Assert.Equal(9, await model.CountTokensAsync(request));
        await wire.Completion;
        Assert.Equal(2, wire.Requests.Length);
        var conversation = wire.Requests[0].Body.GetProperty("messages");
        var counted = wire.Requests[1].Body.GetProperty("input").GetProperty("converse").GetProperty("messages");
        Assert.Equal(conversation.GetRawText(), counted.GetRawText());
        Assert.Equal(new[] { "signed" }, ReasoningTexts(conversation));
        Assert.EndsWith("/count-tokens", wire.Requests[1].Target);
        Emit(wire, "same-count-graph");
    }

    [Fact]
    public async Task RetainedEmptySignatureIsAbsentOnTheOfficialCountWireWhenEnabled()
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(false) with { ModelId = "other.model", UseNativeTokenCount = true });
        var block = new ReasoningBlock("", "");
        Assert.Equal(9, await model.CountTokensAsync(new([new(Role.Assistant, [block, new TextBlock("answer")])])));
        await wire.Completion;
        var messages = Assert.Single(wire.Requests).Body.GetProperty("input").GetProperty("converse").GetProperty("messages");
        var reasoning = messages[0].GetProperty("content")[0].GetProperty("reasoningContent").GetProperty("reasoningText");
        Assert.False(reasoning.TryGetProperty("signature", out _));
        Assert.Equal("", block.Signature);
        Emit(wire, "empty-signature-official-wire");
    }

    [Fact]
    public async Task LocalHeuristicRetainsOriginalCountingAndDoesNotSendWithEitherProjection()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(false) with { UseNativeTokenCount = false });
        var request = Replay();
        var filtered = await model.CountTokensAsync(request);
        model.UpdateConfig(options => options with { FilterRejectedReasoningContent = false });
        Assert.Equal(filtered, await model.CountTokensAsync(request));
        Assert.Empty(wire.Requests);
        await wire.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigUpdatesAffectFutureRequestsWithoutChangingAnAdmittedWireGraph(bool streaming)
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
        var first = CollectAsync(model, Replay());
        try
        {
            await Ready(entered.Task);
            model.UpdateConfig(options => options with { ModelId = "meta.llama", FilterRejectedReasoningContent = false });
            release.TrySetResult(true);
            Assert.Equal("summary", await first);
            Assert.Equal("summary", await CollectAsync(model, Replay()));
            await wire.Completion;
            Assert.Equal(2, wire.Requests.Length);
            Assert.Contains("anthropic.claude", wire.Requests[0].Target);
            Assert.Equal(new[] { "signed" }, ReasoningTexts(wire.Requests[0].Body.GetProperty("messages")));
            Assert.Contains("meta.llama", wire.Requests[1].Target);
            Assert.Equal(new[] { "unsigned", "signed" }, ReasoningTexts(wire.Requests[1].Body.GetProperty("messages")));
            Emit(wire, "options-admission");
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
    public async Task NestedRecoveriesReuseTheFilteredOriginalGraphAndCountOnlyAppliesRememberedFormatting(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError),
            new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming) with
        {
            RecoverStaleThinkingBlocks = true,
            RecoverToolResultTurns = true,
            UseNativeTokenCount = true,
            AdditionalModelRequestFields = JsonSerializer.Deserialize<JsonElement>("""{"thinking":{"type":"adaptive"},"kept":"雪"}""")
        });
        var input = JsonSerializer.SerializeToElement(new { lookup = 7 });
        var request = new ModelRequest([
            Message.User("lookup"),
            new(Role.Assistant, [new ReasoningBlock("unsigned"), new ReasoningBlock("signed", "sig"),
                new RedactedReasoningBlock("AAECAw=="), new ToolUseBlock("call-7", "lookup", input)]),
            new(Role.User, [new ToolResultBlock("call-7", "saved")]),
            new(Role.User, [new TextBlock("summarize"), new ImageBlock("image/png", "AQIDBAU=")])]);
        Assert.Equal("summary", await CollectAsync(model, request));
        Assert.Equal(4, wire.Requests.Length);
        Assert.Equal(9, await model.CountTokensAsync(request));
        await wire.Completion;
        Assert.Equal(5, wire.Requests.Length);
        var attempts = wire.Requests.Take(4).Select(row => row.Body).ToArray();
        foreach (var body in attempts)
        {
            Assert.Equal(new[] { "signed" }, ReasoningTexts(body.GetProperty("messages")));
            Assert.Equal("noop", Assert.Single(body.GetProperty("toolConfig").GetProperty("tools").EnumerateArray()).GetProperty("toolSpec").GetProperty("name").GetString());
            var all = body.GetProperty("messages").EnumerateArray().SelectMany(message => message.GetProperty("content").EnumerateArray()).ToArray();
            Assert.Equal("AAECAw==", Assert.Single(all, block => block.TryGetProperty("reasoningContent", out var value) && value.TryGetProperty("redactedContent", out _))
                .GetProperty("reasoningContent").GetProperty("redactedContent").GetString());
            Assert.Equal("AQIDBAU=", Assert.Single(all, block => block.TryGetProperty("image", out _)).GetProperty("image").GetProperty("source").GetProperty("bytes").GetString());
        }
        Assert.Equal(attempts[0].GetProperty("messages").GetRawText(), attempts[1].GetProperty("messages").GetRawText());
        Assert.Equal(attempts[2].GetProperty("messages").GetRawText(), attempts[3].GetProperty("messages").GetRawText());
        Assert.Equal(4, attempts[0].GetProperty("messages").GetArrayLength());
        Assert.Equal(5, attempts[2].GetProperty("messages").GetArrayLength());
        var counted = wire.Requests[4].Body.GetProperty("input").GetProperty("converse");
        Assert.Equal(attempts[3].GetProperty("messages").GetRawText(), counted.GetProperty("messages").GetRawText());
        Assert.Equal("drop_block", counted.GetProperty("additionalModelRequestFields").GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
        Assert.Equal(4, request.Messages.Count);
        Assert.Equal("unsigned", Assert.IsType<ReasoningBlock>(request.Messages[1].Content[0]).Text);
        Assert.False(model.GetConfig().AdditionalModelRequestFields!.Value.GetProperty("thinking").TryGetProperty("block_binding", out _));
        Emit(wire, "binding-turn-binding-original-filtered-graph");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheAndLatestUserGuardOperateOnAcceptedContentWithoutChangingCallerAuthority(bool filtering)
    {
        ContentBlock[] content = [new TextBlock("durable 雪"), new ReasoningBlock("unsigned"), new TextBlock("dynamic")];
        var request = new ModelRequest([new(Role.User, content)]) { DynamicTrailingBlocks = 1 };
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(request, Options(false) with
        {
            FilterRejectedReasoningContent = filtering,
            CacheLastMessage = true,
            CacheTtl = "5m",
            Guardrail = new() { Identifier = "fixture", Version = "1", GuardLatestUserMessage = true }
        });
        var result = Assert.Single(mapped.Messages).Content;
        Assert.Equal("durable 雪", result[0].GuardContent.Text.Text);
        Assert.Equal("dynamic", result.Last().GuardContent.Text.Text);
        Assert.Equal(filtering ? 1 : 2, result.FindIndex(block => block.CachePoint is not null));
        Assert.Equal("5m", Assert.Single(result, block => block.CachePoint is not null).CachePoint.Ttl.Value);
        Assert.Equal(3, content.Length);
        Assert.IsType<ReasoningBlock>(content[1]);
        Assert.Equal("durable 雪", Assert.IsType<TextBlock>(content[0]).Text);
    }

    [Fact]
    public void RemovedDynamicPrefixDoesNotInventCacheableContent()
    {
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(new([new(Role.User, [new ReasoningBlock("unsigned"), new TextBlock("dynamic")])]) { DynamicTrailingBlocks = 1 },
            Options(false) with { CacheLastMessage = true });
        Assert.Equal("dynamic", Assert.Single(Assert.Single(mapped.Messages).Content).Text);
    }

    [Fact]
    public void ExistingCacheOnlyManagementIsNotReplacedByASecondEmptyMessageSanitizer()
    {
        using var mapper = new BedrockRequestMapper();
        var mapped = mapper.Map(new([new(Role.User, [new ReasoningBlock("unsigned"), new CachePointBlock()])]), Options(false) with { CacheLastMessage = true });
        Assert.Empty(Assert.Single(mapped.Messages).Content);
    }

    [Fact]
    public async Task AlreadyCanceledCallerPreservesTokenBeforeMappingOrSending()
    {
        await using var wire = new BedrockRecoveryWireFixture();
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(true));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(model,
            new([new(Role.Assistant, [new RedactedReasoningBlock("invalid base64!?")])]), caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Empty(wire.Requests);
        await wire.Completion;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquiredMalformedResponsesDoNotRetryEvenWhenAllCompatibilityOptionsAreEnabled(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Malformed: true));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming) with { RecoverStaleThinkingBlocks = true, RecoverToolResultTurns = true });
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Replay()));
        await wire.Completion;
        Assert.Single(wire.Requests);
        Assert.Equal(new[] { "signed" }, ReasoningTexts(wire.Requests[0].Body.GetProperty("messages")));
        Emit(wire, "malformed-no-retry");
    }

    [Fact]
    public async Task MidstreamValidationAfterObservedTextNeverRemapsOrRetries()
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
                await foreach (var item in model.StreamAsync(Replay()))
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
    public async Task BorrowedClientRetainsConfigurationAndRemainsUsableAfterFilteredModelDisposal()
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(), new Reply());
        using var client = wire.CreateClient();
        var serviceUrl = client.Config.ServiceURL;
        using (var model = new BedrockModel(client, Options(false))) Assert.Equal("summary", await CollectAsync(model, Replay()));
        var response = await client.ConverseAsync(new Amazon.BedrockRuntime.Model.ConverseRequest
        {
            ModelId = "other-model",
            Messages = [new() { Role = "user", Content = [new() { Text = "direct" }] }]
        });
        Assert.Equal("summary", response.Output.Message.Content[0].Text);
        await wire.Completion;
        Assert.Equal(serviceUrl, client.Config.ServiceURL);
        Assert.Equal(2, wire.Requests.Length);
        Assert.All(wire.Requests, request => Assert.True(request.Signed));
        Emit(wire, "borrowed-client-reuse");
    }

    private static BedrockModelOptions Options(bool streaming) => new()
    {
        ModelId = "global.anthropic.claude-sonnet-4-6",
        Streaming = streaming,
        FilterRejectedReasoningContent = true,
        RequestTimeout = TimeSpan.FromSeconds(15)
    };

    private static ModelRequest Replay() => new([Message.User("hello"), new(Role.Assistant,
        [new ReasoningBlock("unsigned"), new ReasoningBlock("signed", "sig-雪"), new RedactedReasoningBlock("AAECAw=="), new TextBlock("answer")])]);

    private static string?[] ReasoningTexts(JsonElement messages) => messages.EnumerateArray()
        .SelectMany(message => message.GetProperty("content").EnumerateArray())
        .Where(block => block.TryGetProperty("reasoningContent", out var reasoning) && reasoning.TryGetProperty("reasoningText", out _))
        .Select(block => block.GetProperty("reasoningContent").GetProperty("reasoningText").GetProperty("text").GetString()).ToArray();

    private static async Task<string> CollectAsync(IModel model, ModelRequest request, CancellationToken caller = default)
    {
        var result = new StringBuilder();
        await foreach (var item in model.StreamAsync(request, caller))
            if (item is ModelContentBlockDeltaEvent { Text: { } text }) result.Append(text);
        return result.ToString();
    }

    private static async Task Ready(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        await task;
    }

    private void Emit(BedrockRecoveryWireFixture wire, string phase) => output.WriteLine(JsonSerializer.Serialize(new
    {
        Phase = "b03-composition-" + phase,
        Requests = wire.Requests,
        Signed = wire.Requests.All(request => request.Signed)
    }));
}
