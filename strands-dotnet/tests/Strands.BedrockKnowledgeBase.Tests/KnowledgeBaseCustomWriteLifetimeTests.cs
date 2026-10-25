using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.BedrockAgent.Model;
using Strands.Agents;
using Strands.Agents.BedrockKnowledgeBase;
using Strands.Agents.Memory;
using Agent = Strands.Agents.Agent;
using Message = Strands.Agents.Message;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseCustomWriteLifetimeTests
{
    [Fact]
    public async Task DisposalClosesAdmissionButPhysicallyJoinsAcceptedIngestion()
    {
        using var agent = new AgentClient(); var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        agent.OnIngest = async (_, token) => { entered.TrySetResult(true); await release.Task; token.ThrowIfCancellationRequested(); return new(); };
        var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(agent)); var write = store.AddAsync("fact").AsTask(); Task? close = null; Exception? primary = null; Exception? observed = null;
        try
        {
            await TestSupport.Ready(entered.Task); close = store.DisposeAsync().AsTask(); Assert.False(close.IsCompleted); Assert.False(write.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.AddAsync("late")); Assert.Equal(1, agent.IngestCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try
            {
                try { await write; }
                catch (Exception error) { observed = error; if (primary is null && error is not OperationCanceledException) throw; }
            }
            finally
            {
                try { await (close ?? store.DisposeAsync().AsTask()); }
                catch when (primary is not null) { }
            }
        }
        Assert.IsAssignableFrom<OperationCanceledException>(observed); Assert.Equal(0, agent.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationJoinsAndPreservesMatchingOrNormalizesMismatchedCause(bool matching)
    {
        using var caller = new CancellationTokenSource(); using var agent = new AgentClient(); var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        var primary = new OperationCanceledException("backend canceled", matching ? caller.Token : CancellationToken.None);
        agent.OnIngest = async (_, _) => { entered.TrySetResult(true); await release.Task; throw primary; };
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(agent)); var write = store.AddAsync("fact", cancellationToken: caller.Token).AsTask();
        Exception? primaryFailure = null;
        try { await TestSupport.Ready(entered.Task); caller.Cancel(); Assert.False(write.IsCompleted); }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try { await write; }
            catch (OperationCanceledException) { }
            catch when (primaryFailure is not null) { }
        }
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write); Assert.Equal(caller.Token, actual.CancellationToken);
        if (matching) Assert.Same(primary, actual); else Assert.Same(primary, actual.InnerException);
    }

    [Fact]
    public async Task IndependentFailureRemainsPrimaryWhenCallerCancellationRaces()
    {
        using var caller = new CancellationTokenSource(); using var agent = new AgentClient(); var primary = new IOException("independent ingestion failure");
        agent.OnIngest = (_, _) => { caller.Cancel(); return Task.FromException<IngestKnowledgeBaseDocumentsResponse>(primary); };
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(agent));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await store.AddAsync("fact", cancellationToken: caller.Token)));
    }

    [Fact]
    public async Task PrecancelledAdmissionDoesNotCreateAnyOwnedClient()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel(); var created = 0;
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(), () => { created++; throw new InvalidOperationException(); }, () => { created++; throw new InvalidOperationException(); });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AddAsync("fact", cancellationToken: caller.Token)); Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, created);
    }

    [Fact]
    public async Task SuccessfulSdkCompletionStillHonorsLateCallerCancellation()
    {
        using var caller = new CancellationTokenSource(); using var agent = new AgentClient();
        agent.OnIngest = (_, _) => { caller.Cancel(); return Task.FromResult(new IngestKnowledgeBaseDocumentsResponse()); };
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(agent));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.AddAsync("possibly accepted", cancellationToken: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(1, agent.IngestCalls);
    }

    [Fact]
    public async Task WriteOnlyStoreCreatesAndOwnsOnlyItsAgentClient()
    {
        var createdRuntime = 0; var createdAgent = 0; var agent = new AgentClient();
        var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(), () => { createdRuntime++; throw new InvalidOperationException(); }, () => { createdAgent++; return agent; });
        await store.AddAsync("fact"); await store.DisposeAsync(); await store.DisposeAsync();
        Assert.Equal(0, createdRuntime); Assert.Equal(1, createdAgent); Assert.Equal(1, agent.IngestCalls); Assert.Equal(0, agent.Calls); Assert.Equal(1, agent.Disposals);
    }

    [Fact]
    public async Task ProgrammaticToolAndExtractionWritesUseTheSameBorrowedBackend()
    {
        using var runtime = new RuntimeClient(); using var service = new AgentClient(); var contents = new List<string>();
        service.OnIngest = (request, _) => { contents.Add(request.Documents.Single().Content.Custom.InlineContent.TextContent.Data); return Task.FromResult(new IngestKnowledgeBaseDocumentsResponse()); };
        var extractor = new FixedExtractor();
        await using var store = new BedrockKnowledgeBaseStore(KnowledgeBaseCustomWriteTests.Options(service) with { RuntimeClient = runtime, KnownKnowledgeBaseType = new("MANAGED"), Extraction = new() { Extractor = extractor, Triggers = [new InvocationTrigger()] } });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null, AddTool = new() });
        await manager.AddAsync("manual");
        await using var agent = new Agent(new() { Model = new AnswerModel(), MemoryManager = manager });
        var tool = await agent.InvokeToolAsync(new("write", "add_memory", JsonSerializer.SerializeToElement(new { entries = new[] { "tool" } }))); Assert.NotEqual(ToolResultStatus.Error, tool.Status);
        await agent.InvokeAsync("remember"); await manager.FlushAsync();
        Assert.Equal(new[] { "manual", "tool", "extracted" }, contents); Assert.Equal(1, extractor.Calls);
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.ToReadOnly().AddAsync("denied")); Assert.Equal(3, service.IngestCalls);
        await agent.DisposeAsync(); Assert.Equal(0, service.Disposals); Assert.Equal(0, runtime.Disposals);
    }

    private sealed class FixedExtractor : IExtractor
    {
        internal int Calls;
        public ValueTask<IReadOnlyList<ExtractionResult>> ExtractAsync(IReadOnlyList<Message> messages, ExtractionContext? context = null, CancellationToken cancellationToken = default) { Calls++; return new(new[] { new ExtractionResult("extracted") }); }
    }
    private sealed class AnswerModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "answer"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
