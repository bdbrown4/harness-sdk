using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.S3;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseS3WriteProtocolTests
{
    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, '7')]
    [InlineData(BedrockKnowledgeBaseProfile.Python, '4')]
    public async Task RealS3ContentUploadsBeforeIngestWithExactOriginalTextAndProfileHandle(BedrockKnowledgeBaseProfile profile, char version)
    {
        using var fixture = new KnowledgeBaseLoopback(); using var s3 = fixture.S3(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(Options(s3, agent) with { Profile = profile });
        var result = await store.AddAsync("  Original content 雪\n");
        var requests = fixture.Requests; Assert.Equal(2, requests.Count); Assert.Equal("PUT", requests[0].Method); Assert.Equal("  Original content 雪\n", requests[0].Body); Assert.Equal("text/plain; charset=utf-8", requests[0].Headers["Content-Type"]);
        Assert.StartsWith("s3://fixture-bucket/literal//%2F/", result.DocumentId); var id = result.DocumentId.Substring(result.DocumentId.LastIndexOf('/') + 1); Assert.EndsWith(".txt", id); Assert.True(Guid.TryParseExact(id.Substring(0, id.Length - 4), "D", out _)); Assert.Equal(version, id[14]);
        Assert.Equal("/fixture-bucket/literal//%252F/" + id, requests[0].Target); Assert.Contains("/datasources/", requests[1].Target);
        using var body = JsonDocument.Parse(requests[1].Body); var doc = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray()); Assert.Equal("S3", doc.GetProperty("content").GetProperty("dataSourceType").GetString()); Assert.Equal(result.DocumentId, doc.GetProperty("content").GetProperty("s3").GetProperty("s3Location").GetProperty("uri").GetString()); Assert.False(doc.TryGetProperty("metadata", out _));
    }

    [Fact]
    public async Task RealS3SidecarContainsTypedMetadataCapitalizedAclAndPrecedesIngestion()
    {
        using var fixture = new KnowledgeBaseLoopback(); using var s3 = fixture.S3(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(Options(s3, agent) with { Scope = "tenant", AccessControlList = [new() { Name = "reader", Type = new("GROUP"), Access = new("ALLOW") }] });
        using var metadata = JsonDocument.Parse("{\"namespace\":\"forged\",\"amount\":1.25,\"enabled\":false,\"labels\":[\"a\",\"b\"],\"empty\":[]}");
        var result = await store.AddAsync("fact", metadata.RootElement); var requests = fixture.Requests; Assert.Equal(3, requests.Count); Assert.Equal("fact", requests[0].Body); Assert.Equal(requests[0].Target + ".metadata.json", requests[1].Target); Assert.Equal("application/json", requests[1].Headers["Content-Type"]);
        using var sidecar = JsonDocument.Parse(requests[1].Body); var attrs = sidecar.RootElement.GetProperty("metadataAttributes"); Assert.Equal("tenant", attrs.GetProperty("namespace").GetProperty("value").GetProperty("stringValue").GetString()); Assert.False(attrs.GetProperty("namespace").GetProperty("includeForEmbedding").GetBoolean()); Assert.Equal("NUMBER", attrs.GetProperty("amount").GetProperty("value").GetProperty("type").GetString()); Assert.False(attrs.GetProperty("enabled").GetProperty("value").GetProperty("booleanValue").GetBoolean()); Assert.False(attrs.TryGetProperty("empty", out _));
        var acl = Assert.Single(sidecar.RootElement.GetProperty("accessControlList").EnumerateArray()); Assert.Equal("reader", acl.GetProperty("Name").GetString()); Assert.Equal("GROUP", acl.GetProperty("Type").GetString()); Assert.Equal("ALLOW", acl.GetProperty("Access").GetString()); Assert.False(acl.TryGetProperty("name", out _));
        using var body = JsonDocument.Parse(requests[2].Body); var doc = Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray()); Assert.Equal("S3_LOCATION", doc.GetProperty("metadata").GetProperty("type").GetString()); Assert.Equal(result.DocumentId + ".metadata.json", doc.GetProperty("metadata").GetProperty("s3Location").GetProperty("uri").GetString());
    }

    internal static BedrockKnowledgeBaseStoreOptions Options(IAmazonS3 s3, IAmazonBedrockAgent agent) => new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", AgentClient = agent, Writable = true, DataSourceType = ContentDataSourceType.S3, DataSourceId = "DS12345678", S3 = new() { Bucket = "fixture-bucket", Prefix = "literal//%2F", Client = s3 } };
}
