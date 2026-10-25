using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Context;

public sealed class ContextRetrievalTests
{
    [Fact]
    public async Task SearchUsesSharedHeadersMatchMarkersAndGapSeparators()
    {
        var text = string.Join("\n", Enumerable.Range(1, 400).Select(index => $"line {index}: {(index is 2 or 20 ? "needle" : "ordinary content text")}"));
        var (agent, reference) = await Create(new("call", text));
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "needle", context_lines = 0 });
        Assert.Equal("[2 matches for /needle/]\n\n>  2| line 2: needle\n---\n> 20| line 20: needle", result.Content);
    }

    [Fact]
    public async Task LineRangeUsesSharedHeaderAndPaddedLineNumbers()
    {
        var text = string.Join("\n", Enumerable.Range(1, 400).Select(index => "line " + index + ": ordinary content text"));
        var (agent, reference) = await Create(new("call", text));
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, line_range = new { start = 2, end = 3 } });
        Assert.Equal("[Lines 2-3 of 400]\n\n  2| line 2: ordinary content text\n  3| line 3: ordinary content text", result.Content);
    }

    [Fact]
    public async Task CapsPatternAtSharedTwoHundredCharacterLimit()
    {
        var prefix = new string('a', 200);
        var (agent, reference) = await Create(new("call", prefix + "\n" + new string('x', 8_000)));
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = prefix + "z", context_lines = 0 });
        Assert.StartsWith("[1 match for /" + new string('a', 50) + "/]", result.Content);
        Assert.EndsWith("> 1| " + prefix, result.Content);
    }

    [Fact]
    public async Task NestedQuantifierFallsBackToLiteralPattern()
    {
        var (agent, reference) = await Create(new("call", "^(a+)+$\n" + new string('a', 8_000) + "!"));
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "^(a+)+$", context_lines = 0 });
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("[1 match for /^(a+)+$/]\n\n> 1| ^(a+)+$", result.Content);
    }

    [Fact]
    public async Task RetrievesOriginalTextByRangeAndPatternWithClippedContext()
    {
        var text = string.Join("\n", Enumerable.Range(1, 400).Select(index => $"line {index}: {(index == 20 ? "needle" : "ordinary content text")}"));
        var (agent, reference) = await Create(new("call", text));
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "needle", line_range = new { start = 19, end = 21 }, context_lines = 5 });
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.StartsWith("[1 match for /needle/ in lines 19-21]", result.Content);
        Assert.Contains("  19| line 19:", result.Content);
        Assert.Contains("> 20| line 20: needle", result.Content);
        Assert.Contains("  21| line 21:", result.Content);
        Assert.DoesNotContain("18| line 18:", result.Content);
        Assert.DoesNotContain("22| line 22:", result.Content);
        var range = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, line_range = new { start = 2, end = 3 } });
        Assert.StartsWith("[Lines 2-3 of 400]", range.Content);
        Assert.Contains("  2| line 2:", range.Content);
        Assert.Contains("  3| line 3:", range.Content);
        Assert.DoesNotContain("4| line 4:", range.Content);
    }

    [Fact]
    public async Task InvalidRegexFallsBackToLiteralKeywordAndNoMatchesAreExplicit()
    {
        var (agent, reference) = await Create(new("call", "[needle\n" + new string('x', 8_000)));
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "[needle", context_lines = 0 });
        Assert.Equal("[1 match for /[needle/]\n\n> 1| [needle", result.Content);
        Assert.Equal("No matches found for pattern 'absent' (searched 2 lines).", (await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "absent" })).Content);
    }

    [Fact]
    public async Task BoundsFullAndFilteredTextIncludingTruncationMarker()
    {
        var (agent, reference) = await Create(new("call", new string('x', 8_000)), new() { MaxRetrievalTokens = 40 });
        var full = await ToolResultOffloaderTests.Retrieve(agent, reference);
        var filtered = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "x" });
        Assert.Equal(160, full.Content.Length);
        Assert.EndsWith("[truncated]", full.Content);
        Assert.Equal(160, filtered.Content.Length);
        Assert.EndsWith("[output truncated, narrow your search]", filtered.Content);
    }

    [Fact]
    public async Task PreservesTypedJsonAndOpaqueOriginals()
    {
        ContentBlock[] blocks = [new JsonBlock(JsonSerializer.SerializeToElement(new { value = new string('j', 4_000) })), new ImageBlock("image/png", "AA=="), new AudioBlock("audio/wav", "AQ=="), new DocumentBlock("application/pdf", "Ag=="), new ResourceLinkBlock("https://example.test", "resource")];
        var (agent, reference) = await Create(new("call", blocks));
        var json = await ToolResultOffloaderTests.Retrieve(agent, reference);
        Assert.Equal(new string('j', 4_000), Assert.IsType<JsonBlock>(Assert.Single(json.ContentBlocks)).Json.GetProperty("value").GetString());
        for (var index = 1; index < blocks.Length; index++)
        {
            var nextReference = reference.Substring(0, reference.LastIndexOf('_') + 1) + index;
            Assert.Equal(blocks[index], Assert.Single((await ToolResultOffloaderTests.Retrieve(agent, nextReference)).ContentBlocks));
            Assert.Equal(ToolResultStatus.Error, (await ToolResultOffloaderTests.Retrieve(agent, nextReference, new { reference = nextReference, pattern = "x" })).Status);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(5, 9)]
    public async Task ReportsInvalidRangesAsCorrelatedErrors(int start, int end)
    {
        var (agent, reference) = await Create(new("call", new string('x', 8_000)));
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, line_range = new { start, end } });
        Assert.Equal("retrieve", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.StartsWith("Error:", result.Content);
    }

    [Fact]
    public async Task MissingTamperedAndNegativeReferencesReturnErrors()
    {
        var (agent, reference) = await Create(new("call", new string('x', 8_000)));
        foreach (var invalid in new[] { "missing", reference + "0", reference.Replace("ctx_2_", "ctx_-1_", StringComparison.Ordinal), reference.Replace("ctx_2_", "ctx_999_", StringComparison.Ordinal) })
            Assert.Equal(ToolResultStatus.Error, (await ToolResultOffloaderTests.Retrieve(agent, invalid)).Status);
    }

    [Fact]
    public async Task RegexTimeoutProducesBoundedToolError()
    {
        var (agent, reference) = await Create(new("call", new string('a', 20_000) + "!"), new() { RegexTimeout = TimeSpan.FromMilliseconds(1) });
        var result = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "^(a|aa)+$" });
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("timeout", result.Content);
    }

    [Fact]
    public async Task CancelledRetrievalPropagatesCallerToken()
    {
        var (agent, reference) = await Create(new("call", new string('x', 8_000)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolResultOffloaderTests.Retrieve(agent, reference, cancellationToken: cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task TinyBudgetsAlsoBoundErrorsAndEmptySearches()
    {
        var (agent, reference) = await Create(new("call", new string('x', 8_000)), new() { MaxRetrievalTokens = 2 });
        var missing = await ToolResultOffloaderTests.Retrieve(agent, new string('m', 10_000));
        Assert.Equal(ToolResultStatus.Error, missing.Status);
        Assert.True(missing.Content.Length <= 8);
        var empty = await ToolResultOffloaderTests.Retrieve(agent, reference, new { reference, pattern = "absent" });
        Assert.True(empty.Content.Length <= 8);
    }

    private static async Task<(Agent Agent, string Reference)> Create(ToolResultBlock result, ToolResultOffloadOptions? options = null)
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var agent = new Agent(new() { Model = model, Messages = ToolResultOffloaderTests.History(result), Plugins = [new ToolResultOffloader(options)] });
        await agent.InvokeAsync(null);
        return (agent, ToolResultOffloaderTests.Reference(model.Requests[0]));
    }
}
