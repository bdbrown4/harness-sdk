using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Bedrock.Tests;

public sealed class BedrockCitationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentCitationConfigurationAndContextReachTheNativeRequest(bool enabled)
    {
        using var body = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        var document = new DocumentBlock("application/pdf", "AQID", "Report") { CitationsEnabled = enabled, Context = "Reference material" };

        await BedrockModelTests.CollectAsync(model, new([new Message(Role.User, [document])]));

        var native = Assert.Single(Assert.Single(Assert.Single(client.Requests).Messages).Content).Document;
        Assert.NotNull(native.Citations);
        Assert.Equal(enabled, native.Citations.Enabled);
        Assert.Equal("Reference material", native.Context);
    }

    [Theory]
    [InlineData("""{"documentChar":{"documentIndex":1,"start":2,"end":3}}""", typeof(DocumentCharCitationLocation))]
    [InlineData("""{"documentPage":{"documentIndex":1,"start":2,"end":3}}""", typeof(DocumentPageCitationLocation))]
    [InlineData("""{"documentChunk":{"documentIndex":1,"start":2,"end":3}}""", typeof(DocumentChunkCitationLocation))]
    [InlineData("""{"searchResultLocation":{"searchResultIndex":1,"start":2,"end":3}}""", typeof(SearchResultCitationLocation))]
    [InlineData("""{"web":{"url":"https://example.test/source","domain":"example.test"}}""", typeof(WebCitationLocation))]
    public async Task CitationDeltasAggregateWithTextAndReplayUsingNativeLocations(string location, Type expected)
    {
        using var first = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"Answer"}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"citation":{"location":LOCATION,"source":"source","title":"Title","sourceContent":[{"text":"evidence"}]}}}""".Replace("LOCATION", location)),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var second = BedrockModelTests.Wire(("messageStart", """{"role":"assistant"}"""), ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(first, second);
        using var model = new BedrockModel(client, new());
        await using var agent = new Agent(new() { Model = model });

        var result = await agent.InvokeAsync("question");

        var block = Assert.IsType<CitationsBlock>(Assert.Single(result.Message!.Content));
        Assert.Equal("Answer", result.Message.Text);
        var citation = Assert.Single(block.Citations);
        Assert.IsType(expected, citation.Location);
        Assert.Equal("evidence", Assert.Single(citation.SourceContent).Text);
        await agent.InvokeAsync("continue");
        var replay = Assert.Single(client.Requests[1].Messages[1].Content).CitationsContent;
        Assert.NotNull(replay);
        Assert.Equal("Answer", Assert.Single(replay.Content).Text);
        Assert.Equal("source", Assert.Single(replay.Citations).Source);
        Assert.Equal("Title", replay.Citations[0].Title);
    }

    [Fact]
    public async Task CitationBeforeTextRetainsTheSubsequentGeneratedContent()
    {
        using var body = BedrockModelTests.Wire(
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"citation":{"location":{"web":{"url":"https://example.test"}}}}}"""),
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"later text"}}"""),
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""));
        using var client = new BedrockModelTests.StubClient(body);
        using var model = new BedrockModel(client, new());
        await using var agent = new Agent(new() { Model = model });

        var result = await agent.InvokeAsync("question");

        Assert.Equal("later text", result.Message!.Text);
        Assert.Single(Assert.IsType<CitationsBlock>(Assert.Single(result.Message.Content)).Citations);
    }
}
