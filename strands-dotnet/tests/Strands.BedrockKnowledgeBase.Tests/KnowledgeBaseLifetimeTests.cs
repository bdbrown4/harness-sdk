using Amazon.BedrockAgentRuntime.Model;
using Strands.Agents.BedrockKnowledgeBase;

namespace Strands.BedrockKnowledgeBase.Tests;

public sealed class KnowledgeBaseLifetimeTests
{
    [Fact]
    public async Task DisposalClosesAdmissionAndPhysicallyJoinsAnUncooperativeAcceptedRequest()
    {
        using var runtime = new RuntimeClient(); var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        runtime.OnRetrieve = async (_, _) => { entered.TrySetResult(true); await release.Task; return new(); };
        var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        var request = store.SearchAsync("query").AsTask(); Task? close = null;
        try
        {
            await TestSupport.Ready(entered.Task); close = store.DisposeAsync().AsTask();
            Assert.False(close.IsCompleted); Assert.False(request.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await store.SearchAsync("later"));
            Assert.Equal(0, runtime.Disposals);
        }
        finally { release.TrySetResult(true); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        await close!; await store.DisposeAsync(); Assert.Equal(0, runtime.Disposals);
    }

    [Fact]
    public async Task CallerCancellationPhysicallyJoinsSuccessfulIoAndRetainsCallerToken()
    {
        using var runtime = new RuntimeClient(); using var caller = new CancellationTokenSource();
        var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        runtime.OnRetrieve = async (_, _) => { entered.TrySetResult(true); await release.Task; return new(); };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        var request = store.SearchAsync("query", caller.Token).AsTask();
        try { await TestSupport.Ready(entered.Task); caller.Cancel(); Assert.False(request.IsCompleted); }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(1, runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationPreservesMatchingOceOrNormalizesOnlyMismatchedOce(bool matching)
    {
        using var runtime = new RuntimeClient(); using var caller = new CancellationTokenSource();
        var primary = new OperationCanceledException("sdk cancellation", matching ? caller.Token : CancellationToken.None);
        runtime.OnRetrieve = (_, _) => { caller.Cancel(); throw primary; };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.SearchAsync("query", caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        if (matching) Assert.Same(primary, error); else Assert.Same(primary, error.InnerException);
    }

    [Fact]
    public async Task IndependentServiceFailureRemainsPrimaryEvenWhenCallerCancellationRaces()
    {
        using var runtime = new RuntimeClient(); using var caller = new CancellationTokenSource(); var failure = new IOException("service primary");
        runtime.OnRetrieve = (_, _) => { caller.Cancel(); throw failure; };
        await using var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await store.SearchAsync("query", caller.Token)));
    }

    [Fact]
    public async Task BorrowedClientsAndConfigurationRemainUsableAfterStoreDisposal()
    {
        using var runtime = new RuntimeClient(); using var agent = new AgentClient();
        var configuration = runtime.Config;
        var store = new BedrockKnowledgeBaseStore(TestSupport.Options(runtime) with { AgentClient = agent, KnownKnowledgeBaseType = null, Region = "us-west-2" });
        await store.SearchAsync("query"); await store.DisposeAsync();
        await runtime.RetrieveAsync(new()); await agent.GetKnowledgeBaseAsync(new());
        Assert.Same(configuration, runtime.Config); Assert.Equal(0, runtime.Disposals); Assert.Equal(0, agent.Disposals);
    }

    [Fact]
    public async Task OwnedClientsAreLazyAndEveryCreatedClientIsClosedOnceEvenWhenCleanupFails()
    {
        var runtime = new RuntimeClient(); var agent = new AgentClient(); var created = 0;
        var options = new BedrockKnowledgeBaseStoreOptions { Name = "knowledge", KnowledgeBaseId = "KB12345678" };
        var unused = new BedrockKnowledgeBaseStore(options, () => { created++; return runtime; }, () => { created++; return agent; });
        await unused.DisposeAsync(); Assert.Equal(0, created);
        var store = new BedrockKnowledgeBaseStore(options, () => { created++; return runtime; }, () => { created++; return agent; });
        await store.SearchAsync("query");
        var primary = new IOException("runtime cleanup"); var cleanup = new IOException("agent cleanup");
        runtime.OnDispose = () => throw primary; agent.OnDispose = () => throw cleanup;
        var first = store.DisposeAsync().AsTask(); var second = store.DisposeAsync().AsTask();
        Assert.Same(first, second); Assert.Same(primary, await Assert.ThrowsAsync<IOException>(async () => await first));
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal(2, created); Assert.Equal(1, runtime.Disposals); Assert.Equal(1, agent.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveSdkCallbackCannotJoinItsOwnDisposalEvenWhenExecutionFlowIsSuppressed(bool suppress)
    {
        using var runtime = new RuntimeClient(); BedrockKnowledgeBaseStore? store = null; Exception? observed = null;
        runtime.OnRetrieve = (_, _) =>
        {
            if (suppress) { using (ExecutionContext.SuppressFlow()) observed = Record.Exception(() => store!.Dispose()); }
            else observed = Record.Exception(() => { _ = store!.DisposeAsync(); });
            return Task.FromResult(new RetrieveResponse());
        };
        store = new(TestSupport.Options(runtime)); await store.SearchAsync("query"); Assert.IsType<InvalidOperationException>(observed); await store.DisposeAsync();
    }

    [Fact]
    public async Task OwnedCleanupAndTokenCallbacksCannotJoinDisposal()
    {
        var runtime = new RuntimeClient(); BedrockKnowledgeBaseStore? store = null;
        Exception? tokenObserved = null; Exception? cleanupObserved = null;
        var entered = TestSupport.Signal(); var release = TestSupport.Signal();
        runtime.OnRetrieve = async (_, token) =>
        {
            using var registration = token.Register(() => tokenObserved = Record.Exception(() => store!.Dispose()));
            entered.TrySetResult(true); await release.Task; return new();
        };
        runtime.OnDispose = () => cleanupObserved = Record.Exception(() => { _ = store!.DisposeAsync(); });
        var options = TestSupport.Options(runtime) with { RuntimeClient = null };
        store = new(options, () => runtime, null);
        var request = store.SearchAsync("query").AsTask();
        Exception? primary = null;
        OperationCanceledException? cancellation = null;
        try
        {
            await TestSupport.Ready(entered.Task);
            var close = store.DisposeAsync().AsTask();
            Assert.IsType<InvalidOperationException>(tokenObserved); Assert.False(close.IsCompleted);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try
            {
                try { await request; }
                catch (OperationCanceledException error) { cancellation = error; }
                catch (Exception cleanup)
                {
                    if (primary is null) { primary = cleanup; throw; }
                    primary.Data["RequestJoinError"] = cleanup;
                }
            }
            finally
            {
                try { await store.DisposeAsync(); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["StoreJoinError"] = cleanup; }
            }
        }
        Assert.NotNull(cancellation); Assert.IsType<InvalidOperationException>(cleanupObserved); Assert.Equal(1, runtime.Disposals);
    }

    [Fact]
    public async Task EscapedExpiredReceiptDoesNotPreventLaterExternalDisposal()
    {
        using var runtime = new RuntimeClient(); BedrockKnowledgeBaseStore? store = null; Task? escaped = null;
        var release = TestSupport.Signal();
        runtime.OnRetrieve = (_, _) => { escaped = Task.Run(async () => { await release.Task; await store!.DisposeAsync(); }); return Task.FromResult(new RetrieveResponse()); };
        store = new(TestSupport.Options(runtime)); await store.SearchAsync("query");
        release.TrySetResult(true); await TestSupport.Ready(escaped!);
    }
}
