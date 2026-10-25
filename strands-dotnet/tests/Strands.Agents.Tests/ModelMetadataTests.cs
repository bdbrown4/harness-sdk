using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests;

public sealed class ModelMetadataTests
{
    [Fact]
    public async Task OrdinaryModelsEstimateTextWithoutRequiringAnotherCapability()
    {
        IModel model = new Model();

        var count = await model.CountTokensAsync(new ModelRequest([Message.User("abcde")]));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task HeuristicIncludesSystemToolsReasoningAndRichResultsWithoutDuplicatingDisplayText()
    {
        IModel model = new Model();
        var input = JsonSerializer.SerializeToElement(new { x = 1 });
        var json = JsonSerializer.SerializeToElement(new { ok = true });
        var structured = JsonSerializer.SerializeToElement(new { more = "v" });
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var result = new ToolResultBlock("call", [new TextBlock("result"), new JsonBlock(json), new ImageBlock("image/png", new string('x', 8_000))], structuredContent: structured);
        var request = new ModelRequest([
            Message.User("abcde"),
            new(Role.Assistant, [new ToolUseBlock("call", "lookup", input), new ReasoningBlock("think", "ignored signature")]),
            new(Role.User, [result])
        ], "rules", [new ToolSpec("lookup", "Look up", schema)]);
        const string toolJson = "{\"name\":\"lookup\",\"description\":\"Look up\",\"inputSchema\":{\"type\":\"object\"}}";

        var count = await model.CountTokensAsync(request);

        // Text: system2 + user2 + toolname2 + reasoning2 + result2.
        // JSON: input4 + result6 + structured6, plus the wire-shaped tool specification.
        Assert.Equal(26 + (toolJson.Length + 1L) / 2, count);
    }

    [Fact]
    public async Task OptionalNativeCounterReceivesTheWholeRequestAndCallerToken()
    {
        var request = new ModelRequest([Message.User("abcde")], "system");
        using var cancellation = new CancellationTokenSource();
        ModelRequest? observed = null;
        var token = default(CancellationToken);
        IModel model = new CountingModel((value, ct) => { observed = value; token = ct; return new(47L); });

        var count = await model.CountTokensAsync(request, cancellation.Token);

        Assert.Equal(47, count);
        Assert.Same(request, observed);
        Assert.Equal(cancellation.Token, token);
    }

    [Fact]
    public void LimitsResolvePinnedModelIdsAndRetainConfiguredOutputAllowance()
    {
        IModel model = new MetadataModel(new("global.anthropic.claude-opus-5", MaxOutputTokens: 64_000));

        var limits = model.GetLimits();

        Assert.Equal(1_000_000, limits.ContextWindowLimit);
        Assert.Equal(64_000, limits.MaxOutputTokens);
        Assert.Equal("global.anthropic.claude-opus-5", limits.ModelId);
    }

    [Fact]
    public void UtilizationPreservesOverflowAndUsesExplicitContextOverrides()
    {
        IModel model = new MetadataModel(new(ContextWindowLimit: 1_000));

        var utilization = model.EstimateUtilization(250, new(ContextWindowLimit: 100));

        Assert.Equal(2.5, utilization);
    }

    [Fact]
    public async Task ResponsesVisibleTextCountsWithoutDuplicatingNativeMessageOrOpaqueReasoning()
    {
        IModel model = new Model();
        var part = JsonSerializer.SerializeToElement(new { type = "output_text", text = "abcde" });
        var item = JsonSerializer.SerializeToElement(new { type = "message", id = "msg", role = "assistant", status = "completed", content = new[] { part } });
        var reasoning = JsonSerializer.SerializeToElement(new { type = "reasoning", id = "reason", summary = new[] { new { text = "think" } }, encrypted_content = new string('x', 8_000) });
        var refusal = JsonSerializer.SerializeToElement(new { type = "refusal", refusal = "cannot" });
        var request = new ModelRequest([new Message(Role.Assistant, [new ResponsesContentBlock("msg", 0, part, item: item), new ResponsesReasoningBlock(reasoning), new ResponsesContentBlock("refusal", 0, refusal)])]);

        Assert.Equal(6, await model.CountTokensAsync(request));
    }

    [Fact]
    public async Task GoogleVisibleTextAndThoughtSummariesCountWithoutSignatureOrMediaPayload()
    {
        IModel model = new Model();
        var content = new GoogleContentBlock(JsonSerializer.SerializeToElement(new { text = "abcde", thoughtSignature = new string('x', 8_000) }));
        var thought = new GoogleContentBlock(JsonSerializer.SerializeToElement(new { text = "think", thought = true, thoughtSignature = new string('x', 8_000) }));
        var media = new GoogleContentBlock(JsonSerializer.SerializeToElement(new { inlineData = new { mimeType = "image/png", data = new string('x', 8_000) } }));

        Assert.Equal(4, await model.CountTokensAsync([new Message(Role.Assistant, [content, thought, media])]));
    }

    [Theory]
    [InlineData("claude-sonnet-4-6", 1_000_000L)]
    [InlineData("eu.anthropic.claude-opus-5", 1_000_000L)]
    [InlineData("us-gov.anthropic.claude-sonnet-4-6", 1_000_000L)]
    [InlineData("amazon.nova-micro-v1:0", 128_000L)]
    [InlineData("gpt-5.4", 1_050_000L)]
    [InlineData("gpt-4.1", 1_047_576L)]
    [InlineData("gemini-2.5-flash", 1_048_576L)]
    [InlineData("custom-model", 200_000L)]
    [InlineData("US.anthropic.claude-opus-5", 200_000L)]
    public void ContextLookupMatchesPinnedIdentifiersAndCaseSensitiveRegionRules(string modelId, long expected)
    {
        Assert.Equal(expected, new MetadataModel(new(modelId)).GetLimits().ContextWindowLimit);
    }

    [Fact]
    public void ExplicitLimitsTakePrecedenceOverProviderValuesAndLookup()
    {
        IModel model = new MetadataModel(new("gpt-5.4", 1_000, 50));

        var limits = model.GetLimits(new(ContextWindowLimit: 100, MaxOutputTokens: 20));

        Assert.Equal(new ModelMetadata("gpt-5.4", 100, 20), limits);
    }

    [Fact]
    public async Task CounterFailuresRemainVisibleAndNegativeCountsAreRejected()
    {
        var failure = new IOException("native counting failed");
        IModel failing = new CountingModel((_, _) => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failing.CountTokensAsync(new ModelRequest([])).AsTask()));
        IModel negative = new CountingModel((_, _) => new(-1L));
        await Assert.ThrowsAsync<InvalidDataException>(() => negative.CountTokensAsync(new ModelRequest([])).AsTask());
    }

    [Fact]
    public async Task NativeAndHeuristicCountingHonorCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        IModel native = new CountingModel((_, _) => { calls++; cancellation.Cancel(); return new(10L); });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.CountTokensAsync(new ModelRequest([]), cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => native.CountTokensAsync(new ModelRequest([]), cancellation.Token).AsTask());
        Assert.Equal(1, calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Model().CountTokensAsync(new ModelRequest([]), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task LongCountsDoNotOverflowAtInt32AndOpaqueMediaCostsAreNotInvented()
    {
        IModel model = new Model();
        var message = Message.User(new string('x', 1_000_000));
        Assert.Equal(5_000_000_000L, await model.CountTokensAsync(Enumerable.Repeat(message, 20_000).ToArray()));
        Assert.Equal(0, await model.CountTokensAsync([new Message(Role.User, [new ImageBlock("image/png", "AA=="), new AudioBlock("audio/wav", "AA=="), new DocumentBlock("application/pdf", "AA=="), new RedactedReasoningBlock("AA=="), new CachePointBlock()])]));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void InvalidContextAndOutputLimitsAreRejected(long invalid)
    {
        IModel model = new Model();
        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetLimits(new(ContextWindowLimit: invalid)));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetLimits(new(MaxOutputTokens: invalid)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetadataModel(new(ContextWindowLimit: invalid)).GetLimits());
    }

    private class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }

    private sealed class CountingModel(Func<ModelRequest, CancellationToken, ValueTask<long>> count) : Model, IModelTokenCounter
    {
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default) => count(request, cancellationToken);
    }

    private sealed class MetadataModel(ModelMetadata metadata) : Model, IModelMetadataProvider
    {
        public ModelMetadata Metadata => metadata;
    }
}
