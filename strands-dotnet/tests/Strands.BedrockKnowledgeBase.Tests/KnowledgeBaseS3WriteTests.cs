using System.Text;
using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Amazon.S3;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseS3WriteTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/literal//%2F")]
    public async Task UnsafeLeadingSlashRouteRejectsBeforeAnyPutOrIngestWithoutReconfiguration(string prefix)
    {
        using var fixture = new KnowledgeBaseLoopback(); using var s3 = fixture.S3(); using var agent = fixture.Agent();
        var config = (AmazonS3Config)s3.Config; var endpoint = config.ServiceURL; var pathStyle = config.ForcePathStyle;
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteProtocolTests.Options(s3, agent) with { S3 = new() { Bucket = "fixture-bucket", Prefix = prefix, Client = s3 } });
        var error = await Assert.ThrowsAsync<ArgumentException>(async () => await store.AddAsync("fact")); Assert.Contains("Leading-slash", error.Message); Assert.Empty(fixture.Requests); Assert.Equal(endpoint, config.ServiceURL); Assert.Equal(pathStyle, config.ForcePathStyle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachKnownUnsafeRouteIndependentlyRejectsLeadingSlash(bool pathStyle)
    {
        using var s3 = new S3TestClient(pathStyle ? new() { RegionEndpoint = Amazon.RegionEndpoint.USEast1, ForcePathStyle = true } : new() { ServiceURL = "http://127.0.0.1:1", AuthenticationRegion = "us-east-1", ForcePathStyle = false }); using var agent = new AgentClient();
        await using var store = new BedrockKnowledgeBaseStore(Options(s3, agent, ""));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.AddAsync("fact")); Assert.Equal(0, s3.Calls); Assert.Equal(0, agent.IngestCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("opaque//%2F")]
    [InlineData("opaque//%2F/")]
    [InlineData(" snow\\folder ")]
    public async Task RegionalVirtualHostRetainsOpaqueTypedKeyWithoutLiveAws(string prefix)
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); string? key = null; string? uploaded = null;
        s3.OnPut = (request, _) => { key = request.Key; Assert.False(request.AutoCloseStream); using var reader = new StreamReader(request.InputStream, Encoding.UTF8, false, 1024, true); uploaded = reader.ReadToEnd(); return Task.FromResult(new Amazon.S3.Model.PutObjectResponse()); };
        await using var store = new BedrockKnowledgeBaseStore(Options(s3, agent, prefix)); var result = await store.AddAsync(" Original 雪 ");
        var expectedPrefix = prefix.EndsWith("/", StringComparison.Ordinal) ? prefix : prefix + "/";
        Assert.StartsWith(expectedPrefix, key); Assert.Equal("s3://fixture-bucket/" + key, result.DocumentId); Assert.Equal(" Original 雪 ", uploaded); Assert.Equal(1, s3.Calls); Assert.Equal(1, agent.IngestCalls);
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript)]
    [InlineData(BedrockKnowledgeBaseProfile.Python)]
    public async Task EmptyAclAndUnsupportedMetadataCreateNoSidecar(BedrockKnowledgeBaseProfile profile)
    {
        using var fixture = new KnowledgeBaseLoopback(); using var s3 = fixture.S3(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteProtocolTests.Options(s3, agent) with { Profile = profile, AccessControlList = [] });
        using var metadata = JsonDocument.Parse("{\"nil\":null,\"empty\":[],\"nested\":{}}"); await store.AddAsync("fact", metadata.RootElement); Assert.Equal(2, fixture.Requests.Count);
        using var body = JsonDocument.Parse(fixture.Requests[1].Body); Assert.False(Assert.Single(body.RootElement.GetProperty("documents").EnumerateArray()).TryGetProperty("metadata", out _));
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript)]
    [InlineData(BedrockKnowledgeBaseProfile.Python)]
    public async Task AclOnlyCreatesSidecarWithEmptyMetadataObject(BedrockKnowledgeBaseProfile profile)
    {
        using var fixture = new KnowledgeBaseLoopback(); using var s3 = fixture.S3(); using var agent = fixture.Agent();
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseS3WriteProtocolTests.Options(s3, agent) with { Profile = profile, AccessControlList = [new() { Name = "reader", Type = new("GROUP"), Access = new("ALLOW") }] });
        await store.AddAsync("fact"); Assert.Equal(3, fixture.Requests.Count); using var sidecar = JsonDocument.Parse(fixture.Requests[1].Body); Assert.Empty(sidecar.RootElement.GetProperty("metadataAttributes").EnumerateObject()); Assert.Single(sidecar.RootElement.GetProperty("accessControlList").EnumerateArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EachFailedStepPreservesOriginalAndLeavesAcceptedUploadsWithoutCompensation(int step)
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); var primary = new IOException("independent failure"); var order = new List<string>();
        s3.OnPut = (request, _) => { order.Add(request.Key.EndsWith(".metadata.json", StringComparison.Ordinal) ? "sidecar" : "content"); if (order.Count == step) throw primary; return Task.FromResult(new Amazon.S3.Model.PutObjectResponse()); };
        agent.OnIngest = (_, _) => { order.Add("ingest"); throw primary; };
        await using var store = new BedrockKnowledgeBaseStore(Options(s3, agent) with { Scope = "tenant" });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await store.AddAsync("fact"))); Assert.Equal(new[] { "content", "sidecar", "ingest" }.Take(step), order); Assert.Equal(0, s3.Deletes); Assert.Equal(step == 3 ? 1 : 0, agent.IngestCalls);
    }

    [Fact]
    public async Task MissingAclIngestMappingDoesNotMapAnUploadFailure()
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); var primary = new ValidationException("Missing AccessControlList") { ErrorCode = "ValidationException" };
        s3.OnPut = (_, _) => throw primary; await using var store = new BedrockKnowledgeBaseStore(Options(s3, agent));
        Assert.Same(primary, await Assert.ThrowsAsync<ValidationException>(async () => await store.AddAsync("fact"))); Assert.Equal(0, agent.IngestCalls);
    }

    [Fact]
    public async Task S3IngestMissingAclKeepsExactOfficialCauseAfterUpload()
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); var primary = new ValidationException("Missing AccessControlList") { ErrorCode = "ValidationException" };
        agent.OnIngest = (_, _) => throw primary; await using var store = new BedrockKnowledgeBaseStore(Options(s3, agent));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AddAsync("fact")); Assert.Same(primary, actual.InnerException); Assert.Equal(1, s3.Calls); Assert.Equal(0, s3.Deletes);
    }

    [Fact]
    public async Task CompleteMetadataAndAclAreDetachedBeforeFirstUploadAwait()
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); var entered = TestSupport.Signal(); var release = TestSupport.Signal(); var payloads = new List<string>();
        s3.OnPut = async (request, _) => { using var reader = new StreamReader(request.InputStream, Encoding.UTF8, false, 1024, true); payloads.Add(reader.ReadToEnd()); if (payloads.Count == 1) { entered.TrySetResult(true); await release.Task; } return new(); };
        var acl = new DocumentAccessControlEntry { Name = "original", Type = new("GROUP"), Access = new("ALLOW") }; var entries = new List<DocumentAccessControlEntry> { acl };
        using var metadata = JsonDocument.Parse("{\"text\":\"original 雪\"}"); var options = Options(s3, agent) with { Scope = "tenant", AccessControlList = entries }; await using var store = new BedrockKnowledgeBaseStore(options); var write = store.AddAsync("fact", metadata.RootElement).AsTask();
        try { await TestSupport.Ready(entered.Task); metadata.Dispose(); acl.Name = "changed"; entries.Clear(); }
        finally { release.TrySetResult(true); await write; }
        using var sidecar = JsonDocument.Parse(payloads[1]); Assert.Equal("original 雪", sidecar.RootElement.GetProperty("metadataAttributes").GetProperty("text").GetProperty("value").GetProperty("stringValue").GetString()); Assert.Equal("original", Assert.Single(sidecar.RootElement.GetProperty("accessControlList").EnumerateArray()).GetProperty("Name").GetString());
    }

    [Fact]
    public void S3ConfigurationValidationIsLimitedToWritableS3()
    {
        using var s3 = new S3TestClient(); using var agent = new AgentClient(); var options = Options(s3, agent);
        Assert.Throws<ArgumentException>(() => new BedrockKnowledgeBaseStore(options with { S3 = null })); Assert.Throws<ArgumentException>(() => new BedrockKnowledgeBaseStore(options with { S3 = new() { Bucket = " ", Prefix = "x" } })); Assert.Throws<ArgumentNullException>(() => new BedrockKnowledgeBaseStore(options with { S3 = new() { Bucket = "bucket", Prefix = null! } }));
        using var readOnly = new BedrockKnowledgeBaseStore(options with { Writable = false, S3 = null }); Assert.False(readOnly.Writable); Assert.Equal(0, s3.Calls);
    }

    internal static BedrockKnowledgeBaseStoreOptions Options(IAmazonS3 s3, IAmazonBedrockAgent agent, string prefix = "memory") => KnowledgeBaseS3WriteProtocolTests.Options(s3, agent) with { S3 = new() { Bucket = "fixture-bucket", Prefix = prefix, Client = s3 } };
}
