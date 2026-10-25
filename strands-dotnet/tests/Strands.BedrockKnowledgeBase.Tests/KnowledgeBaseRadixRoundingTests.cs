using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime.Documents;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseRadixRoundingTests
{
    [Theory]
    [InlineData("0b1000000000000000000000000000000000000000000000000000001", 18014398509481984d)]
    [InlineData("0o1000000000000000001", 18014398509481984d)]
    [InlineData("0x40000000000001", 18014398509481984d)]
    [InlineData("0b1000000000000000000000000000000000000000000000000000011", 18014398509481988d)]
    [InlineData("0o1000000000000000003", 18014398509481988d)]
    [InlineData("0x40000000000003", 18014398509481988d)]
    [InlineData("0b1000000000000000000000000000000000000000000000000000101", 18014398509481988d)]
    [InlineData("0o1000000000000000005", 18014398509481988d)]
    [InlineData("0x40000000000005", 18014398509481988d)]
    [InlineData("0b1000000000000000000000000000000000000000000000000000111", 18014398509481990d)]
    [InlineData("0o1000000000000000007", 18014398509481990d)]
    [InlineData("0x40000000000007", 18014398509481990d)]
    [InlineData("0b100000000000000000000000000000000000000000000000000011", 9007199254740996d)]
    [InlineData("0o400000000000000003", 9007199254740996d)]
    [InlineData("0x20000000000003", 9007199254740996d)]
    public async Task RadixNearestEvenCasesMatchRecordedJavaScriptNumber(string encoded, double expected)
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Metadata = new() { ["value"] = Document.FromObject(new { @string = encoded, type = "bigDecimal" }) } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        Assert.Equal(expected, Assert.Single(await store.SearchAsync("query")).Metadata!.Value.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task RadixIntegerRoundsOnceToNearestEvenRatherThanRoundingEveryPrefix()
    {
        using var runtime = new RuntimeClient();
        var encoded = "0b1" + new string('0', 52) + "11"; // 2^54 + 3; nearest representable integer is 2^54 + 4.
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Metadata = new() { ["value"] = Document.FromObject(new { @string = encoded, type = "bigDecimal" }) } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        Assert.Equal(18014398509481988d, Assert.Single(await store.SearchAsync("query")).Metadata!.Value.GetProperty("value").GetDouble());
    }
}
