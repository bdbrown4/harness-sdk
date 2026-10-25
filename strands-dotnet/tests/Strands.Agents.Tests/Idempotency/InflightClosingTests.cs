using Microsoft.Extensions.Logging;
using static Strands.Agents.Tests.Idempotency.InflightConformanceTests;
using static Strands.Agents.Tests.Idempotency.InflightIdempotencyTests;

namespace Strands.Agents.Tests.Idempotency;

// Reuses the SDK's existing process-wide logger isolation collection.
[Collection("SDK logging")]
public sealed class InflightClosingTests
{
    [Fact]
    public async Task ClosingProviderReportKeepsDuplicatesPendingWhileExistingSnapshotDirectAndClassicAdmissionRemainAvailable()
    {
        var original = SdkLogging.Logger; var logger = new BlockingLogger(); var provider = new UpdatingProvider(); var model = new ControlledModel();
        var tool = Tool("fixed", (_, _, _) => new("fixed"));
        var agent = new Agent(new() { Model = model, Tools = [tool], ToolProviders = [provider], EnableToolProviderUpdates = true });
        Task<AgentResult>? primary = null; Task<AgentResult>? duplicate = null;
        try
        {
            SdkLogging.ConfigureLogging(logger);
            primary = Task.Run(() => agent.InvokeAsync("primary", new() { IdempotencyToken = "closing" }));
            await Ready(model.Entered.Task); provider.Emit([Tool("fixed", (_, _, _) => new("collision"))]); model.Release.TrySetResult(true); await Ready(logger.Entered.Task);
            duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "closing" }); Assert.False(primary.IsCompleted); Assert.False(duplicate.IsCompleted);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("missing token"));
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("other token", new() { IdempotencyToken = "other" }));
            var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State }); await agent.LoadSnapshotAsync(snapshot);
            Assert.Equal("fixed", (await agent.InvokeToolAsync(new("direct", "fixed", ParseJson("{}")))).Content);
            // Exercise the exact existing admission lease; this is not a new receipt-wide lifecycle lock.
            using (agent.EnterClassicReduction(fromRegisteredCallback: false)) Assert.False(duplicate.IsCompleted);
        }
        finally
        {
            logger.Release.TrySetResult(true); model.Release.TrySetResult(true);
            try { if (primary is not null) await Observe(primary); if (duplicate is not null) await Observe(duplicate); await agent.DisposeAsync(); }
            finally { SdkLogging.ConfigureLogging(original); }
        }
        Assert.Same(await primary!, await duplicate!); Assert.Equal(1, model.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingShutdownOrDisposeCanBeginDuringClosingAndPhysicallyWaitForTheProviderReport(bool dispose)
    {
        var original = SdkLogging.Logger; var logger = new BlockingLogger(); var provider = new UpdatingProvider(); var model = new ControlledModel();
        var agent = new Agent(new() { Model = model, Tools = [Tool("fixed", (_, _, _) => new("fixed"))], ToolProviders = [provider], EnableToolProviderUpdates = true });
        Task<AgentResult>? primary = null; Task<AgentResult>? duplicate = null; Task? close = null;
        try
        {
            SdkLogging.ConfigureLogging(logger); primary = Task.Run(() => agent.InvokeAsync("primary", new() { IdempotencyToken = "lifecycle" }));
            await Ready(model.Entered.Task); provider.Emit([Tool("fixed", (_, _, _) => new("collision"))]); model.Release.TrySetResult(true); await Ready(logger.Entered.Task);
            duplicate = agent.InvokeAsync("accepted before close", new() { IdempotencyToken = "lifecycle" });
            close = dispose ? agent.DisposeAsync().AsTask() : agent.ShutdownAsync();
            Assert.False(close.IsCompleted); Assert.False(duplicate.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.InvokeAsync("too late", new() { IdempotencyToken = "lifecycle" }));
        }
        finally
        {
            logger.Release.TrySetResult(true); model.Release.TrySetResult(true);
            try { if (primary is not null) await Observe(primary); if (duplicate is not null) await Observe(duplicate); if (close is not null) await Observe(close); await agent.DisposeAsync(); }
            finally { SdkLogging.ConfigureLogging(original); }
        }
        await close!; Assert.Same(await primary!, await duplicate!); Assert.Equal(1, provider.Closes); Assert.Equal(1, provider.Removes);
    }

    [Fact]
    public async Task OldReceiptFinalizerCannotReleaseASeparateAbortThatAcquiredTheExistingClosingGap()
    {
        var paused = await PausedSnapshot();
        var original = SdkLogging.Logger; var logger = new BlockingLogger(); var provider = new UpdatingProvider(); var model = new ControlledModel();
        var abortEntered = Signal(); var abortRelease = Signal(); var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>(async (evt, _) =>
        {
            if (evt.Message.Content.OfType<ToolResultBlock>().Any()) { abortEntered.TrySetResult(true); await abortRelease.Task; }
        });
        var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = [Tool("fixed", (_, _, _) => new("fixed"))], ToolProviders = [provider], EnableToolProviderUpdates = true });
        Task<AgentResult>? primary = null; Task<AgentResult>? duplicate = null; Task<bool>? abort = null;
        try
        {
            SdkLogging.ConfigureLogging(logger); primary = Task.Run(() => agent.InvokeAsync("primary", new() { IdempotencyToken = "old" }));
            await Ready(model.Entered.Task); provider.Emit([Tool("fixed", (_, _, _) => new("collision"))]); model.Release.TrySetResult(true); await Ready(logger.Entered.Task);
            duplicate = agent.InvokeAsync("ignored", new() { IdempotencyToken = "old" });
            await agent.LoadSnapshotAsync(paused); abort = agent.AbortInterruptsAsync("settled without replay").AsTask(); await Ready(abortEntered.Task);
            logger.Release.TrySetResult(true); await primary; await duplicate;
            Assert.False(abort.IsCompleted);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("must not steal abort", new() { IdempotencyToken = "new" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State }).AsTask());
        }
        finally
        {
            logger.Release.TrySetResult(true); model.Release.TrySetResult(true); abortRelease.TrySetResult(true);
            try { if (primary is not null) await Observe(primary); if (duplicate is not null) await Observe(duplicate); if (abort is not null) await Observe(abort); await agent.DisposeAsync(); }
            finally { SdkLogging.ConfigureLogging(original); }
        }
        Assert.True(await abort!); Assert.Same(await primary!, await duplicate!); Assert.Equal(1, model.Calls);
    }

    private static async Task<AgentSnapshot> PausedSnapshot()
    {
        var model = new ControlledModel { ToolName = "approve" }; model.Release.TrySetResult(true);
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("approve", (_, context, _) => new(context.Interrupt("approval").ToString()))] });
        Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync("pause")).StopReason);
        return await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages | SnapshotFields.InterruptState });
    }

    private sealed class UpdatingProvider : IToolProviderUpdates
    {
        private IObserver<IReadOnlyList<ITool>>? _observer;
        internal int Closes, Removes;
        internal void Emit(IReadOnlyList<ITool> tools) => _observer!.OnNext(tools);
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => new(Array.Empty<ITool>());
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Removes++; return default; }
        public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default)
        { _observer = observer; observer.OnNext(Array.Empty<ITool>()); return new(new Lease(this)); }
        private sealed class Lease(UpdatingProvider owner) : IAsyncDisposable { public ValueTask DisposeAsync() { owner.Closes++; return default; } }
    }
    private sealed class BlockingLogger : ILogger
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal(); internal TaskCompletionSource<bool> Release { get; } = Signal();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).Contains("A tool provider update could not be published.")) return;
            Entered.TrySetResult(true); Release.Task.GetAwaiter().GetResult();
        }
    }
}
