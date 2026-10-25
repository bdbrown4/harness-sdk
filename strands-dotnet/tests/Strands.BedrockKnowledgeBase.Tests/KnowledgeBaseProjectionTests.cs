using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgentRuntime;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime.Documents;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseProjectionTests
{
    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, "_sourceLocation", "_relevanceScore")]
    [InlineData(BedrockKnowledgeBaseProfile.Python, "_source_location", "_relevance_score")]
    public async Task MetadataIsDetachedReservedKeysOverrideAndNoResultsAreLocallyTaken(BedrockKnowledgeBaseProfile profile, string locationKey, string scoreKey)
    {
        using var runtime = new RuntimeClient();
        var metadata = new Dictionary<string, Document> { [locationKey] = Document.FromObject("wrong"), [scoreKey] = Document.FromObject(9), ["nested"] = Document.FromObject(new Dictionary<string, object?> { ["nil"] = null, ["list"] = new[] { "a" } }) };
        var location = new RetrievalResultLocation { Type = RetrievalResultLocationType.S3, S3Location = new() { Uri = "s3://bucket/key" } };
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Content = new() { Text = "first" }, Score = 0, Metadata = metadata, Location = location }, new() { Content = null, Metadata = new() }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { Profile = profile });
        var entries = await store.SearchAsync("query", 1);
        metadata.Clear(); location.S3Location.Uri = "changed";
        Assert.Equal(new[] { "first", "" }, entries.Select(entry => entry.Content));
        var first = entries[0].Metadata!.Value;
        Assert.Equal(0, first.GetProperty(scoreKey).GetDouble());
        Assert.Equal("s3://bucket/key", first.GetProperty(locationKey).GetProperty("s3Location").GetProperty("uri").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("nested").GetProperty("nil").ValueKind);
        Assert.Equal(JsonValueKind.Object, entries[1].Metadata!.Value.ValueKind);
    }

    [Theory]
    [InlineData("1.25", true, 1.25)]
    [InlineData("1e2", true, 100)]
    [InlineData("+.5", true, .5)]
    [InlineData("1.", true, 1)]
    [InlineData("0x10", true, 16)]
    [InlineData("0b101", true, 5)]
    [InlineData("0o17", true, 15)]
    [InlineData("\uFEFF  2\u00a0", true, 2)]
    [InlineData("-0", true, 0)]
    [InlineData("", false, 0)]
    [InlineData(" \uFEFF", false, 0)]
    [InlineData("Infinity", false, 0)]
    [InlineData("-Infinity", false, 0)]
    [InlineData("NaN", false, 0)]
    [InlineData("1e999", false, 0)]
    [InlineData("-0x10", false, 0)]
    [InlineData("+0b1", false, 0)]
    [InlineData("0o8", false, 0)]
    [InlineData("0x", false, 0)]
    [InlineData("1_000", false, 0)]
    [InlineData("1,5", false, 0)]
    public async Task TypeScriptNumericWrapperFollowsFiniteEcmaNumberRatherThanDecimalOnly(string encoded, bool converted, double expected)
    {
        using var runtime = new RuntimeClient();
        var wrapper = Document.FromObject(new Dictionary<string, object> { ["string"] = encoded, ["type"] = "bigDecimal" });
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Metadata = new() { ["value"] = wrapper, ["nested"] = Document.FromObject(new Dictionary<string, object> { ["value"] = new Dictionary<string, object> { ["string"] = "2", ["type"] = "bigDecimal" } }) } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        var value = Assert.Single(await store.SearchAsync("query")).Metadata!.Value;
        Assert.Equal(converted ? JsonValueKind.Number : JsonValueKind.Object, value.GetProperty("value").ValueKind);
        if (converted) Assert.Equal(expected, value.GetProperty("value").GetDouble()); else Assert.Equal(encoded, value.GetProperty("value").GetProperty("string").GetString());
        Assert.Equal(JsonValueKind.Object, value.GetProperty("nested").GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task PythonLeavesNumericWrappersOpaqueAndMissingResultsAreEmpty()
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Metadata = new() { ["value"] = Document.FromObject(new { @string = "0x10", type = "bigDecimal" }) } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { Profile = BedrockKnowledgeBaseProfile.Python });
        Assert.Equal("0x10", Assert.Single(await store.SearchAsync("query")).Metadata!.Value.GetProperty("value").GetProperty("string").GetString());
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse());
        Assert.Empty(await store.SearchAsync("query"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScoreBoundsAreInclusiveAtZeroAndRetainUnscoredEntries(bool maximum)
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Content = new() { Text = "zero" }, Score = 0 }, new() { Content = new() { Text = "positive" }, Score = .5 }, new() { Content = new() { Text = "unscored" } }] });
        var options = maximum ? TestSupport.Options(runtime) with { MaxScore = 0 } : TestSupport.Options(runtime) with { MinScore = 0 };
        await using var store = new BedrockKnowledgeBaseStore(options);
        Assert.Equal(maximum ? new[] { "zero", "unscored" } : new[] { "zero", "positive", "unscored" }, (await store.SearchAsync("query")).Select(x => x.Content));
    }

    [Fact]
    public async Task NanBoundUsesSourceComparisonAndRetainsOnlyUnscoredEntries()
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Content = new() { Text = "scored" }, Score = .5 }, new() { Content = new() { Text = "unscored" } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { MinScore = double.NaN });
        Assert.Equal("unscored", Assert.Single(await store.SearchAsync("query")).Content);
    }

    [Fact]
    public async Task EmptyNestedLocationObjectRemainsPartOfTheAcceptedSourceLocation()
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Location = new() { S3Location = new() } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        var location = Assert.Single(await store.SearchAsync("query")).Metadata!.Value.GetProperty("_sourceLocation");
        Assert.Empty(location.GetProperty("s3Location").EnumerateObject());
    }
}
