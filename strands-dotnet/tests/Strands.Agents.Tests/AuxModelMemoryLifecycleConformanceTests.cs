using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests;

public sealed class AuxModelMemoryLifecycleConformanceTests
{
    [Fact]
    public async Task SelectionBeforeInitializationBecomesTheMemoryExtractionDefault()
    {
        var primary = new RecordingModel("ready");
        var original = Facts("original fact");
        var replacement = Facts("replacement fact");
        var store = new EntryStore();
        var manager = Manager(store);
        await using var agent = new Agent(new() { Model = primary, AuxModel = original, MemoryManager = manager });

        agent.SetAuxModel(replacement);
        await agent.InitializeAsync();
        await agent.InvokeAsync("remember this preference");
        Assert.Empty(store.Entries);
        await manager.FlushAsync();

        Assert.Equal(["replacement fact"], store.Entries);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, original.Calls);
        Assert.Equal(1, replacement.Calls);
        Assert.Equal(1, replacement.IteratorDisposals);
        Assert.Same(replacement, agent.AuxModel);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task InitializedExtractionRetainsItsCapturedDefaultAfterSelectionChanges(bool configured, bool reset)
    {
        var primary = Facts("primary fact");
        var original = Facts("original fact");
        var replacement = Facts("replacement fact");
        var store = new EntryStore();
        var manager = Manager(store);
        await using var agent = new Agent(new() { Model = primary, AuxModel = configured ? original : null, MemoryManager = manager });
        await agent.InitializeAsync();

        agent.SetAuxModel(reset ? null : replacement);
        await agent.InvokeAsync("remember the captured model");
        await manager.FlushAsync();

        Assert.Equal([configured ? "original fact" : "primary fact"], store.Entries);
        Assert.Equal(configured ? 1 : 2, primary.Calls);
        Assert.Equal(configured ? 1 : 0, original.Calls);
        Assert.Equal(0, replacement.Calls);
        Assert.Equal(configured ? 1 : 2, primary.IteratorDisposals);
        Assert.Equal(configured ? 1 : 0, original.IteratorDisposals);
        Assert.Same(reset ? primary : replacement, agent.AuxModel);
    }

    [Fact]
    public async Task ExplicitExtractorModelStillOverridesTheCapturedAndCurrentAuxiliaryModels()
    {
        var primary = new RecordingModel("ready");
        var original = Facts("original fact");
        var replacement = Facts("replacement fact");
        var extractorModel = Facts("explicit fact");
        var store = new EntryStore(new ModelExtractor(new() { Model = extractorModel }));
        var manager = Manager(store);
        await using var agent = new Agent(new() { Model = primary, AuxModel = original, MemoryManager = manager });
        await agent.InitializeAsync();

        agent.SetAuxModel(replacement);
        agent.SetAuxModel(null);
        await agent.InvokeAsync("remember explicit extraction");
        await manager.FlushAsync();

        Assert.Equal(["explicit fact"], store.Entries);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, original.Calls);
        Assert.Equal(0, replacement.Calls);
        Assert.Equal(1, extractorModel.Calls);
        Assert.Equal(1, extractorModel.IteratorDisposals);
        Assert.Same(primary, agent.AuxModel);
    }

    [Fact]
    public async Task ReassignmentDoesNotRetargetAcceptedOrLaterBatchesOfAnInitializedExtractor()
    {
        var release = Signal();
        var primary = new RecordingModel("ready");
        var original = Facts("captured fact", release.Task);
        var replacement = Facts("replacement fact");
        var store = new EntryStore();
        var manager = Manager(store);
        var agent = new Agent(new() { Model = primary, AuxModel = original, MemoryManager = manager });
        Task? flush = null;
        Exception? failure = null;
        try
        {
            await agent.InvokeAsync("first batch");
            flush = manager.FlushAsync().AsTask();
            await Within(original.Entered.Task);
            Assert.False(flush.IsCompleted);

            agent.SetAuxModel(replacement);
            Assert.Same(replacement, agent.AuxModel);
            Assert.Empty(store.Entries);
            release.TrySetResult(true);
            await Within(flush);
            Assert.Equal(["captured fact"], store.Entries);
            Assert.Equal(1, original.IteratorDisposals);

            await agent.InvokeAsync("second batch");
            await manager.FlushAsync();
            Assert.Equal(["captured fact", "captured fact"], store.Entries);
            Assert.Equal(2, original.Calls);
            Assert.Equal(2, original.IteratorDisposals);
            Assert.Equal(0, replacement.Calls);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            release.TrySetResult(true);
            await Finish(failure, () => flush ?? Task.CompletedTask, () => agent.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task ShutdownRejectsReassignmentBeforeCallbacksFinishAndAfterCompletion()
    {
        var entered = Signal();
        var release = Signal();
        var primary = new RecordingModel("ready");
        var original = Facts("original fact");
        var replacement = Facts("replacement fact");
        var agent = new Agent(new() { Model = primary, AuxModel = original });
        using var hook = agent.Hooks.Register<ShutdownEvent>(async (_, _) =>
        {
            entered.TrySetResult(true);
            await release.Task;
        });
        Task? shutdown = null;
        Exception? failure = null;
        try
        {
            shutdown = agent.ShutdownAsync();
            await Within(entered.Task);
            Assert.False(shutdown.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => agent.SetAuxModel(replacement));
            Assert.Same(original, agent.AuxModel);

            release.TrySetResult(true);
            await Within(shutdown);
            Assert.Throws<ObjectDisposedException>(() => agent.SetAuxModel(null));
            Assert.Same(original, agent.AuxModel);
            Assert.Equal(0, primary.Disposals);
            Assert.Equal(0, original.Disposals);
            Assert.Equal(0, replacement.Disposals);
            Assert.Equal(0, primary.Calls + original.Calls + replacement.Calls);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            release.TrySetResult(true);
            await Finish(failure, () => shutdown ?? Task.CompletedTask, () => agent.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposalRejectsReassignmentAndDoesNotAcquireBorrowedSelections()
    {
        var resource = new PendingResource();
        var primary = new RecordingModel("ready");
        var original = Facts("original fact");
        var replacement = Facts("replacement fact");
        var agent = new Agent(new() { Model = primary, AuxModel = original, OwnsModel = true, OwnedResources = [resource] });
        agent.SetAuxModel(replacement);
        Task? disposal = null;
        Exception? failure = null;
        try
        {
            disposal = agent.DisposeAsync().AsTask();
            await Within(resource.Entered.Task);
            Assert.False(disposal.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => agent.SetAuxModel(original));
            Assert.Same(replacement, agent.AuxModel);
            Assert.Equal(0, primary.Disposals);

            resource.Release.TrySetResult(true);
            await Within(disposal);
            Assert.Throws<ObjectDisposedException>(() => agent.SetAuxModel(null));
            Assert.Same(replacement, agent.AuxModel);
            await agent.DisposeAsync();
            Assert.Equal(1, resource.Disposals);
            Assert.Equal(1, primary.Disposals);
            Assert.Equal(0, original.Disposals);
            Assert.Equal(0, replacement.Disposals);
            Assert.Equal(0, primary.Calls + original.Calls + replacement.Calls);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            resource.Release.TrySetResult(true);
            await Finish(failure, () => disposal ?? Task.CompletedTask, () => agent.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task ExplicitOwnedResourceAuthoritySurvivesReassignmentAndReset()
    {
        var primary = new RecordingModel("ready");
        var ownedAuxiliary = Facts("owned fact");
        var replacement = Facts("replacement fact");
        var agent = new Agent(new() { Model = primary, AuxModel = ownedAuxiliary, OwnsModel = true, OwnedResources = [ownedAuxiliary] });
        try
        {
            agent.SetAuxModel(replacement);
            agent.SetAuxModel(null);
            Assert.Same(primary, agent.AuxModel);
            Assert.Equal(0, ownedAuxiliary.Disposals);
            Assert.Equal(0, replacement.Disposals);
        }
        finally { await agent.DisposeAsync(); }

        await agent.DisposeAsync();
        Assert.Equal(1, ownedAuxiliary.Disposals);
        Assert.Equal(1, primary.Disposals);
        Assert.Equal(0, replacement.Disposals);
        Assert.Equal(0, primary.Calls + ownedAuxiliary.Calls + replacement.Calls);
    }

    private static MemoryManager Manager(EntryStore store) => new(new() { Stores = [store], Injection = null });
    private static RecordingModel Facts(string fact, Task? release = null) => new(JsonSerializer.Serialize(new[] { new { content = fact } }), release);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Within(Task operation)
    {
        using var watchdogCancellation = new CancellationTokenSource();
        var watchdog = Task.Delay(TimeSpan.FromSeconds(10), watchdogCancellation.Token);
        try
        {
            Assert.Same(operation, await Task.WhenAny(operation, watchdog));
            await operation;
        }
        finally
        {
            watchdogCancellation.Cancel();
            try { await watchdog; }
            catch (OperationCanceledException) when (watchdogCancellation.IsCancellationRequested) { }
        }
    }

    private static async Task Finish(Exception? failure, params Func<Task>[] cleanup)
    {
        foreach (var action in cleanup)
        {
            try { await action(); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else if (!ReferenceEquals(failure, error))
                {
                    if (failure.Data["Fixture.CleanupErrors"] is not List<Exception> errors)
                        failure.Data["Fixture.CleanupErrors"] = errors = [];
                    errors.Add(error);
                }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class EntryStore(IExtractor? extractor = null) : MemoryStore, IWritableMemoryStore
    {
        public override string Name => "captured-auxiliary";
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => new() { Triggers = [new IntervalTrigger(int.MaxValue)], Extractor = extractor };
        internal List<string> Entries { get; } = [];
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
            => new((IReadOnlyList<MemoryEntry>)Array.Empty<MemoryEntry>());
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        {
            Entries.Add(content);
            return default;
        }
    }

    private sealed class PendingResource : IAsyncDisposable
    {
        internal TaskCompletionSource<bool> Entered { get; } = Signal();
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        internal int Disposals { get; private set; }
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            Entered.TrySetResult(true);
            await Release.Task;
        }
    }

    private sealed class RecordingModel(string response, Task? release = null) : IModel, IAsyncDisposable
    {
        private int _calls;
        private int _iteratorDisposals;
        private int _disposals;
        internal int Calls => Volatile.Read(ref _calls);
        internal int IteratorDisposals => Volatile.Read(ref _iteratorDisposals);
        internal int Disposals => Volatile.Read(ref _disposals);
        internal TaskCompletionSource<bool> Entered { get; } = Signal();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Entered.TrySetResult(true);
                if (release is not null) await release;
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: response);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            finally { Interlocked.Increment(ref _iteratorDisposals); }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            return default;
        }
    }
}
