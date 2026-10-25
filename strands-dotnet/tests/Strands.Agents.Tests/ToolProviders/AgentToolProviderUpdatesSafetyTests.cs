using System.Text.Json;
using static Strands.Agents.Tests.ToolProviders.AgentToolProviderUpdatesTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesSafetyTests
{
    [Fact]
    public async Task AWholeReplacementCannotPartiallyOverwriteForeignRegistrations()
    {
        var original = new FixedTool("initial"); var foreign = new FixedTool("foreign");
        var provider = new UpdatingProvider([original]);
        await using var agent = new Agent(new() { Model = new TextModel(), Tools = [foreign], ToolProviders = [provider], EnableToolProviderUpdates = true });
        await agent.InitializeAsync();
        provider.Emit([new FixedTool("new"), new FixedTool("foreign")]);
        Assert.Equal(new ITool[] { foreign, original }, agent.Tools);
        Assert.Equal("initial", (await agent.InvokeToolAsync(Call("initial"))).Content);
    }

    [Fact]
    public async Task SameCapabilityCannotAcquireAnotherProvidersFrozenMetadataAuthority()
    {
        var first = new FixedTool("first"); var shared = new MutableTool("second");
        var provider = new UpdatingProvider([first]); var other = new UpdatingProvider([shared]);
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider, other], EnableToolProviderUpdates = true });
        await agent.InitializeAsync();
        shared.Spec = Spec("renamed");
        provider.Emit([shared]);
        Assert.Equal(new ITool[] { first, shared }, agent.Tools);
        Assert.Equal("second", agent.RegisteredToolSpec(shared).Name);
        Assert.Same(other, agent.RegisteredToolProvider(shared));
        Assert.Equal("original body", (await agent.InvokeToolAsync(Call("second"))).Content);
    }

    [Fact]
    public async Task FilteredCapabilitiesAreNeverReadAndIncomingMembershipIsDetached()
    {
        var original = new FixedTool("initial"); var excluded = new ThrowingSpecTool();
        var provider = new UpdatingProvider([original]);
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true, ToolFilter = tool => !ReferenceEquals(tool, excluded) });
        await agent.InitializeAsync();
        var included = new FixedTool("included"); var membership = new List<ITool> { excluded, included };
        provider.Emit(membership); membership.Clear();
        Assert.Same(included, Assert.Single(agent.Tools)); Assert.Equal(0, excluded.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalNotificationDiscardsAnUnpublishedBusyBatch(bool failed)
    {
        var model = new GatedModel(); var original = new FixedTool("initial"); var provider = new UpdatingProvider([original]);
        await using var agent = new Agent(new() { Model = model, ToolProviders = [provider], EnableToolProviderUpdates = true });
        var invocation = agent.InvokeAsync("run"); await model.Entered.Task;
        try
        {
            provider.Emit([new FixedTool("unpublished")]);
            if (failed) provider.Observer!.OnError(new IOException("source ended")); else provider.Observer!.OnCompleted();
            provider.Emit([new FixedTool("late")]);
        }
        finally { model.Release.TrySetResult(true); }
        await invocation;
        Assert.Same(original, Assert.Single(agent.Tools));
    }

    [Fact]
    public async Task ValidInitialSnapshotCanCompleteBeforeItsLeaseReturns()
    {
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        provider.BeforeReturn = () => provider.Observer!.OnCompleted();
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await agent.InitializeAsync(); Assert.Equal("initial", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task InitialTerminalFailurePreservesItsCauseAndJoinsReturnedLease()
    {
        var primary = new IOException("initial stream failed"); var provider = new UpdatingProvider([new FixedTool("initial")]);
        provider.BeforeReturn = () => provider.Observer!.OnError(primary);
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Equal(1, provider.Lease.Disposals); Assert.Equal(1, provider.Removes); Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task MissingInitialSnapshotWaitsForPhysicalProvisionalCleanup()
    {
        var entered = Signal(); var release = Signal(); var provider = new UpdatingProvider([]) { NoInitial = true };
        provider.Lease.BeforeDispose = async () => { entered.TrySetResult(true); await release.Task; };
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        var initialization = agent.InitializeAsync(); await entered.Task;
        try { Assert.False(initialization.IsCompleted); Assert.Equal(0, provider.Removes); }
        finally { release.TrySetResult(true); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => initialization);
        Assert.Equal(1, provider.Removes);
    }

    [Fact]
    public async Task CancellationAfterInitialDeliveryStillClosesTheLeaseBeforeFailure()
    {
        using var cancellation = new CancellationTokenSource(); var provider = new UpdatingProvider([new FixedTool("initial")]);
        provider.BeforeReturn = cancellation.Cancel;
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InitializeAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(1, provider.Lease.Disposals); Assert.Equal(1, provider.Removes); Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task ExternalDisposalJoinsABlockedRefreshFilterAndRefusesLaterWork()
    {
        var entered = Signal(); var release = Signal(); var original = new FixedTool("initial"); var next = new FixedTool("next");
        var provider = new UpdatingProvider([original]);
        var agent = new Agent(new()
        {
            Model = new TextModel(),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool => { if (ReferenceEquals(tool, next)) { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); } return true; }
        });
        await agent.InitializeAsync();
        var delivery = Task.Run(() => provider.Emit([next]));
        await entered.Task; var disposal = agent.DisposeAsync().AsTask();
        try { Assert.False(disposal.IsCompleted); Assert.Equal(0, provider.Removes); }
        finally { release.TrySetResult(true); }
        await delivery; await disposal;
        var late = new ThrowingSpecTool(); provider.Emit([late]);
        Assert.Equal(0, late.Reads); Assert.Same(original, Assert.Single(agent.Tools)); Assert.Equal(1, provider.Removes);
    }

    [Fact]
    public async Task OwnLiveFilterLifecycleWaitFailsFastButExpiredReceiptIsIndependent()
    {
        Exception? refusal = null; ExecutionContext? escaped = null; Agent? owner = null;
        var original = new FixedTool("initial"); var next = new FixedTool("next"); var provider = new UpdatingProvider([original]);
        owner = new(new()
        {
            Model = new TextModel(),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool =>
            {
                if (ReferenceEquals(tool, next))
                {
                    refusal = Record.Exception(() => owner!.DisposeAsync());
                    escaped = ExecutionContext.Capture();
                }
                return true;
            }
        });
        await using var agent = owner;
        await agent.InitializeAsync(); provider.Emit([next]);
        Assert.IsType<InvalidOperationException>(refusal); Assert.NotNull(escaped);
        Task? disposal = null;
        ExecutionContext.Run(escaped!, _ => disposal = agent.DisposeAsync().AsTask(), null);
        await disposal!; Assert.Equal(1, provider.Removes);
    }

    [Fact]
    public async Task OlderPreparedSnapshotCannotOverwriteANewerAcceptedSequence()
    {
        var entered = Signal(); var release = Signal(); var older = new FixedTool("older"); var newer = new FixedTool("newer");
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        await using var agent = new Agent(new()
        {
            Model = new TextModel(),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            ToolFilter = tool => { if (ReferenceEquals(tool, older)) { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); } return true; }
        });
        await agent.InitializeAsync();
        var first = Task.Run(() => provider.Emit([older])); await entered.Task;
        try { provider.Emit([newer]); Assert.Same(newer, Assert.Single(agent.Tools)); }
        finally { release.TrySetResult(true); }
        await first; Assert.Same(newer, Assert.Single(agent.Tools));
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ToolUseBlock Call(string name) => new("call", name, JsonSerializer.SerializeToElement(new { }));
    private static ToolSpec Spec(string name) => new(name, name, JsonSerializer.SerializeToElement(new { type = "object" }));
    private sealed class MutableTool(string name) : ITool
    {
        public ToolSpec Spec { get; set; } = AgentToolProviderUpdatesSafetyTests.Spec(name);
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("original body");
    }
    private sealed class ThrowingSpecTool : ITool
    {
        internal int Reads;
        public ToolSpec Spec { get { Reads++; throw new IOException("Excluded specification must not be read."); } }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
