using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests.Memory;

public sealed class MemoryExtractionTests
{
    [Fact]
    public async Task InvocationExtractionIsDetachedAndFlushPersistsOrderedIndependentMessageSnapshots()
    {
        var store = new MessageStore(new() { Triggers = [new InvocationTrigger()] });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        try
        {
            await agent.InvokeAsync("hello");
            Assert.Equal(1, store.Calls);
            var flush = manager.FlushAsync().AsTask();
            Assert.False(flush.IsCompleted);
            store.Release.TrySetResult(true);
            await flush;
            Assert.Equal(["hello", "ready"], store.Messages.Select(message => message.Text));
            Assert.Equal(new long[] { 0, 1 }, store.SequenceNumbers);
            Assert.NotSame(agent.Messages[0], store.Messages[0]);
            await manager.FlushAsync();
            Assert.Equal(1, store.Calls);
        }
        finally { store.Release.TrySetResult(true); await manager.FlushAsync(); }
    }

    [Fact]
    public async Task FilteringKeepsSequenceAlignmentAndMetadataWhileExplicitEmptyFilterRetainsCompleteToolTraffic()
    {
        var filtered = new RecordingStore("filtered", new() { Triggers = [new InvocationTrigger()] });
        var complete = new RecordingStore("complete", new() { Triggers = [new InvocationTrigger()], ExcludeContentBlocks = [] });
        var manager = new MemoryManager(new() { Stores = [filtered, complete], Injection = null });
        var tool = new FunctionTool(new("lookup", "Look up data", JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""")), (_, _, _) => new("lookup result"));
        await using var agent = new Agent(new() { Model = new Model(firstTool: true), Tools = [tool], MemoryManager = manager });

        await agent.InvokeAsync("hello");
        await manager.FlushAsync();

        var filteredBatch = Assert.Single(filtered.Batches);
        Assert.Equal(new long[] { 0, 1, 3 }, filteredBatch.Sequences);
        Assert.Equal(["hello", "thinking", "ready"], filteredBatch.Messages.Select(message => message.Text));
        Assert.Equal(1, filteredBatch.Messages[1].Metadata!.Value.GetProperty("step").GetInt32());
        var fullBatch = Assert.Single(complete.Batches);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, fullBatch.Sequences);
        Assert.Contains(fullBatch.Messages[1].Content, block => block is ToolUseBlock);
        Assert.Contains(fullBatch.Messages[2].Content, block => block is ToolResultBlock);
    }

    [Fact]
    public async Task FailingStoresRetainTheirBacklogAndProbeEveryThirdRequestUntilTheyRecover()
    {
        var failing = new RecordingStore("failing", new() { Triggers = [new InvocationTrigger()] }) { Fail = true };
        var healthy = new RecordingStore("healthy", new() { Triggers = [new InvocationTrigger()] });
        var manager = new MemoryManager(new() { Stores = [failing, healthy], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        for (var turn = 0; turn < 12; turn++) await agent.InvokeAsync("turn " + turn);
        Assert.Equal(10, failing.Batches.Count);
        Assert.Equal(12, healthy.Batches.Count);
        await agent.InvokeAsync("probe fails");
        Assert.Equal(11, failing.Batches.Count);
        failing.Fail = false;
        await agent.InvokeAsync("waiting one");
        await agent.InvokeAsync("waiting two");
        Assert.Equal(11, failing.Batches.Count);
        await agent.InvokeAsync("probe succeeds");
        Assert.Equal(12, failing.Batches.Count);
        Assert.Equal(Enumerable.Range(0, 32).Select(index => (long)index), failing.Batches.Last().Sequences);
        await agent.InvokeAsync("normal again");
        Assert.Equal(new long[] { 32, 33 }, failing.Batches.Last().Sequences);
        Assert.All(healthy.Batches, batch => Assert.Equal(2, batch.Messages.Count));
    }

    [Fact]
    public async Task FlushBypassesIntervalsAndBackoffAndWaitsForWorkQueuedWhileItIsAlreadyWaiting()
    {
        var store = new MessageStore(new() { Triggers = [new InvocationTrigger()] });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        try
        {
            await agent.InvokeAsync("first");
            var flush = manager.FlushAsync().AsTask();
            await agent.InvokeAsync("second");
            Assert.Equal(1, store.Calls);
            Assert.False(flush.IsCompleted);
            store.Release.TrySetResult(true);
            await flush;
            Assert.Equal(2, store.Calls);
            Assert.Equal(new long[] { 2, 3 }, store.SequenceNumbers);
        }
        finally { store.Release.TrySetResult(true); await manager.FlushAsync(); }

        var deferred = new RecordingStore("deferred", new() { Triggers = [new IntervalTrigger(50)] });
        var deferredManager = new MemoryManager(new() { Stores = [deferred], Injection = null });
        await using var other = new Agent(new() { Model = new Model(), MemoryManager = deferredManager });
        await other.InvokeAsync("saved at shutdown boundary");
        Assert.Empty(deferred.Batches);
        await deferredManager.FlushAsync();
        Assert.Single(deferred.Batches);

        var offline = new RecordingStore("offline", new() { Triggers = [new InvocationTrigger()] }) { Fail = true };
        var offlineManager = new MemoryManager(new() { Stores = [offline], Injection = null });
        await using var last = new Agent(new() { Model = new Model(), MemoryManager = offlineManager });
        for (var turn = 0; turn < 12; turn++) await last.InvokeAsync("turn");
        Assert.Equal(10, offline.Batches.Count);
        offline.Fail = false;
        await offlineManager.FlushAsync();
        Assert.Equal(11, offline.Batches.Count);
        Assert.Equal(24, offline.Batches.Last().Messages.Count);
    }

    [Fact]
    public async Task CancelingFlushDoesNotCancelAnAcceptedBackgroundSave()
    {
        var store = new MessageStore(new() { Triggers = [new InvocationTrigger()] });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        try
        {
            await agent.InvokeAsync("hello");
            using var canceled = new CancellationTokenSource();
            var flush = manager.FlushAsync(canceled.Token).AsTask();
            Assert.False(flush.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
            Assert.False(store.Token.CanBeCanceled);
        }
        finally { store.Release.TrySetResult(true); await manager.FlushAsync(); }
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task SharedIntervalTriggersKeepIndependentCountersAndReadOnlyDelegatesNeverExtract()
    {
        var trigger = new IntervalTrigger(2);
        var store = new RecordingStore("shared", new() { Triggers = [trigger] });
        var firstManager = new MemoryManager(new() { Stores = [store], Injection = null });
        var secondManager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var first = new Agent(new() { Model = new Model(), MemoryManager = firstManager });
        await using var second = new Agent(new() { Model = new Model(), MemoryManager = secondManager });
        await first.InvokeAsync("one");
        await second.InvokeAsync("one");
        Assert.Empty(store.Batches);
        await first.InvokeAsync("two");
        Assert.Single(store.Batches);
        await second.InvokeAsync("two");
        Assert.Equal(2, store.Batches.Count);
        var readOnly = firstManager.ToReadOnly();
        await using var delegated = new Agent(new() { Model = new Model(), MemoryManager = readOnly });
        for (var turn = 0; turn < 6; turn++) await delegated.InvokeAsync("read only");
        await readOnly.FlushAsync();
        Assert.Equal(2, store.Batches.Count);
    }

    [Fact]
    public async Task EntryExtractionDefaultsToTheAuxiliaryModelAndRetriesAnEntirePartiallyFailedBatch()
    {
        var auxiliary = new Model("[{\"content\":\"alpha\"},{\"content\":\"beta\"}]");
        var store = new EntryStore() { Fail = true };
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), AuxModel = auxiliary, MemoryManager = manager });
        await agent.InvokeAsync("hello");
        Assert.Equal(1, auxiliary.Calls);
        Assert.Equal(2, store.Entries.Count);
        store.Fail = false;
        await manager.FlushAsync();
        Assert.Equal(2, auxiliary.Calls);
        Assert.Equal(2, store.Entries.Count(entry => entry == "alpha"));
        Assert.Equal(2, store.Entries.Count(entry => entry == "beta"));
    }

    [Fact]
    public async Task ShutdownDrainsDeferredExtractionAndDisposesEveryTriggerSubscriptionOnce()
    {
        var first = new DeferredTrigger();
        var second = new DeferredTrigger();
        var store = new RecordingStore("deferred", new() { Triggers = [first, second] });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        await agent.InvokeAsync("save at shutdown");
        Assert.Empty(store.Batches);

        await agent.ShutdownAsync();

        Assert.Single(store.Batches);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        await agent.ShutdownAsync();
        Assert.Single(store.Batches);
        Assert.Equal(1, first.Disposals);
    }

    [Fact]
    public async Task OneStoresSuccessfulSaveCannotDiscardAnotherStoresStillUncommittedRetryBacklog()
    {
        var successful = new GatedStore("successful", failFirst: false);
        var failing = new GatedStore("failing", failFirst: true);
        var manager = new MemoryManager(new() { Stores = [successful, failing], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        try
        {
            await agent.InvokeAsync("first");
            Assert.Single(successful.Batches);
            Assert.Single(failing.Batches);
            successful.Release.TrySetResult(true);
            await agent.InvokeAsync("second");
            Assert.Same(successful.SecondEntered.Task, await Task.WhenAny(successful.SecondEntered.Task, Task.Delay(TimeSpan.FromSeconds(20))));
            failing.Release.TrySetResult(true);

            await manager.FlushAsync();

            Assert.Equal(new long[] { 0, 1, 2, 3 }, failing.Batches[1].Sequences);
            Assert.Equal(["first", "ready", "second", "ready"], failing.Batches[1].Messages.Select(message => message.Text));
        }
        finally { successful.Release.TrySetResult(true); failing.Release.TrySetResult(true); await manager.FlushAsync(); }
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("google")]
    [InlineData("plain")]
    public async Task SemanticReasoningExclusionAlsoRemovesNativeProviderReasoning(string provider)
    {
        ContentBlock reasoning = provider switch
        {
            "responses" => new Strands.Agents.Models.ResponsesReasoningBlock(JsonSerializer.Deserialize<JsonElement>("""{"type":"reasoning","id":"reason","encrypted_content":"opaque-private","summary":[{"type":"summary_text","text":"private reasoning"}]}""")),
            "google" => new Strands.Agents.Models.GoogleContentBlock(JsonSerializer.Deserialize<JsonElement>("""{"thought":true,"text":"private reasoning","thoughtSignature":"opaque-private"}""")),
            _ => new ReasoningBlock("private reasoning", "opaque-private")
        };
        var store = new RecordingStore("filtered", new() { Triggers = [new InvocationTrigger()], ExcludeContentBlocks = ["reasoningContent"] });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(extraBlocks: [reasoning]), MemoryManager = manager });
        await agent.InvokeAsync("hello");
        await manager.FlushAsync();
        var batch = Assert.Single(store.Batches);
        Assert.All(batch.Messages, message => Assert.All(message.Content, block => Assert.IsType<TextBlock>(block)));
        Assert.Equal(["hello", "ready"], batch.Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("google")]
    public async Task SemanticTextExclusionRecognizesNativeVisibleTextAndRetainsOtherTypedContent(string provider)
    {
        ContentBlock visible = provider == "responses"
            ? new Strands.Agents.Models.ResponsesContentBlock("message", 0, JsonSerializer.Deserialize<JsonElement>("""{"type":"output_text","text":"more visible text"}"""))
            : new Strands.Agents.Models.GoogleContentBlock(JsonSerializer.Deserialize<JsonElement>("""{"text":"more visible text"}"""));
        var store = new RecordingStore("filtered", new() { Triggers = [new InvocationTrigger()], ExcludeContentBlocks = ["text"] });
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(extraBlocks: [visible, new JsonBlock(JsonSerializer.SerializeToElement(new { safe = true }))]), MemoryManager = manager });
        await agent.InvokeAsync("hello");
        await manager.FlushAsync();
        var message = Assert.Single(Assert.Single(store.Batches).Messages);
        Assert.True(Assert.IsType<JsonBlock>(Assert.Single(message.Content)).Json.GetProperty("safe").GetBoolean());
    }

    private sealed class GatedStore(string name, bool failFirst) : MemoryStore, IMessageMemoryStore
    {
        public override string Name => name;
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => new() { Triggers = [new InvocationTrigger()] };
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> SecondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<(IReadOnlyList<Message> Messages, IReadOnlyList<long> Sequences)> Batches = [];
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public async ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default)
        {
            Batches.Add((messages, sequenceNumbers));
            var call = Batches.Count;
            if (call == 1)
            {
                await Release.Task.ConfigureAwait(false);
                if (failFirst) throw new IOException("first save failed after another store completed");
            }
            else SecondEntered.TrySetResult(true);
        }
    }

    private sealed class DeferredTrigger : ExtractionTrigger
    {
        public override string Name => "deferred";
        internal int Disposals;
        public override IDisposable Attach(Agent agent, Action fire) => new Subscription(this);
        private sealed class Subscription(DeferredTrigger owner) : IDisposable
        { public void Dispose() => owner.Disposals++; }
    }

    private sealed class EntryStore : MemoryStore, IWritableMemoryStore
    {
        public override string Name => "entries";
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => new() { Triggers = [new InvocationTrigger()] };
        internal bool Fail;
        internal readonly List<string> Entries = [];
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        { Entries.Add(content); if (Fail && content == "alpha") throw new IOException("one entry failed"); return default; }
    }

    private sealed class RecordingStore(string name, MemoryExtractionOptions extraction) : MemoryStore, IMessageMemoryStore
    {
        public override string Name => name;
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => extraction;
        internal bool Fail;
        internal readonly List<(IReadOnlyList<Message> Messages, IReadOnlyList<long> Sequences)> Batches = [];
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default)
        { Batches.Add((messages, sequenceNumbers)); if (Fail) throw new IOException("offline"); return default; }
    }

    private sealed class MessageStore(MemoryExtractionOptions extraction) : MemoryStore, IMessageMemoryStore
    {
        public override string Name => "messages";
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => extraction;
        internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        internal IReadOnlyList<Message> Messages = [];
        internal IReadOnlyList<long> SequenceNumbers = [];
        internal CancellationToken Token;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public async ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default)
        { Calls++; Token = cancellationToken; Messages = messages; SequenceNumbers = sequenceNumbers; await Release.Task; }
    }

    private sealed class Model(string output = "ready", bool firstTool = false, IReadOnlyList<ContentBlock>? extraBlocks = null) : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = ++Calls;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: firstTool && call == 1 ? "thinking" : output);
            yield return new ModelContentBlockStopEvent(0);
            var index = 1;
            foreach (var extra in extraBlocks ?? [])
            {
                yield return new ModelContentBlockStartEvent(index) { ContentBlock = extra };
                yield return new ModelContentBlockStopEvent(index++);
            }
            if (firstTool && call == 1)
            {
                yield return new ModelContentBlockStartEvent(index, new("call", "lookup", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(index);
            }
            yield return new ModelMessageStopEvent(firstTool && call == 1 ? StopReasons.ToolUse : StopReasons.EndTurn) { Metadata = JsonSerializer.SerializeToElement(new { step = call }) };
            await Task.CompletedTask;
        }
    }
}
