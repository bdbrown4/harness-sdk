using System.Text;
using Strands.Agents.Memory;
using Strands.Agents.Storage;

namespace Strands.Sqlite.Tests;

public sealed class Bm25MemoryIntegrationTests
{
    [Fact]
    public async Task RealBackendIndexWorksThroughTheMemoryNamespaceWithoutChangingTheDefault()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy();
        var storage = new LocalFileStorage(directory.Root, strategy);
        var selected = new FileMemoryStore(new() { Name = "facts", Storage = storage, UseStorageSearch = true });
        var foreign = new FileMemoryStore(new() { Name = "foreign", Storage = storage, UseStorageSearch = true });
        await selected.AddAsync("# Authentication\nOAuth tokens are rotated");
        await foreign.AddAsync("# Foreign authentication\nOAuth tokens are external");
        var entry = Assert.Single(await selected.SearchAsync("auth tok", 1));
        Assert.Contains("tokens are rotated", entry.Content);
        Assert.Equal("authentication.md", entry.Metadata!.Value.GetProperty("path").GetString());
        var unchanged = new FileMemoryStore(new() { Name = "facts", Storage = storage });
        Assert.Empty(await unchanged.SearchAsync("auth tok"));
    }

    [Fact]
    public async Task OptInBackendDataCanSupplyMissingOrUnreadableFiles()
    {
        var backend = new Backend { IsNamespaced = true, Results = [new("topic.md", 0.25, Bytes(" backend content "))], FailRead = true };
        var store = new FileMemoryStore(new() { Name = "facts", Storage = backend, UseStorageSearch = true });
        var entry = Assert.Single(await store.SearchAsync("needle"));
        Assert.Equal("backend content", entry.Content);
        Assert.Equal("topic.md", entry.Metadata!.Value.GetProperty("path").GetString());
        Assert.Equal(0.25, entry.Metadata.Value.GetProperty("score").GetDouble());
        Assert.Equal(1, backend.SearchCalls);
        Assert.Equal(0, backend.ReadCalls);
    }

    [Fact]
    public async Task EntireBackendBatchIsDetachedBeforeFirstFallbackRead()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = Bytes("originalneedle");
        var results = new List<StorageSearchResult> { new("first.md", 1), new("second.md", 0.5, data) };
        var backend = new Backend { IsNamespaced = true, Results = results, Read = async key => { entered.TrySetResult(true); await release.Task; return Bytes("fallbackneedle"); } };
        var store = new FileMemoryStore(new() { Name = "facts", Storage = backend, UseStorageSearch = true });
        var pending = store.SearchAsync("needle").AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, pending));
            Array.Clear(data, 0, data.Length);
            results.Clear();
        }
        finally { release.TrySetResult(true); }
        var entries = await pending;
        Assert.Equal(new[] { "fallbackneedle", "originalneedle" }, entries.Select(entry => entry.Content));
        Assert.Equal(1, backend.ReadCalls);
    }

    [Fact]
    public async Task NamespaceFiltersBeforeLimitingAndStripsOnce()
    {
        var backend = new Backend { Results = [new("memory/foreign/first.md", 9, Bytes("foreign")), new("memory/facts/second.md", 1, Bytes("accepted"))] };
        var store = new FileMemoryStore(new() { Name = "facts", Storage = backend, UseStorageSearch = true });
        var entry = Assert.Single(await store.SearchAsync("needle", 1));
        Assert.Equal("accepted", entry.Content);
        Assert.Equal("second.md", entry.Metadata!.Value.GetProperty("path").GetString());
        Assert.Equal(0, backend.ReadCalls);
    }

    [Fact]
    public async Task ExplicitSearchKeepsItsExistingReadHydrationDespiteOptIn()
    {
        var backend = new Backend { IsNamespaced = true, FailSearch = true };
        backend.Values["topic.md"] = Bytes("from backing bytes");
        var store = new FileMemoryStore(new() { Name = "facts", Storage = backend, UseStorageSearch = true, Search = new FixedSearch() });
        Assert.Equal("from backing bytes", Assert.Single(await store.SearchAsync("needle")).Content);
        Assert.Equal(0, backend.SearchCalls);
        Assert.Equal(1, backend.ReadCalls);
    }

    [Fact]
    public async Task FalseDefaultRetainsTheNativeKeywordScan()
    {
        var backend = new Backend { IsNamespaced = true, FailSearch = true };
        backend.Values["topic.md"] = Bytes("needle keyword bytes");
        var store = new FileMemoryStore(new() { Name = "facts", Storage = backend });
        Assert.Equal("needle keyword bytes", Assert.Single(await store.SearchAsync("needle")).Content);
        Assert.Equal(0, backend.SearchCalls);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class FixedSearch : StorageSearchStrategy
    {
        public override ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default)
            => new(new[] { new StorageSearchResult("topic.md", 1, Bytes("ignored supplied data")) });
    }

    private sealed class Backend : ISearchableStorage, IStorageCharacteristics
    {
        internal Dictionary<string, byte[]> Values { get; } = new();
        internal IReadOnlyList<StorageSearchResult> Results { get; init; } = [];
        internal Func<string, Task<byte[]?>>? Read { get; init; }
        internal bool FailRead { get; init; }
        internal bool FailSearch { get; init; }
        internal int ReadCalls;
        internal int SearchCalls;
        public bool IsNamespaced { get; init; }
        public bool IsEphemeral => true;
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Values.Remove(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => new(Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { Values[key] = (byte[])data.Clone(); return default; }
        public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ReadCalls);
            if (FailRead) throw new IOException("No backing read allowed.");
            if (Read is { } read) return await read(key);
            return Values.TryGetValue(key, out var data) ? (byte[])data.Clone() : null;
        }
        public ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SearchCalls);
            if (FailSearch) throw new IOException("Backend search was not selected.");
            return new(Results);
        }
    }
}
