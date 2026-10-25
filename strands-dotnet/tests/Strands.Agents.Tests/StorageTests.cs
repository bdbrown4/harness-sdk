using System.Text;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task MemoryRoundTripsOpaqueBytesWithoutSharingMutableBuffers()
    {
        var storage = new InMemoryStorage();
        byte[] original = [0, 255, 10, 42];
        await storage.WriteAsync("items/a", original);
        original[0] = 90;

        var first = await storage.ReadAsync("items/a");
        Assert.Equal([0, 255, 10, 42], first);
        first![1] = 90;
        Assert.Equal([0, 255, 10, 42], await storage.ReadAsync("items/a"));
    }

    [Fact]
    public async Task KeysNormalizeAndPrefixesRetainTheirDirectoryBoundary()
    {
        var storage = new InMemoryStorage();
        await storage.WriteAsync("//items///b/", [2]);
        await storage.WriteAsync("items/a", [1]);
        await storage.WriteAsync("items-other", [3]);
        Assert.Equal([2], await storage.ReadAsync("items/b"));
        Assert.Equal(["items-other", "items/a", "items/b"], await storage.ListAsync("/items"));
        Assert.Equal(["items/a", "items/b"], await storage.ListAsync("//items///"));
        await storage.WriteAsync("items/b", [4]);
        Assert.Equal([4], await storage.ReadAsync("items/b"));
        await storage.DeleteAsync("items/missing");
        await storage.DeleteAsync("items/b");
        Assert.Null(await storage.ReadAsync("items/b"));
        storage.Clear();
        Assert.Empty(await storage.ListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("///")]
    [InlineData("../escape")]
    [InlineData("a/../escape")]
    [InlineData("a\\escape")]
    public async Task InvalidKeysFailEveryByteOperation(string key)
    {
        var storage = new InMemoryStorage();
        await Assert.ThrowsAsync<StorageException>(() => storage.WriteAsync(key, []).AsTask());
        await Assert.ThrowsAsync<StorageException>(() => storage.ReadAsync(key).AsTask());
        await Assert.ThrowsAsync<StorageException>(() => storage.DeleteAsync(key).AsTask());
    }

    [Fact]
    public async Task NamespacesNestWithoutLeakingSiblingKeysOrSearchResults()
    {
        var storage = new InMemoryStorage();
        var team = storage.Namespace("//team///");
        var child = team.Namespace("child/");
        await child.WriteAsync("note", Encoding.UTF8.GetBytes("green apple"));
        await storage.WriteAsync("team-other/note", Encoding.UTF8.GetBytes("green apple"));
        await storage.WriteAsync("other/note", Encoding.UTF8.GetBytes("green apple"));
        Assert.Equal(["child/note"], await team.ListAsync());
        Assert.Equal(["note"], await child.ListAsync());
        var match = Assert.Single(await child.SearchAsync("green"));
        Assert.Equal("note", match.Key);
        Assert.Equal(1, match.Score);
        Assert.True(Assert.IsAssignableFrom<IStorageCharacteristics>(child).IsEphemeral);
        Assert.True(Assert.IsAssignableFrom<IStorageCharacteristics>(child).IsNamespaced);
        Assert.False(storage.IsNamespaced);
        await Assert.ThrowsAsync<StorageException>(() => child.ReadAsync("../note").AsTask());
        Assert.Throws<StorageException>(() => storage.Namespace("a/../b"));
    }

    [Fact]
    public async Task KeywordSearchScoresDistinctUnicodeWordsInKeysAndContentWithStableTies()
    {
        var storage = new InMemoryStorage();
        await storage.WriteAsync("zeta", Encoding.UTF8.GetBytes("APPLE apple café"));
        await storage.WriteAsync("apple", Encoding.UTF8.GetBytes("the pear"));
        await storage.WriteAsync("alpha", Encoding.UTF8.GetBytes("café"));
        var matches = await storage.SearchAsync("apple APPLE café");
        Assert.Equal(["zeta", "alpha", "apple"], matches.Select(match => match.Key));
        Assert.Equal([2d, 1d, 1d], matches.Select(match => match.Score));
        Assert.All(matches, match => Assert.Null(match.Data));
        Assert.Empty(await storage.SearchAsync("?!"));
        Assert.Single(await storage.SearchAsync("the"));
    }

    [Fact]
    public async Task CustomSearchIndexesCommittedCopiesAndCanReadTheBackend()
    {
        var strategy = new IndexStrategy();
        var storage = new InMemoryStorage(strategy);
        await storage.WriteAsync("//note/", [7]);
        Assert.Equal("note", strategy.Key);
        Assert.Equal([7], strategy.Committed);
        Assert.Equal([7], await storage.ReadAsync("note"));
        Assert.Equal("note", Assert.Single(await storage.SearchAsync("custom")).Key);
        Assert.Equal("custom", strategy.Query);
    }

    [Fact]
    public async Task CancellationPreventsMutationsAndPropagatesThroughSearchAndNamespaces()
    {
        var storage = new InMemoryStorage();
        await storage.WriteAsync("note", [1]);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.WriteAsync("note", [2], source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.DeleteAsync("note", source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ReadAsync("note", source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.ListAsync("", source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.SearchAsync("", source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.Namespace("team").WriteAsync("note", [], source.Token).AsTask());
        Assert.Equal([1], await storage.ReadAsync("note"));
    }

    private sealed class IndexStrategy : StorageSearchStrategy
    {
        internal string? Key;
        internal byte[]? Committed;
        internal string? Query;
        public override async ValueTask IndexAsync(IStorage storage, string key, byte[] data, CancellationToken cancellationToken = default)
        { Key = key; data[0] = 99; Committed = await storage.ReadAsync(key, cancellationToken); }
        public override ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default)
        { Query = query; return new(new[] { new StorageSearchResult(Key!, 10) }); }
    }
}
