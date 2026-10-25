using System.Text.Json;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime.Documents;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseNumberCompatibilityTests
{
    [Theory]
    [InlineData("1\0", false, 0)]
    [InlineData("0.84551240822557006", true, 0.8455124082255701)]
    [InlineData("9007199254740993", true, 9007199254740992d)]
    [InlineData("18014398509481987", true, 18014398509481988d)]
    public async Task DecimalLexicalAndNearestDoubleSemanticsMatchFiniteNumber(string encoded, bool converted, double expected)
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Metadata = new() { ["value"] = Document.FromObject(new { @string = encoded, type = "bigDecimal" }) } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        var value = Assert.Single(await store.SearchAsync("query")).Metadata!.Value.GetProperty("value");
        Assert.Equal(converted ? JsonValueKind.Number : JsonValueKind.Object, value.ValueKind);
        if (converted) Assert.Equal(expected, value.GetDouble());
    }
}
