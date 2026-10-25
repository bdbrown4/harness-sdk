using System.Globalization;
using System.Text.Json;
using Amazon.BedrockAgent;
using Amazon.BedrockAgent.Model;
using Strands.Agents.BedrockKnowledgeBase;
using Strands.Agents.Memory;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseCustomWriteTests
{
    [Theory]
    [InlineData(null, "source")]
    [InlineData("", "source")]
    [InlineData("custom", "source")]
    [InlineData("OTHER", "source")]
    [InlineData("S3", "source")]
    [InlineData("CUSTOM", null)]
    [InlineData("CUSTOM", "")]
    public void InvalidWritableConfigurationCannotCreateAClient(string? type, string? source)
    {
        var created = 0;
        Assert.Throws<ArgumentException>(() => new BedrockKnowledgeBaseStore(Options() with { DataSourceType = type is null ? null : new(type), DataSourceId = source }, () => { created++; throw new InvalidOperationException(); }, () => { created++; throw new InvalidOperationException(); }));
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task ReadonlyDefaultsAndManagerRoutingRejectWritesWithoutIngestion()
    {
        using var runtime = new RuntimeClient(); using var agent = new AgentClient();
        await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { RuntimeClient = runtime, Writable = false, DataSourceType = null, DataSourceId = null });
        Assert.False(store.Writable); Assert.Null(store.Extraction);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.AddAsync("fact"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((IWritableMemoryStore)store).AddAsync("fact"));
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.AddAsync("fact"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.AddAsync("fact", new() { Stores = [store.Name] }));
        Assert.Equal(0, agent.IngestCalls); Assert.Equal(0, agent.Calls); Assert.Equal(0, runtime.Calls);
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, "", true)]
    [InlineData(BedrockKnowledgeBaseProfile.Python, " \r\n\t", true)]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, "\ufeff", true)]
    [InlineData(BedrockKnowledgeBaseProfile.Python, "\ufeff", false)]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, "\u001c", false)]
    [InlineData(BedrockKnowledgeBaseProfile.Python, "\u001c", true)]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, "\u0085", false)]
    [InlineData(BedrockKnowledgeBaseProfile.Python, "\u0085", true)]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, "  fact\n", false)]
    [InlineData(BedrockKnowledgeBaseProfile.Python, "  fact\n", false)]
    public async Task SourceWhitespaceValidationNeverTrimsAcceptedContent(BedrockKnowledgeBaseProfile profile, string content, bool blank)
    {
        using var agent = new AgentClient(); string? accepted = null;
        agent.OnIngest = (request, _) => { accepted = request.Documents.Single().Content.Custom.InlineContent.TextContent.Data; return Task.FromResult(new IngestKnowledgeBaseDocumentsResponse()); };
        await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { Profile = profile });
        if (blank) await Assert.ThrowsAsync<ArgumentException>(async () => await store.AddAsync(content)); else await store.AddAsync(content);
        Assert.Equal(blank ? null : content, accepted); Assert.Equal(blank ? 0 : 1, agent.IngestCalls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("\"metadata\"")]
    public async Task NonobjectMetadataFailsBeforeAnyRequest(string json)
    {
        using var agent = new AgentClient(); await using var store = new BedrockKnowledgeBaseStore(Options(agent)); using var metadata = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.AddAsync("fact", metadata.RootElement)); Assert.Equal(0, agent.IngestCalls);
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript)]
    [InlineData(BedrockKnowledgeBaseProfile.Python)]
    public async Task NonemptyInlineAclRequiresSupportedAttributes(BedrockKnowledgeBaseProfile profile)
    {
        using var agent = new AgentClient(); await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { Profile = profile, AccessControlList = [new() { Name = "reader", Access = new("ALLOW"), Type = new("USER") }] });
        using var metadata = JsonDocument.Parse("{\"unsupported\":{},\"empty\":[]}");
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.AddAsync("fact", metadata.RootElement)); Assert.Equal(0, agent.IngestCalls);
    }

    [Fact]
    public async Task ConfigurationAndCompleteMetadataAreDetachedBeforeIngestAwait()
    {
        using var agent = new AgentClient(); var entered = TestSupport.Signal(); var release = TestSupport.Signal(); IngestKnowledgeBaseDocumentsRequest? accepted = null;
        agent.OnIngest = async (request, _) => { accepted = request; entered.TrySetResult(true); await release.Task; return new(); };
        var acl = new DocumentAccessControlEntry { Name = "original", Type = new("future"), Access = new("ACCESS") }; var entries = new List<DocumentAccessControlEntry> { acl };
        await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { AccessControlList = entries });
        acl.Name = "changed"; acl.Type = new("changed"); entries.Clear();
        var metadata = JsonDocument.Parse("{\"labels\":[\"one\",\"two\"],\"truth\":true}"); var write = store.AddAsync("fact", metadata.RootElement).AsTask(); metadata.Dispose();
        Exception? primaryFailure = null;
        try
        {
            await TestSupport.Ready(entered.Task); var document = Assert.Single(accepted!.Documents); var projected = Assert.Single(document.Metadata.AccessControlList);
            Assert.Equal("original", projected.Name); Assert.Equal("future", projected.Type.Value); Assert.Equal("ACCESS", projected.Access.Value);
            Assert.Equal(new[] { "one", "two" }, document.Metadata.InlineAttributes[0].Value.StringListValue); Assert.True(document.Metadata.InlineAttributes[1].Value.BooleanValue);
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try { await write; }
            catch when (primaryFailure is not null) { }
        }
    }

    [Theory]
    [InlineData(true, "ValidationException", "missing ACCESSCONTROLLIST", false, true)]
    [InlineData(true, "ValidationException", "missing access control list", false, false)]
    [InlineData(true, "validationexception", "missing accessControlList", false, false)]
    [InlineData(true, "Other", "missing accessControlList", false, false)]
    [InlineData(false, "ValidationException", "missing accessControlList", false, false)]
    [InlineData(true, "ValidationException", "missing accessControlList", true, false)]
    public async Task MissingAclRemapRequiresExactServiceShapeAndAbsentConfiguration(bool typed, string code, string message, bool emptyAcl, bool remap)
    {
        using var agent = new AgentClient(); Exception primary = typed ? new ValidationException(message) { ErrorCode = code } : new AmazonBedrockAgentException(message) { ErrorCode = code };
        agent.OnIngest = async (_, _) => { await Task.Yield(); throw primary; };
        await using var store = new BedrockKnowledgeBaseStore(Options(agent) with { Scope = "tenant", AccessControlList = emptyAcl ? [] : null });
        var actual = await Record.ExceptionAsync(async () => await store.AddAsync("fact"));
        if (remap) { var mapped = Assert.IsType<InvalidOperationException>(actual); Assert.Same(primary, mapped.InnerException); Assert.Contains("AccessControlList", mapped.Message); }
        else Assert.Same(primary, actual);
        Assert.Contains(nameof(MissingAclRemapRequiresExactServiceShapeAndAbsentConfiguration), primary.StackTrace);
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript)]
    [InlineData(BedrockKnowledgeBaseProfile.Python)]
    public void GeneratedIdentifiersHaveSourceVersionVariantRangeAndUniqueHandles(BedrockKnowledgeBaseProfile profile)
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var identifiers = Enumerable.Range(0, 256).Select(_ => KnowledgeBaseWriteDocument.NewId(profile)).ToArray(); var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(256, identifiers.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in identifiers)
        {
            Assert.True(Guid.TryParseExact(id, "D", out _)); Assert.Equal(profile == BedrockKnowledgeBaseProfile.TypeScript ? '7' : '4', id[14]); Assert.Contains(id[19], "89ab");
            if (profile == BedrockKnowledgeBaseProfile.TypeScript) Assert.InRange(long.Parse(id.Substring(0, 8) + id.Substring(9, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture), before, after);
        }
    }

    internal static BedrockKnowledgeBaseStoreOptions Options(IAmazonBedrockAgent? agent = null) => new() { Name = "knowledge", KnowledgeBaseId = "KB12345678", AgentClient = agent, Writable = true, DataSourceType = ContentDataSourceType.CUSTOM, DataSourceId = "DS12345678" };
}
