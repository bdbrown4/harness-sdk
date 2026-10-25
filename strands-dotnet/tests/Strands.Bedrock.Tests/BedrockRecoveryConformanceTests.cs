using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;
using Message = Strands.Agents.Message;
using Reply = Strands.Bedrock.Tests.BedrockRecoveryWireFixture.Reply;

namespace Strands.Bedrock.Tests;

public sealed class BedrockRecoveryConformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void RecoveryIsExplicitAndOldJsonKeepsBothFlagsDisabled()
    {
        var options = JsonSerializer.Deserialize<BedrockModelOptions>("{}");
        Assert.NotNull(options);
        Assert.False(options.RecoverStaleThinkingBlocks);
        Assert.False(options.RecoverToolResultTurns);
        using var model = new BedrockModel(options);
        Assert.False(model.GetConfig().RecoverStaleThinkingBlocks);
        model.UpdateConfig(current => current with { RecoverStaleThinkingBlocks = true, RecoverToolResultTurns = true });
        Assert.True(model.GetConfig().RecoverStaleThinkingBlocks);
        Assert.True(model.GetConfig().RecoverToolResultTurns);
        Assert.False(options.RecoverStaleThinkingBlocks);
    }

    [Theory]
    [InlineData(false, "flag")]
    [InlineData(true, "flag")]
    [InlineData(false, "type")]
    [InlineData(true, "type")]
    [InlineData(false, "code")]
    [InlineData(true, "code")]
    [InlineData(false, "case")]
    [InlineData(true, "case")]
    [InlineData(false, "message")]
    [InlineData(true, "message")]
    public async Task StrictThinkingPredicatesPreserveTheOriginalBorrowedSdkCause(bool streaming, string rejected)
    {
        Exception original = rejected == "type"
            ? new AmazonBedrockRuntimeException(BedrockRecoveryWireFixture.BindingError) { ErrorCode = "ValidationException" }
            : new ValidationException(rejected == "case" ? BedrockRecoveryWireFixture.BindingError.ToUpperInvariant()
                : rejected == "message" ? "unrelated validation" : BedrockRecoveryWireFixture.BindingError)
            { ErrorCode = rejected == "code" ? "validationException" : "ValidationException" };
        using var client = new FailureClient(original);
        using var model = new BedrockModel(client, Options(streaming) with { RecoverStaleThinkingBlocks = rejected != "flag" });
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Plain()));
        Assert.Same(original, error.InnerException);
        Assert.Equal(1, client.Calls);
        Assert.Equal(streaming, client.Streaming);
        Assert.False(client.Disposed);
    }

    [Theory]
    [InlineData(false, "flag")]
    [InlineData(true, "flag")]
    [InlineData(false, "type")]
    [InlineData(true, "type")]
    [InlineData(false, "code")]
    [InlineData(true, "code")]
    [InlineData(false, "case")]
    [InlineData(true, "case")]
    [InlineData(false, "message")]
    [InlineData(true, "message")]
    public async Task StrictTurnPredicatesPreserveTheOriginalBorrowedSdkCause(bool streaming, string rejected)
    {
        Exception original = rejected == "type"
            ? new AmazonBedrockRuntimeException(BedrockRecoveryWireFixture.TurnError) { ErrorCode = "ValidationException" }
            : new ValidationException(rejected == "case" ? BedrockRecoveryWireFixture.TurnError.ToLowerInvariant()
                : rejected == "message" ? "Conversation blocks and tool result blocks rejected differently." : BedrockRecoveryWireFixture.TurnError)
            { ErrorCode = rejected == "code" ? "validationException" : "ValidationException" };
        using var client = new FailureClient(original);
        using var model = new BedrockModel(client, Options(streaming) with { RecoverToolResultTurns = rejected != "flag" });
        var error = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Turns()));
        Assert.Same(original, error.InnerException);
        Assert.Equal(1, client.Calls);
        Assert.False(client.Disposed);
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"thinking\":null}")]
    [InlineData(true, "{\"thinking\":null}")]
    [InlineData(false, "{\"thinking\":\"adaptive\"}")]
    [InlineData(true, "{\"thinking\":\"adaptive\"}")]
    [InlineData(false, "{\"thinking\":{\"block_binding\":null}}")]
    [InlineData(true, "{\"thinking\":{\"block_binding\":null}}")]
    [InlineData(false, "{\"thinking\":{\"block_binding\":{\"prefix_mismatch_behavior\":\"keep\"}}}")]
    [InlineData(true, "{\"thinking\":{\"block_binding\":{\"prefix_mismatch_behavior\":\"keep\"}}}")]
    [InlineData(false, "{\"thinking\":{\"block_binding\":false}}")]
    [InlineData(true, "{\"thinking\":{\"block_binding\":false}}")]
    public async Task MissingThinkingOrExplicitBlockBindingNeverRewritesOrRetries(bool streaming, string json)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError));
        using var client = wire.CreateClient();
        var fields = JsonSerializer.Deserialize<JsonElement>(json);
        using var model = new BedrockModel(client, Options(streaming) with { AdditionalModelRequestFields = fields });
        var failure = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Plain()));
        Assert.IsType<ValidationException>(failure.InnerException);
        await wire.Completion;
        Assert.Single(wire.Requests);
        Assert.Equal(fields.GetRawText(), model.GetConfig().AdditionalModelRequestFields!.Value.GetRawText());
        Emit(wire, "thinking-authority");
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "string")]
    [InlineData(true, "string")]
    [InlineData(false, "present")]
    [InlineData(true, "present")]
    [InlineData(false, "mixed")]
    [InlineData(true, "mixed")]
    public async Task RetryDetachesThinkingAndRetainsCallerFieldsWithSourceBetaArrayRules(bool streaming, string beta)
    {
        var betaField = beta switch
        {
            "missing" => "",
            "string" => ",\"anthropic_beta\":\"caller-string\"",
            "present" => ",\"anthropic_beta\":[\"thinking-binding-controls-2026-08-01\",\"other\"]",
            _ => ",\"anthropic_beta\":[7,\"other\",null]"
        };
        var json = "{\"thinking\":{\"type\":\"adaptive\",\"budget_tokens\":2048},\"kept\":{\"雪\":true}" + betaField + "}";
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply());
        using var client = wire.CreateClient();
        var fields = JsonSerializer.Deserialize<JsonElement>(json);
        using var model = new BedrockModel(client, Options(streaming) with
        {
            AdditionalModelRequestFields = fields,
            RequestMetadata = new Dictionary<string, string> { ["kept"] = "雪" },
            AdditionalModelResponseFieldPaths = ["/kept"],
            Temperature = .25,
            MaxTokens = 31
        });
        Assert.Equal("summary", await CollectAsync(model, Plain()));
        await wire.Completion;
        var first = wire.Requests[0].Body;
        var retry = wire.Requests[1].Body;
        var adjusted = retry.GetProperty("additionalModelRequestFields");
        Assert.Equal("drop_block", adjusted.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
        Assert.Equal(2048, adjusted.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.True(adjusted.GetProperty("kept").GetProperty("雪").GetBoolean());
        var betas = adjusted.GetProperty("anthropic_beta").EnumerateArray().ToArray();
        Assert.Single(betas, value => value.ValueKind == JsonValueKind.String && value.GetString() == "thinking-binding-controls-2026-08-01");
        Assert.Equal(beta == "present" ? 2 : beta == "mixed" ? 4 : 1, betas.Length);
        foreach (var property in first.EnumerateObject().Where(value => value.Name != "additionalModelRequestFields"))
            Assert.Equal(property.Value.GetRawText(), retry.GetProperty(property.Name).GetRawText());
        Assert.Equal(json, fields.GetRawText());
        Assert.Equal(json, model.GetConfig().AdditionalModelRequestFields!.Value.GetRawText());
        Emit(wire, "thinking-detached");
    }

    [Theory]
    [InlineData(false, "prior-mixed")]
    [InlineData(true, "prior-mixed")]
    [InlineData(false, "prior-cache")]
    [InlineData(true, "prior-cache")]
    [InlineData(false, "current-result")]
    [InlineData(true, "current-result")]
    [InlineData(false, "current-mixed")]
    [InlineData(true, "current-mixed")]
    [InlineData(false, "prior-assistant")]
    [InlineData(true, "prior-assistant")]
    [InlineData(false, "current-assistant")]
    [InlineData(true, "current-assistant")]
    [InlineData(false, "prior-empty")]
    [InlineData(true, "prior-empty")]
    [InlineData(false, "current-empty")]
    [InlineData(true, "current-empty")]
    public async Task IneligibleMappedPairsPreserveCacheAndMixedBoundariesWithoutRetry(bool streaming, string shape)
    {
        var messages = Turns().Messages.ToList();
        var result = new Strands.Agents.ToolResultBlock("history-7", "saved");
        messages[2] = shape switch
        {
            "prior-mixed" => new(Role.User, [result, new TextBlock("annotation")]),
            "prior-cache" => new(Role.User, [result, new Strands.Agents.CachePointBlock("1h")]),
            "prior-assistant" => new(Role.Assistant, [result]),
            "prior-empty" => new(Role.User, []),
            _ => messages[2]
        };
        messages[3] = shape switch
        {
            "current-result" => new(Role.User, [new Strands.Agents.ToolResultBlock("history-8", "more")]),
            "current-mixed" => new(Role.User, [new TextBlock("next"), new Strands.Agents.ToolResultBlock("history-8", "more")]),
            "current-assistant" => Message.Assistant("next"),
            _ => messages[3]
        };
        if (shape == "current-empty") messages[3] = new(Role.User, []);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.TurnError));
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var failure = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, new(messages)));
        Assert.IsType<ValidationException>(failure.InnerException);
        await wire.Completion;
        Assert.Single(wire.Requests);
        Assert.Equal(4, wire.Requests[0].Body.GetProperty("messages").GetArrayLength());
        Assert.Equal(4, messages.Count);
        Emit(wire, "turn-ineligible");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryEligibleAdjacentPairGetsOneAcknowledgementWithoutHistoryOrCacheMutation(bool streaming)
    {
        var messages = Turns().Messages.ToList();
        messages.Add(new(Role.Assistant, [new Strands.Agents.ToolUseBlock("history-8", "second", JsonSerializer.SerializeToElement(new { }))]));
        messages.Add(new(Role.User, [new Strands.Agents.ToolResultBlock("history-8", "second saved")]));
        messages.Add(new(Role.User, [new TextBlock("next"), new Strands.Agents.CachePointBlock("1h")]));
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        Assert.Equal("summary", await CollectAsync(model, new(messages)));
        await wire.Completion;
        var initial = wire.Requests[0].Body.GetProperty("messages");
        var retry = wire.Requests[1].Body.GetProperty("messages");
        Assert.Equal(7, initial.GetArrayLength()); Assert.Equal(9, retry.GetArrayLength());
        Assert.Equal(new[] { 3, 7 }, retry.EnumerateArray().Select((message, index) => (message, index)).Where(value => IsAcknowledgement(value.message)).Select(value => value.index));
        Assert.Equal(initial[6].GetRawText(), retry[8].GetRawText());
        Assert.Single(messages[6].Content.OfType<Strands.Agents.CachePointBlock>());
        Assert.Equal(7, messages.Count);
        Emit(wire, "turn-multiple");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedRecoveryUsesOriginalAdmittedGraphForFourAttemptsThenBothStickyFormats(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError),
            new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var input = Turns();
        Assert.Equal("summary", await CollectAsync(model, input));
        Assert.Equal("summary", await CollectAsync(model, input));
        await wire.Completion;
        var bodies = wire.Requests.Select(value => value.Body).ToArray();
        Assert.Equal(new[] { false, true, false, true, true }, bodies.Select(HasDropBlock));
        Assert.Equal(new[] { 4, 4, 5, 5, 5 }, bodies.Select(value => value.GetProperty("messages").GetArrayLength()));
        Assert.Equal(bodies[0].GetProperty("messages").GetRawText(), bodies[1].GetProperty("messages").GetRawText());
        Assert.Equal(bodies[2].GetProperty("messages").GetRawText(), bodies[3].GetProperty("messages").GetRawText());
        Assert.Equal(4, input.Messages.Count);
        Emit(wire, "nested-original-graph");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedRetryLearnsThinkingBeforeSendButNeverLearnsTurnFormatting(bool streaming, bool turn)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: turn ? BedrockRecoveryWireFixture.TurnError : BedrockRecoveryWireFixture.BindingError),
            new Reply(Error: "retry rejected"), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var request = turn ? Turns() : Plain();
        var failure = await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, request));
        Assert.Equal("retry rejected", Assert.IsType<ValidationException>(failure.InnerException).Message);
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        var future = wire.Requests[2].Body;
        Assert.Equal(!turn, HasDropBlock(future));
        Assert.Equal(turn ? 4 : 1, future.GetProperty("messages").GetArrayLength());
        Emit(wire, "failed-learning");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RememberedRecoveryIsExactModelIdAndFutureOptionFlagsRemainAuthoritative(bool streaming, bool turn)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: turn ? BedrockRecoveryWireFixture.TurnError : BedrockRecoveryWireFixture.BindingError), new Reply(), new Reply(), new Reply(), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var request = turn ? Turns() : Plain();
        Assert.Equal("summary", await CollectAsync(model, request));
        model.UpdateConfig(value => value with { ModelId = "fixture-OTHER" });
        Assert.Equal("summary", await CollectAsync(model, request));
        model.UpdateConfig(value => value with { ModelId = "fixture-model", RecoverStaleThinkingBlocks = false, RecoverToolResultTurns = false });
        Assert.Equal("summary", await CollectAsync(model, request));
        model.UpdateConfig(value => value with { RecoverStaleThinkingBlocks = true, RecoverToolResultTurns = true });
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        if (!turn) Assert.Equal(new[] { false, true, false, false, true }, wire.Requests.Select(value => HasDropBlock(value.Body)));
        else Assert.Equal(new[] { 4, 5, 4, 4, 5 }, wire.Requests.Select(value => value.Body.GetProperty("messages").GetArrayLength()));
        Assert.Contains("fixture-OTHER", wire.Requests[2].Target, StringComparison.Ordinal);
        Emit(wire, "model-id-authority");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountTokensUsesAcceptedFormatsButDoesNotPerformRecoveryOrLearnFromItsErrors(bool turn)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: turn ? BedrockRecoveryWireFixture.TurnError : BedrockRecoveryWireFixture.BindingError),
            new Reply(), new Reply(), new Reply(Error: turn ? BedrockRecoveryWireFixture.TurnError : BedrockRecoveryWireFixture.BindingError), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(false) with { UseNativeTokenCount = true, TokenCountFailurePolicy = NativeTokenCountFailurePolicy.Throw });
        var request = turn ? Turns() : Plain();
        Assert.Equal("summary", await CollectAsync(model, request));
        Assert.Equal(9, await model.CountTokensAsync(request));
        model.UpdateConfig(value => value with { ModelId = "fresh-model" });
        var failure = await Assert.ThrowsAsync<ModelException>(async () => await model.CountTokensAsync(request));
        Assert.IsType<ValidationException>(failure.InnerException);
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        var accepted = wire.Requests[2];
        Assert.EndsWith("count-tokens", accepted.Target, StringComparison.Ordinal);
        var converse = accepted.Body.GetProperty("input").GetProperty("converse");
        Assert.Equal(!turn, HasDropBlock(converse));
        Assert.Equal(turn ? 5 : 1, converse.GetProperty("messages").GetArrayLength());
        Assert.False(HasDropBlock(wire.Requests[4].Body));
        Assert.Equal(turn ? 4 : 1, wire.Requests[4].Body.GetProperty("messages").GetArrayLength());
        Emit(wire, "count-format-only");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquiredMalformedResponseIsNotRetriedAndSuccessfulTurnAcquisitionStaysLearned(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.TurnError), new Reply(Malformed: true), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        await Assert.ThrowsAsync<ModelException>(() => CollectAsync(model, Turns()));
        Assert.Equal(2, wire.Requests.Length);
        Assert.Equal("summary", await CollectAsync(model, Turns()));
        await wire.Completion;
        Assert.Equal(5, wire.Requests[2].Body.GetProperty("messages").GetArrayLength());
        Emit(wire, "acquired-malformed");
    }

    [Fact]
    public async Task StreamErrorAfterPartialOutputDoesNotRetryOrLearnThinking()
    {
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wire = new BedrockRecoveryWireFixture(new Reply(StreamFault: true), new Reply()) { BeforeStreamFault = observed.Task };
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(true));
        var seen = new StringBuilder();
        try
        {
            await Assert.ThrowsAsync<ModelException>(async () =>
            {
                await foreach (var item in model.StreamAsync(Plain()))
                    if (item is ModelContentBlockDeltaEvent { Text: { } text }) { seen.Append(text); observed.TrySetResult(true); }
            });
        }
        finally { observed.TrySetResult(true); }
        Assert.Equal("partial", seen.ToString());
        Assert.Single(wire.Requests);
        Assert.Equal("summary", await CollectAsync(model, Plain()));
        await wire.Completion;
        Assert.False(HasDropBlock(wire.Requests[1].Body));
        Emit(wire, "partial-no-retry");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThinkingRetryReusesMappedImageBytesWithoutDroppingOrReplayingHistory(bool streaming)
    {
        await using var wire = new BedrockRecoveryWireFixture(new Reply(Error: BedrockRecoveryWireFixture.BindingError), new Reply());
        using var client = wire.CreateClient();
        using var model = new BedrockModel(client, Options(streaming));
        var request = new ModelRequest([new(Role.User, [new TextBlock("image 雪"), new Strands.Agents.ImageBlock("image/png", "AQIDBAU=")])]);
        Assert.Equal("summary", await CollectAsync(model, request));
        await wire.Completion;
        Assert.Equal(2, wire.Requests.Length);
        var first = wire.Requests[0].Body.GetProperty("messages");
        var second = wire.Requests[1].Body.GetProperty("messages");
        Assert.Equal("AQIDBAU=", first[0].GetProperty("content")[1].GetProperty("image").GetProperty("source").GetProperty("bytes").GetString());
        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Single(request.Messages);
        Emit(wire, "mapped-image-correspondence");
    }

    internal static BedrockModelOptions Options(bool streaming) => new()
    {
        ModelId = "fixture-model",
        Streaming = streaming,
        RecoverStaleThinkingBlocks = true,
        RecoverToolResultTurns = true,
        AdditionalModelRequestFields = JsonSerializer.Deserialize<JsonElement>("""{"thinking":{"type":"adaptive"}}"""),
        RequestTimeout = TimeSpan.FromSeconds(15)
    };

    internal static ModelRequest Plain() => new([Message.User("hello 雪")]);
    internal static ModelRequest Turns() => new([
        Message.User("Find it."),
        new(Role.Assistant, [new Strands.Agents.ToolUseBlock("history-7", "historical_lookup", JsonSerializer.SerializeToElement(new { id = 7 }))]),
        new(Role.User, [new Strands.Agents.ToolResultBlock("history-7", "saved result")]),
        Message.User("Summarize 雪")]);
    internal static async Task<string> CollectAsync(IModel model, ModelRequest request, CancellationToken cancellation = default)
    {
        var result = new StringBuilder();
        await foreach (var item in model.StreamAsync(request, cancellation))
            if (item is ModelContentBlockDeltaEvent { Text: { } text }) result.Append(text);
        return result.ToString();
    }
    internal static bool HasDropBlock(JsonElement body) => body.TryGetProperty("additionalModelRequestFields", out var fields) && fields.TryGetProperty("thinking", out var thinking)
        && thinking.ValueKind == JsonValueKind.Object && thinking.TryGetProperty("block_binding", out var binding) && binding.ValueKind == JsonValueKind.Object
        && binding.TryGetProperty("prefix_mismatch_behavior", out var behavior) && behavior.GetString() == "drop_block";
    private static bool IsAcknowledgement(JsonElement message) => message.GetProperty("role").GetString() == "assistant"
        && message.GetProperty("content")[0].TryGetProperty("text", out var text) && text.GetString() == "Tool result received.";
    private void Emit(BedrockRecoveryWireFixture wire, string scenario) => output.WriteLine(JsonSerializer.Serialize(new { Phase = "recovery-conformance-wire", Scenario = scenario, Requests = wire.Requests }));

    private sealed class FailureClient(Exception error) : AmazonBedrockRuntimeClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        internal int Calls { get; private set; }
        internal bool Streaming { get; private set; }
        internal bool Disposed { get; private set; }
        public override Task<ConverseResponse> ConverseAsync(ConverseRequest request, CancellationToken cancellationToken = default)
        { Calls++; Streaming = false; return Task.FromException<ConverseResponse>(error); }
        public override Task<ConverseStreamResponse> ConverseStreamAsync(ConverseStreamRequest request, CancellationToken cancellationToken = default)
        { Calls++; Streaming = true; return Task.FromException<ConverseStreamResponse>(error); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
