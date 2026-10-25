using System.Runtime.CompilerServices;
using Amazon.BedrockAgentRuntime.Model;
using Amazon.Runtime.Documents;
using Strands.Agents;
using Strands.Agents.BedrockKnowledgeBase;
using Strands.Agents.Memory;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseCompositionTests
{
    [Fact]
    public async Task ManagerLimitThreeStoreOverrideAndDirectTenRemainIndependent()
    {
        using var runtime = new RuntimeClient();
        var limits = new List<int?>();
        runtime.OnRetrieve = (request, _) => { limits.Add(request.RetrievalConfiguration.ManagedSearchConfiguration.NumberOfResults); return Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Content = new() { Text = "fact" } }] }); };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await store.SearchAsync("query"); Assert.Equal(10, limits.Last());
        Assert.Equal("knowledge", Assert.Single(await manager.SearchAsync("query")).StoreName); Assert.Equal(3, limits.Last());
        await manager.SearchAsync("query", new() { MaxSearchResults = 7 }); Assert.Equal(7, limits.Last());
        await using var configured = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { MaxSearchResults = 5 });
        await configured.SearchAsync("query"); Assert.Equal(5, limits.Last());
        await new MemoryManager(new() { Stores = [configured], Injection = null }).SearchAsync("query"); Assert.Equal(5, limits.Last());
        Assert.False(store.Writable); Assert.True(typeof(IWritableMemoryStore).IsAssignableFrom(store.GetType())); Assert.Null(store.Extraction);
    }

    [Fact]
    public async Task IndependentReadonlyManagersShareTypeCacheAndInitializeBeforeModel()
    {
        using var runtime = new RuntimeClient(); using var detection = new AgentClient(); var modelCalls = 0;
        runtime.OnRetrieve = (_, _) => Task.FromResult(new RetrieveResponse { RetrievalResults = [new() { Content = new() { Text = "fact" } }] });
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { KnownKnowledgeBaseType = null, AgentClient = detection });
        var manager = new MemoryManager(new() { Stores = [store] });
        var model = new TextModel(() => { Assert.Equal(1, detection.Calls); Assert.True(runtime.Calls > 0); modelCalls++; });
        await using (var first = new Agent(new() { Model = model, Plugins = [manager] })) await first.InvokeAsync("query");
        await using (var second = new Agent(new() { Model = model, Plugins = [manager.ToReadOnly()] })) await second.InvokeAsync("query again");
        Assert.Equal(2, modelCalls); Assert.Equal(1, detection.Calls); Assert.Equal(0, detection.Disposals); Assert.Equal(0, runtime.Disposals);
    }

    [Theory]
    [InlineData(BedrockKnowledgeBaseProfile.TypeScript, "")]
    [InlineData(BedrockKnowledgeBaseProfile.Python, "namespace")]
    public async Task WhitespaceScopeIsNotTrimmedAndEmptyScopeKeyUsesSourcePolicy(BedrockKnowledgeBaseProfile profile, string key)
    {
        using var runtime = new RuntimeClient();
        runtime.OnRetrieve = (request, _) => { var filter = request.RetrievalConfiguration.ManagedSearchConfiguration.Filter; Assert.Equal(key, filter.Equals.Key); Assert.Equal(" ", filter.Equals.Value.AsString()); return Task.FromResult(new RetrieveResponse()); };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { Profile = profile, Scope = " ", ScopeMetadataKey = "" });
        await store.SearchAsync("query");
    }

    [Fact]
    public async Task CyclicDocumentIsRejectedBeforeAnyClientCanBeCreated()
    {
        var document = Document.FromObject(new Dictionary<string, object>());
        document.AsDictionary()["self"] = document;
        using var runtime = new RuntimeClient();
        Assert.Throws<ArgumentException>(() => new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { Filter = new() { Equals = new() { Key = "cyclic", Value = document } } }));
        Assert.Equal(0, runtime.Calls);
    }

    private sealed class TextModel(Action onCall) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            onCall(); await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
