using System.Runtime.CompilerServices;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AContextPoolTests
{
    [Fact]
    public async Task ConcurrentFactoriesCannotTransferTheSameInitializingAgentToTwoContexts()
    {
        var gate = new InitializationGate();
        var model = new Model();
        var shared = new Agent(new() { Model = model, OwnsModel = true, Plugins = [gate] });
        await using var pool = new A2AContextPool(new() { AgentFactory = (_, _) => new(shared) });
        var first = pool.AcquireAsync("first", default).AsTask();
        await A2AWireTests.WithDeadline(gate.Entered.Task);
        var second = pool.AcquireAsync("second", default).AsTask();
        gate.Release.TrySetResult(true);
        using var firstLease = await first;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => { using var unexpected = await second; });

        Assert.Contains("independent agents", error.Message);
        Assert.Equal(0, model.Disposals);
        firstLease.Dispose();
        await pool.DisposeAsync();
        Assert.Equal(1, model.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnershipIsExplicitAndExecutorDisposalIsIdempotent(bool owns)
    {
        var model = new Model();
        var agent = new Agent(new() { Model = model, OwnsModel = true });
        var pool = new A2AContextPool(new() { AgentFactory = (_, _) => new(agent), OwnsAgents = owns });
        using (await pool.AcquireAsync("context", default)) { }

        await pool.DisposeAsync(); await pool.DisposeAsync();

        Assert.Equal(owns ? 1 : 0, model.Disposals);
        if (!owns) { await agent.InvokeAsync("still borrowed"); await agent.DisposeAsync(); }
        Assert.Equal(1, model.Disposals);
    }
    [Fact]
    public async Task CapacityRejectsActiveContextsAndEvictsTheOldestInactiveOwnedAgent()
    {
        var models = new Dictionary<string, Model>();
        await using var pool = new A2AContextPool(new()
        {
            MaxContexts = 1,
            AgentFactory = (id, _) => { var model = new Model(); models.Add(id, model); return new(new Agent(new() { Model = model, OwnsModel = true })); }
        });
        using var first = await pool.AcquireAsync("first", default);
        var capacity = await Assert.ThrowsAsync<A2ARpcException>(async () => { using var unexpected = await pool.AcquireAsync("second", default); });
        Assert.Equal(-32000, capacity.Code);
        Assert.Single(models);

        first.Dispose();
        using var second = await pool.AcquireAsync("second", default);

        Assert.Equal(1, models["first"].Disposals);
        Assert.Equal(0, models["second"].Disposals);
    }
    [Fact]
    public async Task CancellingAQueuedAcquisitionCannotReleaseTheCurrentExecutionLease()
    {
        await using var pool = new A2AContextPool(new() { MaxContexts = 1, AgentFactory = (_, _) => new(new Agent(new() { Model = new Model() })) });
        using var first = await pool.AcquireAsync("context", default);
        using var cancellation = new CancellationTokenSource();
        var waiting = pool.AcquireAsync("context", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var unexpected = await waiting; });
        await Assert.ThrowsAsync<A2ARpcException>(async () => { using var unexpected = await pool.AcquireAsync("different", default); });
        first.Dispose();
        using var next = await pool.AcquireAsync("different", default);
    }
    [Fact]
    public async Task FailedInitializationReleasesTheSlotAndPreservesOwnedCleanupDiagnostics()
    {
        var primary = new InvalidOperationException("initialize");
        var cleanup = new IOException("dispose model");
        var firstModel = new Model { DisposeError = cleanup };
        var factories = 0;
        await using var pool = new A2AContextPool(new()
        {
            MaxContexts = 1,
            AgentFactory = (_, _) => new(new Agent(new() { Model = Interlocked.Increment(ref factories) == 1 ? firstModel : new Model(), OwnsModel = true, Plugins = factories == 1 ? [new FailingPlugin(primary)] : [] }))
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => { using var unexpected = await pool.AcquireAsync("context", default); });

        Assert.Same(primary, error);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, firstModel.Disposals);
        using var recovered = await pool.AcquireAsync("context", default);
        Assert.Equal(2, factories);
    }

    private sealed class InitializationGate : IPlugin
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "initialization-gate";
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(true); await Release.Task; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class Model : IModel, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        internal Exception? DisposeError { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask; yield return new ModelMessageStartEvent(); yield return new ModelMessageStopEvent(StopReasons.EndTurn); }
        public ValueTask DisposeAsync() { Disposals++; if (DisposeError is { } error) throw error; return default; }
    }
    private sealed class FailingPlugin(Exception error) : IPlugin
    {
        public string Name => "failing";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => throw error;
        public IReadOnlyList<ITool> GetTools() => [];
    }
}
