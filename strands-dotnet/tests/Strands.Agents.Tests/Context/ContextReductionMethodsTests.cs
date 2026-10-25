using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Context;

public sealed class ContextReductionMethodsTests
{
    [Fact]
    public void PreviewKeepsBothEndsAndIncludesStableElisionMetadata()
    {
        var text = "BEGIN" + new string('x', 1_000) + "END";

        var preview = ContextReductionMethods.BuildPreview(text, 2, new() { PreviewTokens = 10 });

        Assert.StartsWith("[Truncated: 2 blocks, ~252 tokens]\n\nBEGIN", preview);
        Assert.Contains("[... 968 chars elided ...]", preview);
        Assert.EndsWith("END", preview);
        Assert.True(preview.Length < text.Length);
    }

    [Fact]
    public void RichTruncationPreservesOpaqueAndStructuredDataAndToolCorrelation()
    {
        var image = new ImageBlock("image/png", "AA==");
        var structured = JsonSerializer.SerializeToElement(new { authoritative = true });
        var original = new ToolResultBlock("call", [new TextBlock(new string('x', 8_000)), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 1 })), image], ToolResultStatus.Error, structured);

        var reduced = ContextReductionMethods.TruncateToolResult(original, new() { PreviewTokens = 10 });

        Assert.NotSame(original, reduced);
        Assert.Equal("call", reduced.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, reduced.Status);
        Assert.Equal(image, reduced.ContentBlocks[1]);
        Assert.True(JsonElement.DeepEquals(structured, reduced.StructuredContent!.Value));
        Assert.StartsWith("[Truncated: 2 blocks", Assert.IsType<TextBlock>(reduced.ContentBlocks[0]).Text);
        Assert.Equal(8_000, Assert.IsType<TextBlock>(original.ContentBlocks[0]).Text.Length);
    }

    [Fact]
    public void NativeJsonIsRetainedWhenConvertingItsPreviewToTextWouldIncreaseEncodedSize()
    {
        var values = Enumerable.Repeat("", 400).ToArray();
        var original = new ToolResultBlock("call", [new JsonBlock(JsonSerializer.SerializeToElement(values))]);

        var reduced = ContextReductionMethods.TruncateToolResult(original, new() { PreviewTokens = 200 });

        static int NativeSize(ToolResultBlock result) => JsonSerializer.SerializeToUtf8Bytes(result.ContentBlocks.Select(block => block switch
        {
            JsonBlock json => (object)new { json = json.Json },
            TextBlock text => new { text = text.Text },
            _ => throw new InvalidOperationException("Unexpected content in native JSON fixture.")
        }).ToArray(), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Length;
        Assert.True(NativeSize(reduced) <= NativeSize(original), "A text preview must not expand native typed JSON on the wire.");
        Assert.Same(original, reduced);
    }

    [Theory]
    [InlineData(PreviewMode.Head)]
    [InlineData(PreviewMode.Tail)]
    public void PreviewCanKeepOnlyTheRequestedEnd(PreviewMode mode)
    {
        var preview = ContextReductionMethods.BuildPreview("BEGIN" + new string('x', 1_000) + "END", options: new() { PreviewTokens = 10, Preview = mode });
        Assert.Equal(mode == PreviewMode.Head, preview.Contains("BEGIN"));
        Assert.Equal(mode == PreviewMode.Tail, preview.EndsWith("END", StringComparison.Ordinal));
    }

    [Fact]
    public void PreviewDoesNotExpandSmallInputsOrInterpretHostCulture()
    {
        const string small = "small";
        Assert.Same(small, ContextReductionMethods.BuildPreview(small, options: new() { PreviewTokens = 0 }));
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("fr-FR");
            Assert.Contains("2,000 tokens", ContextReductionMethods.BuildPreview(new string('x', 8_000), options: new() { PreviewTokens = 0 }));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void JsonCanBeReducedWhenSafeButDuplicatePropertyNormalizationCannotHideGrowth()
    {
        var large = new ToolResultBlock("call", [new JsonBlock(JsonSerializer.SerializeToElement(new { value = new string('x', 8_000) }))]);
        Assert.IsType<TextBlock>(Assert.Single(ContextReductionMethods.TruncateToolResult(large, new() { PreviewTokens = 10 }).ContentBlocks));
        var duplicate = JsonSerializer.Deserialize<JsonElement>("{\"value\":\"" + new string('x', 8_000) + "\",\"value\":\"short\"}");
        var original = new ToolResultBlock("call", [new JsonBlock(duplicate)]);
        Assert.Same(original, ContextReductionMethods.TruncateToolResult(original, new() { PreviewTokens = 10 }));
    }

    [Fact]
    public void OpaqueOnlyResultsAndStructuredDataRemainUntouched()
    {
        var opaque = new ToolResultBlock("call", [new ImageBlock("image/png", "AA==")], structuredContent: JsonSerializer.SerializeToElement(new { value = "authoritative" }));
        Assert.Same(opaque, ContextReductionMethods.TruncateToolResult(opaque));
    }

    [Fact]
    public void InvalidPreviewAllowancesAndModesFailBeforeAllocating()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContextReductionMethods.BuildPreview("text", options: new() { PreviewTokens = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContextReductionMethods.BuildPreview("text", options: new() { PreviewTokens = int.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ContextReductionMethods.BuildPreview("text", options: new() { Preview = (PreviewMode)100 }));
    }

    [Fact]
    public void StandaloneTextReductionReturnsASmallerImmutableBlock()
    {
        var original = new TextBlock(new string('x', 8_000));

        var reduced = ContextReductionMethods.TruncateText(original, new() { PreviewTokens = 10 });

        Assert.NotSame(original, reduced);
        Assert.StartsWith("[Truncated:", reduced.Text);
        Assert.Equal(8_000, original.Text.Length);
    }

    [Fact]
    public void RetainedOpaqueContentCannotMakeAnExpandingJsonPreviewAppearSmaller()
    {
        var original = new ToolResultBlock("call", [new JsonBlock(JsonSerializer.SerializeToElement(Enumerable.Repeat("", 400).ToArray())), new ImageBlock("image/png", Convert.ToBase64String(new byte[8_000]))]);

        Assert.Same(original, ContextReductionMethods.TruncateToolResult(original, new() { PreviewTokens = 200 }));
    }

    [Fact]
    public void PreviewDoesNotSplitAnOtherwiseValidSurrogatePair()
    {
        var original = new string('a', 23) + "\U0001F600" + new string('x', 1_000);

        var preview = ContextReductionMethods.BuildPreview(original, options: new() { PreviewTokens = 10 });

        Assert.Null(Record.Exception(() => new System.Text.UTF8Encoding(false, true).GetBytes(preview)));
    }

    [Fact]
    public async Task FailedMultimodalSummarizationFallsBackToTextWithoutReasoningSignatures()
    {
        var model = new MultimodalFallbackModel();

        var summary = await ContextReductionMethods.SummarizeAsync([new TextBlock("facts"), new ImageBlock("image/png", "AA=="), new ReasoningBlock("private", "signature")], model);

        Assert.Equal("summary", summary);
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[0].Messages[0].Content, block => block is ImageBlock);
        Assert.DoesNotContain(model.Requests[0].Messages[0].Content, block => block is ReasoningBlock);
        Assert.All(model.Requests[1].Messages[0].Content, block => Assert.IsType<TextBlock>(block));
    }

    [Fact]
    public async Task ValidEmptySummaryDoesNotRetryTheModel()
    {
        var model = new AgentTests.ScriptedModel([new ModelMessageStartEvent(), new ModelMessageStopEvent(StopReasons.EndTurn)]);

        var summary = await ContextReductionMethods.SummarizeAsync([new TextBlock("facts"), new ImageBlock("image/png", "AA==")], model);

        Assert.Null(summary);
        Assert.Single(model.Requests);
    }

    private sealed class MultimodalFallbackModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Messages.SelectMany(message => message.Content).Any(block => block is ImageBlock)) throw new NotSupportedException("This summarizer accepts text only.");
            foreach (var item in AgentTests.Text("summary")) yield return item;
            await Task.CompletedTask;
        }
    }
}
