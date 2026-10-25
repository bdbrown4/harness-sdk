using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests.Memory;

public sealed class MemoryManagerTests
{
    [Fact]
    public async Task SearchPreservesStoreOrderAttributesProvenanceAndToleratesIndividualBackendFailures()
    {
        var first = new Store("first", [new("one", "untrusted")], maxResults: 7);
        var failed = new Store("failed", [], failure: new IOException("offline"));
        var last = new Store("last", [new("two")]);
        var manager = new MemoryManager(new() { Stores = [first, failed, last] });

        var entries = await manager.SearchAsync("preferences");

        Assert.Equal(["one", "two"], entries.Select(entry => entry.Content));
        Assert.Equal(["first", "last"], entries.Select(entry => entry.StoreName));
        Assert.Equal(7, first.LastLimit);
        Assert.Equal(3, last.LastLimit);
        await manager.SearchAsync("preferences", new() { MaxSearchResults = 2 });
        Assert.Equal(2, first.LastLimit);
        var reversed = await manager.SearchAsync("preferences", new() { Stores = ["last", "first", "last"] });
        Assert.Equal(["two", "one"], reversed.Select(entry => entry.Content));
    }

    [Fact]
    public async Task AddAttemptsEveryWritableEntryStoreAndFlattensFailuresWithoutWritingToReadOnlyBackends()
    {
        var firstFailure = new IOException("first offline");
        var secondFailure = new InvalidOperationException("second offline");
        var first = new WritableStore("first", firstFailure);
        var second = new WritableStore("second", new AggregateException(secondFailure));
        var successful = new WritableStore("successful");
        var manager = new MemoryManager(new() { Stores = [first, new Store("read-only", []), second, successful] });

        var error = await Assert.ThrowsAsync<AggregateException>(() => manager.AddAsync("Prefers C#", new() { Metadata = JsonSerializer.SerializeToElement(new { source = "user" }) }).AsTask());

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains(error.InnerExceptions, item => ReferenceEquals(item, firstFailure));
        Assert.Contains(error.InnerExceptions, item => ReferenceEquals(item, secondFailure));
        Assert.Equal(1, first.Writes);
        Assert.Equal(1, second.Writes);
        Assert.Equal(1, successful.Writes);
        Assert.Equal("user", successful.Metadata!.Value.GetProperty("source").GetString());
    }

    [Fact]
    public async Task RoutingValidatesTheCompleteSelectionBeforeAnyBackendIoAndDistinguishesAnEmptySelection()
    {
        var store = new WritableStore("one");
        var manager = new MemoryManager(new() { Stores = [store] });
        await Assert.ThrowsAsync<ArgumentException>(() => manager.SearchAsync("q", new() { Stores = ["one", "missing"] }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => manager.AddAsync("fact", new() { Stores = ["one", "missing"] }).AsTask());
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Empty(await manager.SearchAsync("q", new() { Stores = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.AddAsync("fact", new() { Stores = [] }).AsTask());
        await manager.AddAsync("fact", new() { Stores = ["one", "one"] });
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task ABackendCanceledIndependentlyOfTheCallerIsAnIsolatedRetrievalFailure()
    {
        using var backendCancellation = new CancellationTokenSource();
        backendCancellation.Cancel();
        var manager = new MemoryManager(new() { Stores = [new Store("canceled", [], failure: new OperationCanceledException(backendCancellation.Token)), new Store("healthy", [new("available")])] });
        var entries = await manager.SearchAsync("query");
        Assert.Equal("available", Assert.Single(entries).Content);
    }

    [Fact]
    public async Task CallerCancellationIsNeverConvertedToAnEmptySearchOrAnAggregateWriteFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new WritableStore("one");
        var manager = new MemoryManager(new() { Stores = [store] });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SearchAsync("query", cancellationToken: cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.AddAsync("fact", cancellationToken: cancellation.Token).AsTask());
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
    }

    private sealed class WritableStore(string name, Exception? failure = null) : MemoryStore, IWritableMemoryStore
    {
        public override string Name => name;
        public override bool Writable => true;
        internal int Writes;
        internal int Reads;
        internal JsonElement? Metadata;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
        { Reads++; return new((IReadOnlyList<MemoryEntry>)[]); }
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        { Writes++; Metadata = metadata; return failure is null ? default : throw failure; }
    }

    private sealed class Store(string name, IReadOnlyList<MemoryEntry> entries, int? maxResults = null, Exception? failure = null) : MemoryStore
    {
        public override string Name => name;
        public override int? MaxSearchResults => maxResults;
        internal int LastLimit;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
        {
            LastLimit = maxSearchResults;
            return failure is null ? new(entries) : throw failure;
        }
    }
}
