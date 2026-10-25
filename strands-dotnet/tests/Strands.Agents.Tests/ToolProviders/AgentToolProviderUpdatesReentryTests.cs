using static Strands.Agents.Tests.ToolProviders.AgentToolProviderUpdatesTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesReentryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredExpiredContextCannotHideAnActiveProviderUpdate(bool shutdown)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new FixedTool("first"); var next = new FixedTool("next");
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        Agent? owner = null;
        Exception? refusal = null; Task? reentrant = null;
        CancellationTokenRegistration registration = default;
        owner = new(new()
        {
            Model = new TextModel(),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool =>
            {
                if (ReferenceEquals(tool, first))
                    registration = cancellation.Token.Register(() =>
                        refusal = Record.Exception(() => { reentrant = shutdown ? owner!.ShutdownAsync() : owner!.DisposeAsync().AsTask(); }));
                if (ReferenceEquals(tool, next)) cancellation.Cancel();
                return true;
            }
        });
        var agent = owner;
        try
        {
            await agent.InitializeAsync();
            provider.Emit([first]);
            provider.Emit([next]);
            Assert.IsType<InvalidOperationException>(refusal);
            Assert.Null(reentrant);
            Assert.Same(next, Assert.Single(agent.Tools));
        }
        finally
        {
            registration.Dispose();
            if (reentrant is not null) await reentrant;
            await agent.DisposeAsync();
        }
        Assert.Equal(1, provider.Lease.Disposals);
        Assert.Equal(1, provider.Removes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredExpiredContextCannotReenterProviderLeaseCleanup(bool shutdown)
    {
        using var cancellation = new CancellationTokenSource();
        var first = new FixedTool("first");
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        Agent? owner = null;
        Exception? refusal = null; Task? reentrant = null;
        CancellationTokenRegistration registration = default;
        owner = new(new()
        {
            Model = new TextModel(),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool =>
            {
                if (ReferenceEquals(tool, first))
                    registration = cancellation.Token.Register(() =>
                        refusal = Record.Exception(() => { reentrant = shutdown ? owner!.ShutdownAsync() : owner!.DisposeAsync().AsTask(); }));
                return true;
            }
        });
        var agent = owner;
        try
        {
            await agent.InitializeAsync();
            provider.Emit([first]);
            provider.Lease.BeforeDispose = () => { cancellation.Cancel(); return default; };
            await agent.DisposeAsync();
            Assert.IsType<InvalidOperationException>(refusal);
            Assert.Null(reentrant);
        }
        finally { registration.Dispose(); await agent.DisposeAsync(); }
        Assert.Equal(1, provider.Lease.Disposals);
        Assert.Equal(1, provider.Removes);
    }

    [Fact]
    public async Task NestedPreparationProtectsBothOwnersAndPermitsAnIndependentAgent()
    {
        var outerTool = new FixedTool("outer"); var innerTool = new FixedTool("inner");
        var outerProvider = new UpdatingProvider([new FixedTool("initial")]);
        var innerProvider = new UpdatingProvider([new FixedTool("initial")]);
        await using var independent = new Agent(new() { Model = new TextModel() });
        Agent? outerOwner = null; Agent? innerOwner = null;
        Exception? outerRefusal = null; Exception? innerRefusal = null; Exception? independentFailure = null;
        Task? independentDisposal = null;
        outerOwner = new(new()
        {
            Model = new TextModel(),
            ToolProviders = [outerProvider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool => { if (ReferenceEquals(tool, outerTool)) innerProvider.Emit([innerTool]); return true; }
        });
        innerOwner = new(new()
        {
            Model = new TextModel(),
            ToolProviders = [innerProvider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool =>
            {
                if (ReferenceEquals(tool, innerTool))
                {
                    outerRefusal = Record.Exception(() => { _ = outerOwner.DisposeAsync(); });
                    innerRefusal = Record.Exception(() => { _ = innerOwner!.DisposeAsync(); });
                    independentFailure = Record.Exception(() => { independentDisposal = independent.DisposeAsync().AsTask(); });
                }
                return true;
            }
        });
        await using var outer = outerOwner;
        await using var inner = innerOwner;
        await independent.InitializeAsync(); await outer.InitializeAsync(); await inner.InitializeAsync();
        outerProvider.Emit([outerTool]);
        Assert.IsType<InvalidOperationException>(outerRefusal);
        Assert.IsType<InvalidOperationException>(innerRefusal);
        Assert.Null(independentFailure); Assert.NotNull(independentDisposal);
        await independentDisposal!;
        Assert.Same(outerTool, Assert.Single(outer.Tools)); Assert.Same(innerTool, Assert.Single(inner.Tools));
    }

    [Fact]
    public async Task EscapedPreparationTaskCanDisposeAfterTheCallbackReturns()
    {
        var outside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new FixedTool("next"); var provider = new UpdatingProvider([new FixedTool("initial")]);
        Agent? owner = null; Task? escaped = null;
        owner = new(new()
        {
            Model = new TextModel(),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool =>
            {
                if (ReferenceEquals(tool, next)) escaped = Task.Run(async () => { await outside.Task; await owner!.DisposeAsync(); });
                return true;
            }
        });
        await using var agent = owner;
        await agent.InitializeAsync(); provider.Emit([next]);
        Assert.Same(next, Assert.Single(agent.Tools)); Assert.NotNull(escaped);
        outside.TrySetResult(true); await escaped!;
        Assert.Equal(1, provider.Lease.Disposals); Assert.Equal(1, provider.Removes);
    }
}
