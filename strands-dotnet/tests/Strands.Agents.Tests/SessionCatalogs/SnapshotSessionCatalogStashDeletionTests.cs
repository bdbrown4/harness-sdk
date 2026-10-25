using Strands.Agents.Context;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

public sealed class SnapshotSessionCatalogStashDeletionTests
{
    [Theory]
    [InlineData("outside")]
    [InlineData("double-slash")]
    [InlineData("null")]
    public async Task EntireStashListingMustValidateBeforeTheFirstDelete(string corruption)
    {
        var prefix = "context/selected/";
        var storage = new ProbeStorage
        {
            Listing = [prefix + "scopes/agent/first/valid", corruption switch
            {
                "outside" => "context/other/keep",
                "double-slash" => prefix + "scopes//bad",
                _ => null!
            }]
        };
        var stash = new Stash(storage, "selected", "first");
        await Assert.ThrowsAsync<InvalidDataException>(() => stash.ClearSessionAsync().AsTask());
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task AcceptedListingIsDetachedBeforeDeleteCallbacksCanMutateTheBackendList()
    {
        var storage = new ProbeStorage { Listing = ["context/selected/first", "context/selected/second"] };
        storage.OnDelete = key => { if (key.EndsWith("first", StringComparison.Ordinal)) storage.Listing[1] = "context/other/keep"; };
        await new Stash(storage, "selected", "first").ClearSessionAsync();
        Assert.Equal(new[] { "context/selected/first", "context/selected/second" }, storage.Deleted);
    }

    [Fact]
    public async Task CallerScopedSharedStashRemainsANoOpEvenWithACancelledToken()
    {
        var storage = new ProbeStorage(); using var caller = new CancellationTokenSource(); caller.Cancel();
        await new Stash(storage.Namespace("shared"), "selected", "first", customStashNamespace: true).ClearSessionAsync(caller.Token);
        Assert.Equal(0, storage.Lists); Assert.Empty(storage.Deleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DotSessionKeepsExistingLiteralOrPhysicalAliasSemantics(bool fileBackend)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-catalog-dot-stash", Guid.NewGuid().ToString("N"));
        try
        {
            IStorage storage = fileBackend ? new LocalFileStorage(directory) : new InMemoryStorage();
            await storage.WriteAsync("context/./owned", [1]); await storage.WriteAsync("context/sibling/keep", [2]);
            await storage.WriteAsync("outside/keep", [3]);
            await new Stash(storage, ".", "first").ClearSessionAsync();
            Assert.Null(await storage.ReadAsync("context/./owned"));
            if (fileBackend) Assert.Null(await storage.ReadAsync("context/sibling/keep"));
            else Assert.NotNull(await storage.ReadAsync("context/sibling/keep"));
            Assert.NotNull(await storage.ReadAsync("outside/keep"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ProbeStorage : IStorage
    {
        public List<string> Listing { get; set; } = [];
        public List<string> Deleted { get; } = [];
        public int Lists { get; private set; }
        public Action<string>? OnDelete { get; set; }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No writes in stash deletion tests.");
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No reads in stash deletion tests.");
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Deleted.Add(key); OnDelete?.Invoke(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) { Lists++; return new(Listing); }
    }
}
