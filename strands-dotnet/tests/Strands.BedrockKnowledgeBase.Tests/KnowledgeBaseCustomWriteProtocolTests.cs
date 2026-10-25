using System.Globalization;
using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseCustomWriteProtocolTests
{
    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, '7')]
    [InlineData(BedrockKnowledgeBaseProfile.Python, '4')]
    public async Task RealCustomIngestPreservesOriginalTextAndReturnsItsExactProfileIdentifier(BedrockKnowledgeBaseProfile profile, char version)
    {
        using var fixture = new KnowledgeBaseLoopback(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { Profile = profile });
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = await store.AddAsync("  Original content 雪\n");
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.True(Guid.TryParseExact(result.DocumentId, "D", out _)); Assert.Equal(version, result.DocumentId[14]); Assert.Contains(result.DocumentId[19], "89ab");
        if (profile == BedrockKnowledgeBaseProfile.TypeScript) Assert.InRange(long.Parse(result.DocumentId.Substring(0, 8) + result.DocumentId.Substring(9, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture), before, after);
        var request = Assert.Single(fixture.Requests); Assert.Equal("PUT", request.Method); Assert.Contains("/datasources/DS12345678/documents", request.Target);
        using var body = JsonDocument.Parse(request.Body); var document = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray());
        var content = document.GetProperty("content"); Assert.Equal("CUSTOM", content.GetProperty("dataSourceType").GetString()); var custom = content.GetProperty("custom");
        Assert.Equal(result.DocumentId, custom.GetProperty("customDocumentIdentifier").GetProperty("id").GetString()); Assert.Equal("IN_LINE", custom.GetProperty("sourceType").GetString());
        Assert.Equal("TEXT", custom.GetProperty("inlineContent").GetProperty("type").GetString()); Assert.Equal("  Original content 雪\n", custom.GetProperty("inlineContent").GetProperty("textContent").GetProperty("data").GetString());
        Assert.False(document.TryGetProperty("metadata", out _)); Assert.True(store.Writable);
    }

    [Fact]
    public async Task RealCustomMetadataUsesTypedAttributesScopeAuthorityAndLowercaseAclMembers()
    {
        using var fixture = new KnowledgeBaseLoopback(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { Scope = "tenant", AccessControlList = [new() { Name = "reader", Type = new("future-principal"), Access = new("future-access") }] });
        using var metadata = JsonDocument.Parse("{\"namespace\":\"forged\",\"text\":\"\",\"amount\":1.25,\"enabled\":false,\"labels\":[\"a\",\"b\"],\"empty\":[],\"mixed\":[\"a\",2],\"nil\":null,\"object\":{}}");
        await store.AddAsync("fact", metadata.RootElement);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Requests).Body); var doc = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray()); var value = doc.GetProperty("metadata");
        Assert.Equal("IN_LINE_ATTRIBUTE", value.GetProperty("type").GetString()); var attrs = value.GetProperty("inlineAttributes").EnumerateArray().ToArray(); Assert.Equal(["namespace", "text", "amount", "enabled", "labels"], attrs.Select(x => x.GetProperty("key").GetString()!).ToArray());
        Assert.Equal("tenant", attrs[0].GetProperty("value").GetProperty("stringValue").GetString()); Assert.Equal("STRING", attrs[1].GetProperty("value").GetProperty("type").GetString());
        Assert.Equal(1.25, attrs[2].GetProperty("value").GetProperty("numberValue").GetDouble()); Assert.False(attrs[3].GetProperty("value").GetProperty("booleanValue").GetBoolean()); Assert.Equal("STRING_LIST", attrs[4].GetProperty("value").GetProperty("type").GetString());
        var acl = Assert.Single(value.GetProperty("accessControlList").EnumerateArray()); Assert.Equal("reader", acl.GetProperty("name").GetString()); Assert.Equal("future-principal", acl.GetProperty("type").GetString()); Assert.Equal("future-access", acl.GetProperty("access").GetString());
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, false)]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, true)]
    [InlineData(BedrockKnowledgeBaseProfile.Python, false)]
    [InlineData(BedrockKnowledgeBaseProfile.Python, true)]
    public async Task EmptyAclPresenceHasSourceSpecificRealWireShape(BedrockKnowledgeBaseProfile profile, bool scope)
    {
        using var fixture = new KnowledgeBaseLoopback(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { Profile = profile, Scope = scope ? "tenant" : null, AccessControlList = [] });
        if (!scope && profile == BedrockKnowledgeBaseProfile.TypeScript)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.AddAsync("fact")); Assert.Empty(fixture.Requests); return;
        }
        await store.AddAsync("fact");
        using var body = JsonDocument.Parse(Assert.Single(fixture.Requests).Body); var doc = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray());
        Assert.Equal(scope, doc.TryGetProperty("metadata", out var metadata));
        if (scope)
        {
            Assert.Equal(profile == BedrockKnowledgeBaseProfile.TypeScript, metadata.TryGetProperty("accessControlList", out var acl));
            if (profile == BedrockKnowledgeBaseProfile.TypeScript) Assert.Empty(acl.EnumerateArray());
        }
    }

    [Fact]
    public async Task RealValidationResponseRetainsItsOfficialExceptionAsTheRemapCause()
    {
        using var fixture = new KnowledgeBaseLoopback { Respond = request => request.Target.Contains("/datasources/") ? new("ValidationException", "Missing ACCESSCONTROLLIST on this source.") : null };
        using var agent = fixture.Agent(); await using var store = new BedrockKnowledgeBaseStore(Options(agent));
        var mapped = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AddAsync("fact")); var original = Assert.IsType<ValidationException>(mapped.InnerException);
        Assert.Equal("ValidationException", original.ErrorCode); Assert.Contains("ACCESSCONTROLLIST", original.Message); Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task BorrowedOfficialAgentRemainsUsableAfterSuccessfulWriteAndStoreDisposal()
    {
        using var fixture = new KnowledgeBaseLoopback(); using var agent = fixture.Agent(); var config = agent.Config;
        var store = new BedrockKnowledgeBaseStore(Options(agent)); await store.AddAsync("fact"); await store.DisposeAsync();
        await agent.GetKnowledgeBaseAsync(new() { KnowledgeBaseId = "KB12345678" });
        Assert.Same(config, agent.Config); Assert.Equal(2, fixture.Requests.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.AddAsync("late"));
    }
    private static BedrockKnowledgeBaseStoreOptions Options(IAmazonBedrockAgent agent) => new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", AgentClient = agent, Writable = true, DataSourceType = ContentDataSourceType.CUSTOM, DataSourceId = "DS12345678" };
}
