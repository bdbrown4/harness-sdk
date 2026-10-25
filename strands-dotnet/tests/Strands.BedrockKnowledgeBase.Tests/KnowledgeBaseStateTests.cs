using Amazon.BedrockAgent;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime.Documents;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseStateTests
{
    [Fact]
    public async Task ConcurrentInitializationPublishesOneValidType()
    {
        using var runtime = new RuntimeClient(); using var agent = new AgentClient();
        var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        agent.OnGet = async (_, _) => { entered.TrySetResult(true); await release.Task; return AgentClient.Type("MANAGED"); };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { KnownKnowledgeBaseType = null, AgentClient = agent });
        var tasks = Enumerable.Range(0, 8).Select(_ => store.InitializeAsync().AsTask()).ToArray();
        try { await TestSupport.Ready(entered.Task); Assert.Equal(1, agent.Calls); Assert.All(tasks, task => Assert.False(task.IsCompleted)); }
        finally { release.TrySetResult(true); }
        await Task.WhenAll(tasks);
        await store.InitializeAsync(); Assert.Equal(1, agent.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledDetectionCanRetryWithoutPublishingPartialType(bool cancel)
    {
        using var runtime = new RuntimeClient(); using var agent = new AgentClient(); using var caller = new CancellationTokenSource();
        var failure = new IOException("detection failed");
        agent.OnGet = (_, _) => { if (cancel) { caller.Cancel(); return Task.FromResult(AgentClient.Type("MANAGED")); } throw failure; };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { KnownKnowledgeBaseType = null, AgentClient = agent });
        if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.InitializeAsync(caller.Token)); Assert.Equal(caller.Token, error.CancellationToken);
        }
        else Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await store.InitializeAsync()));
        agent.OnGet = (_, _) => Task.FromResult(AgentClient.Type("MANAGED"));
        await store.InitializeAsync(); await store.InitializeAsync();
        Assert.Equal(2, agent.Calls);
    }

    [Fact]
    public async Task MissingTypeIsNotCachedAndAValidRetrySucceeds()
    {
        using var runtime = new RuntimeClient(); using var agent = new AgentClient();
        agent.OnGet = (_, _) => Task.FromResult(new Amazon.BedrockAgent.Model.GetKnowledgeBaseResponse());
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { KnownKnowledgeBaseType = null, AgentClient = agent });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.InitializeAsync());
        agent.OnGet = (_, _) => Task.FromResult(AgentClient.Type("VECTOR")); await store.InitializeAsync();
        Assert.Equal(2, agent.Calls);
    }

    [Theory]
    [InlineData("MANAGED", true)]
    [InlineData("managed", false)]
    [InlineData("FUTURE_TYPE", false)]
    public async Task ExplicitKnownTypeUsesOrdinalValueWithoutConstantClassCaseFolding(string type, bool managed)
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (request, _) => { Assert.Equal(managed, request.RetrievalConfiguration.ManagedSearchConfiguration is not null); Assert.Equal(!managed, request.RetrievalConfiguration.VectorSearchConfiguration is not null); return Task.FromResult(new RetrieveResponse()); };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { KnownKnowledgeBaseType = new(type) });
        await store.SearchAsync("query"); Assert.Equal(1, runtime.Calls);
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, false, "")]
    [InlineData(BedrockKnowledgeBaseProfile.Python, true, "namespace")]
    public async Task EmptyFilterAndScopeKeyFollowExplicitSourceProfile(BedrockKnowledgeBaseProfile profile, bool scopeFilter, string key)
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (request, _) =>
        {
            var filter = request.RetrievalConfiguration.ManagedSearchConfiguration.Filter;
            Assert.NotNull(filter); Assert.Equal(scopeFilter, filter.Equals is not null);
            if (scopeFilter) { Assert.Equal(key, filter.Equals!.Key); Assert.Equal("tenant", filter.Equals.Value.AsString()); }
            return Task.FromResult(new RetrieveResponse());
        };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { Profile = profile, Scope = "tenant", ScopeMetadataKey = "", Filter = new() });
        await store.SearchAsync("query");
    }

    [Fact]
    public async Task FilterGraphAndDocumentsDetachBeforeAnyDetectionAwaitAndBetweenRequests()
    {
        using var runtime = new RuntimeClient(); using var agent = new AgentClient();
        var nested = new Dictionary<string, Document> { ["value"] = Document.FromObject("original") };
        var attribute = new FilterAttribute { Key = "custom", Value = Document.FromObject(nested) };
        var filter = new RetrievalFilter { AndAll = [new() { Equals = attribute }] };
        var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        agent.OnGet = async (_, _) => { entered.TrySetResult(true); await release.Task; return AgentClient.Type("MANAGED"); };
        runtime.OnRetrieve = (request, _) =>
        {
            var item = request.RetrievalConfiguration.ManagedSearchConfiguration.Filter.AndAll.Single().Equals;
            Assert.Equal("custom", item.Key); Assert.Equal("original", item.Value.AsDictionary()["value"].AsString());
            item.Key = "request mutated"; item.Value.AsDictionary().Clear();
            return Task.FromResult(new RetrieveResponse());
        };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { AgentClient = agent, KnownKnowledgeBaseType = null, Filter = filter, Scope = "ignored" });
        var search = store.SearchAsync("query").AsTask();
        try { await TestSupport.Ready(entered.Task); nested.Clear(); attribute.Key = "changed"; filter.AndAll.Clear(); }
        finally { release.TrySetResult(true); }
        await search; await store.SearchAsync("again"); Assert.Equal(2, runtime.Calls);
    }

    [Fact]
    public async Task InvalidConfigurationAndFilterCyclesFailBeforeClientCreation()
    {
        var created = 0;
        Func<Amazon.BedrockAgentRuntime.IAmazonBedrockAgentRuntime> factory = () => { created++; return new RuntimeClient(); };
        var valid = new BedrockKnowledgeBaseStoreOptions { Name = "knowledge", KnowledgeBaseId = "KB12345678" };
        Assert.Throws<ArgumentException>(() => new BedrockKnowledgeBaseStore(valid with { Name = " " }, factory, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockKnowledgeBaseStore(valid with { MaxSearchResults = 0 }, factory, null));
        Assert.Throws<ArgumentException>(() => new BedrockKnowledgeBaseStore(valid with { MinScore = 0, MaxScore = 0 }, factory, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BedrockKnowledgeBaseStore(valid with { Profile = (BedrockKnowledgeBaseProfile)99 }, factory, null));
        var cycle = new RetrievalFilter(); cycle.AndAll = [cycle];
        Assert.Throws<ArgumentException>(() => new BedrockKnowledgeBaseStore(valid with { Filter = cycle }, factory, null));
        Assert.Equal(0, created);
        await using var store = new BedrockKnowledgeBaseStore(valid, factory, null);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.SearchAsync("query", 0));
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.InitializeAsync(caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, created);
    }
}
