using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Compatibility.Tests;

public sealed class ModelMetadataCompatibilityTests
{
    [Fact]
    public async Task RetryAndNestedRoutingForwardDefaultModelLimitsAndNativeTokenCounting()
    {
        var request = new ModelRequest([Message.User("visible")], "rules");
        var native = new MetadataCountingModel();
        var retry = new RetryModel(native);
        IModel router = new RoutingModel([new(new RoutingModel([new(retry)])), new(new MetadataModel(new("small", 100)))]);

        Assert.Equal(1_000_000, ((IModel)retry).GetLimits().ContextWindowLimit);
        Assert.Equal(123, await ((IModel)retry).CountTokensAsync(request));
        Assert.Equal(1_000_000, router.GetLimits().ContextWindowLimit);
        Assert.Equal(123, await router.CountTokensAsync(request));
        var invocation = ((IInvocationModel)router).CreateInvocationModel(new Dictionary<string, object?>());
        Assert.Equal(1_000_000, invocation.GetLimits().ContextWindowLimit);
        Assert.Equal(123, await invocation.CountTokensAsync(request));
        Assert.Same(request, native.Request);
        Assert.Equal(3, native.Counts);
    }

    private sealed class MetadataCountingModel : Model, IModelMetadataProvider, IModelTokenCounter
    {
        public ModelMetadata Metadata => new("global.anthropic.claude-opus-5", MaxOutputTokens: 64);
        internal ModelRequest? Request;
        internal int Counts;
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Request = request; Counts++; cancellationToken.ThrowIfCancellationRequested(); return new(123L); }
    }

    [Fact]
    public async Task HeuristicCountsRichRequestOnceAndIgnoresOpaqueMedia()
    {
        IModel model = new Model();
        var result = new ToolResultBlock("call", [new TextBlock("result"), new JsonBlock(JsonSerializer.SerializeToElement(new { ok = true })), new ImageBlock("image/png", new string('x', 8_000))]);
        var request = new ModelRequest([
            Message.User("abcde"),
            new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { x = 1 })), new ReasoningBlock("think", "ignored")]),
            new(Role.User, [result])
        ], "rules");

        Assert.Equal(20, await model.CountTokensAsync(request));
    }

    [Fact]
    public async Task HeuristicUsesLongTotalsAcrossLargeSharedHistories()
    {
        IModel model = new Model();
        var message = Message.User(new string('x', 1_000_000));

        var count = await model.CountTokensAsync(Enumerable.Repeat(message, 20_000).ToArray());

        Assert.Equal(5_000_000_000L, count);
    }

    [Fact]
    public async Task NativeCountingPreservesProviderErrorsAndRejectsNegativeResults()
    {
        var request = new ModelRequest([]);
        var failure = new IOException("counter failed");
        IModel failing = new CountingModel((_, _) => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failing.CountTokensAsync(request).AsTask()));
        IModel negative = new CountingModel((_, _) => new(-1L));
        await Assert.ThrowsAsync<InvalidDataException>(() => negative.CountTokensAsync(request).AsTask());
    }

    [Fact]
    public async Task CountingChecksCancellationBeforeAndAfterNativeCapability()
    {
        var request = new ModelRequest([]);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        IModel model = new CountingModel((_, _) => { calls++; cancellation.Cancel(); return new(42L); });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CountTokensAsync(request, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CountTokensAsync(request, cancellation.Token).AsTask());
        Assert.Equal(1, calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Model().CountTokensAsync(request, cancellation.Token).AsTask());
    }

    [Theory]
    [InlineData("global.anthropic.claude-opus-5", 1_000_000L)]
    [InlineData("us.anthropic.claude-sonnet-4-6", 1_000_000L)]
    [InlineData("gpt-5.4", 1_050_000L)]
    [InlineData("gemini-2.5-flash", 1_048_576L)]
    [InlineData("custom-deployment", 200_000L)]
    public void ContextLimitsUsePinnedMetadataAndExplicitOverrides(string modelId, long contextWindow)
    {
        IModel model = new MetadataModel(new(modelId, MaxOutputTokens: 64));
        Assert.Equal(contextWindow, model.GetLimits().ContextWindowLimit);
        Assert.Equal(64, model.GetLimits().MaxOutputTokens);
        Assert.Equal(new ModelMetadata(modelId, 100, 20), model.GetLimits(new(ContextWindowLimit: 100, MaxOutputTokens: 20)));
        Assert.Equal(2.5, model.EstimateUtilization(250, new(ContextWindowLimit: 100)));
        Assert.Equal(0, model.EstimateUtilization(0));
        Assert.Equal(ModelMetadata.Unknown, new Model().GetLimits());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void InvalidLimitsAndNegativeInputCountsAreRejected(long invalid)
    {
        IModel model = new Model();
        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetLimits(new(ContextWindowLimit: invalid)));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.GetLimits(new(MaxOutputTokens: invalid)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetadataModel(new(ContextWindowLimit: invalid)).GetLimits());
        Assert.Throws<ArgumentOutOfRangeException>(() => model.EstimateUtilization(-1));
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
