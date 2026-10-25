using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryDeletionConformanceTests
{
    [Fact]
    public async Task MissingAndUnreadableRecordsDeleteWithoutReadingOrDisposingBorrowedStorage()
    {
        var storage = new ProbeStorage();
        var repository = new StorageSessionRepository(storage);
        await repository.DeleteSessionAsync("missing");
        await repository.CreateSessionAsync(new("selected"));
        var key = Assert.Single(storage.Data).Key;
        storage.Data[key] = [0xFF, 0, 0xFF];
        await repository.DeleteSessionAsync("selected");
        await repository.DeleteSessionAsync("selected");
        Assert.Empty(storage.Data);
        Assert.Equal(0, storage.Reads);
        Assert.False(storage.Disposed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task InvalidSessionIdentityFailsBeforeBorrowedStorage(string? sessionId)
    {
        var storage = new ProbeStorage();
        var repository = new StorageSessionRepository(storage);
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await repository.DeleteSessionAsync(sessionId!));
        Assert.Equal(0, storage.Lists);
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task AlreadyCancelledDeletionDoesNotEnterBorrowedStorage()
    {
        var storage = new ProbeStorage();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new StorageSessionRepository(storage).DeleteSessionAsync("selected", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, storage.Lists);
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task ListingFailureRetainsItsIdentityAndDoesNotDelete()
    {
        var expected = new IOException("listing unavailable");
        var storage = new ProbeStorage { OnList = (_, _) => throw expected };
        var actual = await Assert.ThrowsAsync<IOException>(async () => await new StorageSessionRepository(storage).DeleteSessionAsync("selected"));
        Assert.Same(expected, actual);
        Assert.Empty(storage.Deleted);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("prefix-only")]
    [InlineData("double-slash")]
    [InlineData("backslash")]
    [InlineData("null")]
    [InlineData("traversal")]
    public async Task EntireListingIsValidatedBeforeAnyDeletion(string corruption)
    {
        var storage = new ProbeStorage
        {
            OnList = (prefix, _) => new(new[]
            {
                prefix + "valid.json",
                corruption switch
                {
                    "outside" => "other/session.json",
                    "prefix-only" => prefix,
                    "double-slash" => prefix + "agents//state.json",
                    "backslash" => prefix + "agents\\state.json",
                    "null" => null!,
                    _ => prefix + "../sibling.json"
                }
            })
        };
        if (corruption is "backslash" or "traversal")
            await Assert.ThrowsAsync<StorageException>(async () => await new StorageSessionRepository(storage).DeleteSessionAsync("selected"));
        else
            await Assert.ThrowsAsync<InvalidDataException>(async () => await new StorageSessionRepository(storage).DeleteSessionAsync("selected"));
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task DeleteFailureRetainsItsIdentityAndStopsLaterMutations()
    {
        var expected = new IOException("cannot delete second row");
        var storage = new ProbeStorage
        {
            OnList = (prefix, _) => new(new[] { prefix + "a", prefix + "b", prefix + "c" }),
            OnDelete = (key, _) => key.EndsWith("b", StringComparison.Ordinal) ? throw expected : default
        };
        var actual = await Assert.ThrowsAsync<IOException>(async () => await new StorageSessionRepository(storage).DeleteSessionAsync("selected"));
        Assert.Same(expected, actual);
        Assert.Equal(2, storage.Deleted.Count);
        Assert.EndsWith("b", storage.Deleted[1]);
    }

    [Fact]
    public async Task CancellationAfterListingPreventsTheFirstMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var storage = new ProbeStorage { OnList = (prefix, _) => { cancellation.Cancel(); return new(new[] { prefix + "a" }); } };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new StorageSessionRepository(storage).DeleteSessionAsync("selected", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task CancellationDuringDeletionStopsBeforeTheNextRow()
    {
        using var cancellation = new CancellationTokenSource();
        var storage = new ProbeStorage
        {
            OnList = (prefix, _) => new(new[] { prefix + "a", prefix + "b" }),
            OnDelete = (_, _) => { cancellation.Cancel(); return default; }
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new StorageSessionRepository(storage).DeleteSessionAsync("selected", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(storage.Deleted);
    }

    [Fact]
    public async Task CallerCancellationRetainsAnIndependentBackendFailureAsItsCause()
    {
        using var cancellation = new CancellationTokenSource();
        var expected = new IOException("backend failed during cancellation");
        var storage = new ProbeStorage
        {
            OnList = (prefix, _) => new(new[] { prefix + "a" }),
            OnDelete = (_, _) => { cancellation.Cancel(); throw expected; }
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new StorageSessionRepository(storage).DeleteSessionAsync("selected", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(expected, error.InnerException);
    }

    [Fact]
    public async Task PhysicalDeleteCompletionSerializesLaterRowsAndRepositoryReads()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new ProbeStorage
        {
            OnList = (prefix, _) => new(new[] { prefix + "a", prefix + "b" }),
            OnDelete = async (key, _) => { if (key.EndsWith("a", StringComparison.Ordinal)) { entered.SetResult(true); await release.Task; } }
        };
        var repository = new StorageSessionRepository(storage);
        var deletion = repository.DeleteSessionAsync("selected").AsTask();
        Task<SessionRecord?>? read = null;
        try
        {
            await entered.Task;
            read = repository.ReadSessionAsync("sibling").AsTask();
            Assert.False(deletion.IsCompleted);
            Assert.False(read.IsCompleted);
            Assert.Single(storage.Deleted);
            Assert.Equal(0, storage.Reads);
        }
        finally { release.TrySetResult(true); await deletion; if (read is not null) await read; }
        Assert.Equal(2, storage.Deleted.Count);
        Assert.Equal(1, storage.Reads);
    }

    [Fact]
    public async Task ACancelledQueuedDeletionNeverListsAndDoesNotPoisonTheGate()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new ProbeStorage { OnList = async (_, _) => { entered.TrySetResult(true); await release.Task; return Array.Empty<string>(); } };
        var repository = new StorageSessionRepository(storage);
        var first = repository.DeleteSessionAsync("first").AsTask();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await entered.Task;
            var queued = repository.DeleteSessionAsync("queued", cancellation.Token).AsTask();
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(1, storage.Lists);
        }
        finally { release.TrySetResult(true); await first; }
        await repository.DeleteSessionAsync("later");
        Assert.Equal(2, storage.Lists);
    }

    [Fact]
    public async Task ABackendCannotReenterItsOwnRepositoryAndTheGateRecovers()
    {
        var storage = new ProbeStorage();
        var repository = new StorageSessionRepository(storage);
        storage.OnList = async (_, _) => { await repository.DeleteSessionAsync("nested"); return Array.Empty<string>(); };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.DeleteSessionAsync("selected"));
        storage.OnList = null;
        await repository.DeleteSessionAsync("selected");
        Assert.Empty(storage.Deleted);
    }

    [Theory]
    [InlineData("root/.", "root")]
    [InlineData("x/./y", "x/y")]
    public async Task NonFileBackendsRetainLiteralDotNamespaces(string literal, string folded)
    {
        var storage = new InMemoryStorage();
        var selected = new StorageSessionRepository(storage, new() { Prefix = literal });
        var sibling = new StorageSessionRepository(storage, new() { Prefix = folded });
        await selected.CreateSessionAsync(new("same-id"));
        await sibling.CreateSessionAsync(new("same-id"));
        await selected.DeleteSessionAsync("same-id");
        Assert.Null(await selected.ReadSessionAsync("same-id"));
        Assert.NotNull(await sibling.ReadSessionAsync("same-id"));
        Assert.StartsWith(folded + "/", Assert.Single(await storage.ListAsync()));
    }

    private sealed class ProbeStorage : IStorage, IAsyncDisposable
    {
        internal Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal);
        internal List<string> Deleted { get; } = [];
        internal Func<string, CancellationToken, ValueTask<IReadOnlyList<string>>>? OnList { get; set; }
        internal Func<string, CancellationToken, ValueTask>? OnDelete { get; set; }
        internal int Lists { get; private set; }
        internal int Reads { get; private set; }
        internal bool Disposed { get; private set; }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        { Data[key] = (byte[])data.Clone(); return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { Reads++; return new(Data.TryGetValue(key, out var value) ? (byte[])value.Clone() : null); }
        public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        { Deleted.Add(key); if (OnDelete is { } callback) await callback(key, cancellationToken); Data.Remove(key); }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
        { Lists++; return OnList is { } callback ? callback(prefix, cancellationToken) : new(Data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray()); }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
}
