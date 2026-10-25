using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgentRuntime;
using Amazon.Runtime;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseProtocolTests
{
    [Fact]
    public async Task OfficialUnmarshallerCanonicalizesKnownLowercaseServiceTypeBeforeNativeSelection()
    {
        using var fixture = new KnowledgeBaseLoopback("managed"); using var runtime = fixture.Runtime(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", RuntimeClient = runtime, AgentClient = agent });
        await store.SearchAsync("find");
        using var body = JsonDocument.Parse(fixture.Requests[1].Body);
        Assert.True(body.RootElement.GetProperty("retrievalConfiguration").TryGetProperty("managedSearchConfiguration", out _));
    }

    [Fact]
    public async Task KnownManagedTypeRetrievesActualWireResultsAndDirectLimitTen()
    {
        using var fixture = new KnowledgeBaseLoopback();
        using var runtime = fixture.Runtime();
        await using var store = new BedrockKnowledgeBaseStore(new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", KnownKnowledgeBaseType = KnowledgeBaseType.MANAGED, RuntimeClient = runtime });
        var result = await store.SearchAsync("find");
        Assert.Equal("fact", Assert.Single(result).Content);
        var request = Assert.Single(fixture.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(10, body.RootElement.GetProperty("retrievalConfiguration").GetProperty("managedSearchConfiguration").GetProperty("numberOfResults").GetInt32());
    }

    [Fact]
    public async Task DetectionUsesRealGetOnceAndUnknownTypeUsesVectorSearch()
    {
        using var fixture = new KnowledgeBaseLoopback();
        using var runtime = fixture.Runtime(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", RuntimeClient = runtime, AgentClient = agent });
        await store.InitializeAsync(); await store.InitializeAsync();
        Assert.Equal("fact", Assert.Single(await store.SearchAsync("find", 2)).Content);
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Equal("GET", fixture.Requests[0].Method);
        using var body = JsonDocument.Parse(fixture.Requests[1].Body);
        Assert.Equal(2, body.RootElement.GetProperty("retrievalConfiguration").GetProperty("vectorSearchConfiguration").GetProperty("numberOfResults").GetInt32());
    }

    [Fact]
    public async Task PresentEmptyTypeScriptFilterWinsOverScopeOnTheWire()
    {
        using var fixture = new KnowledgeBaseLoopback(); using var runtime = fixture.Runtime();
        await using var store = new BedrockKnowledgeBaseStore(new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", RuntimeClient = runtime, KnownKnowledgeBaseType = KnowledgeBaseType.MANAGED, Scope = "tenant", Filter = new() });
        await store.SearchAsync("find");
        using var body = JsonDocument.Parse(Assert.Single(fixture.Requests).Body);
        var filter = body.RootElement.GetProperty("retrievalConfiguration").GetProperty("managedSearchConfiguration").GetProperty("filter");
        Assert.Empty(filter.EnumerateObject());
    }
}
