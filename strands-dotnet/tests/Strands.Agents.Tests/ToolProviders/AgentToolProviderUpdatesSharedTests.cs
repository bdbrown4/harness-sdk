using System.Text.Json;
using static Strands.Agents.Tests.ToolProviders.AgentToolProviderUpdatesTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesSharedTests
{
    [Fact]
    public async Task SharedProviderAgentsAndChildRetainIndependentConsumerAuthority()
    {
        var initial = new FixedTool("initial"); var replacement = new FixedTool("replacement"); var provider = new SharedProvider([initial]);
        var parent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await using var sibling = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await using var child = parent.CreateChild(options => options with { ToolFilter = tool => !ReferenceEquals(tool, replacement) });
        await parent.InitializeAsync(); await sibling.InitializeAsync(); await child.InitializeAsync();
        try
        {
            Assert.Equal(3, provider.Contexts.Select(value => value.ConsumerId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(0, provider.Loads);
            provider.Emit(parent, [replacement]);
            Assert.Same(replacement, Assert.Single(parent.Tools));
            Assert.Same(initial, Assert.Single(sibling.Tools)); Assert.Same(initial, Assert.Single(child.Tools));
            provider.Emit(child, [replacement]); Assert.Empty(child.Tools);
            Assert.Same(replacement, Assert.Single(parent.Tools)); Assert.Same(initial, Assert.Single(sibling.Tools));
        }
        finally { await parent.DisposeAsync(); }
        Assert.Equal(1, provider.Removes);
        Assert.Equal("initial", (await sibling.InvokeToolAsync(new("call", "initial", JsonSerializer.SerializeToElement(new { })))).Content);
    }

    [Fact]
    public async Task DuplicateDeclarationsAreNotSilentlyDeduplicatedAndEveryLeaseIsClosed()
    {
        var initial = new BorrowedTool(); var provider = new SharedProvider([initial]);
        var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider, provider], EnableToolProviderUpdates = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Equal(2, provider.Watches); Assert.Equal(2, provider.Adds); Assert.Equal(2, provider.Removes); Assert.Equal(2, provider.Closes);
        Assert.Empty(agent.Tools); Assert.Equal(0, initial.Disposals);
        await agent.DisposeAsync(); Assert.Equal(2, provider.Removes); Assert.Equal(0, initial.Disposals);
    }

    [Fact]
    public async Task ActiveInitialPreparationKeepsTheExistingRejectingAgentCloseAdmission()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SharedProvider([new FixedTool("initial")]);
        var agent = new Agent(new()
        {
            Model = new TextModel(),
            ToolProviders = [provider],
            EnableToolProviderUpdates = true,
            ToolFilter = _ => { entered.TrySetResult(true); release.Task.GetAwaiter().GetResult(); return true; }
        });
        var initialization = Task.Run(() => agent.InitializeAsync()); await entered.Task;
        try
        {
            Assert.Throws<InvalidOperationException>(() => { _ = agent.ShutdownAsync(); });
            Assert.Throws<InvalidOperationException>(() => { _ = agent.DisposeAsync(); });
        }
        finally { release.TrySetResult(true); }
        await initialization; await agent.DisposeAsync(); Assert.Equal(1, provider.Closes); Assert.Equal(1, provider.Removes);
    }

    private sealed class SharedProvider(IReadOnlyList<ITool> initial) : IToolProviderUpdates
    {
        private readonly List<(ToolProviderContext Context, IObserver<IReadOnlyList<ITool>> Observer)> _scopes = [];
        internal List<ToolProviderContext> Contexts { get; } = [];
        internal int Adds;
        internal int Loads;
        internal int Watches;
        internal int Removes;
        internal int Closes;
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Adds++; return default; }
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Loads++; return new(initial); }
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Removes++; return default; }
        public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default)
        {
            Watches++; Contexts.Add(context); _scopes.Add((context, observer));
            observer.OnNext(initial);
            return new(new CloseLease(() => { Closes++; _scopes.RemoveAll(value => ReferenceEquals(value.Observer, observer)); }));
        }
        internal void Emit(Agent agent, IReadOnlyList<ITool> tools) => _scopes.Single(value => ReferenceEquals(value.Context.Agent, agent)).Observer.OnNext(tools);
    }
    private sealed class CloseLease(Action close) : IAsyncDisposable
    {
        private bool _closed;
        public ValueTask DisposeAsync() { if (!_closed) { _closed = true; close(); } return default; }
    }
    private sealed class BorrowedTool : ITool, IDisposable
    {
        internal int Disposals;
        public ToolSpec Spec { get; } = new("borrowed", "borrowed", JsonSerializer.SerializeToElement(new { type = "object" }));
        public void Dispose() => Disposals++;
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("borrowed");
    }
}
