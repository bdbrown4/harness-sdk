using System.Text;
using Microsoft.Data.Sqlite;
using Strands.Agents.Storage;

namespace Strands.Sqlite.Tests;

public sealed class Bm25SearchStrategyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealBm25RewardsLengthAndTermFrequencyRatherThanDistinctOverlap(bool frequency)
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        var higher = frequency ? string.Join(" ", Enumerable.Repeat("needle", 5).Concat(Enumerable.Repeat("filler", 5))) : "needle";
        var lower = "needle " + string.Join(" ", Enumerable.Repeat("filler", frequency ? 9 : 100));
        await strategy.IndexAsync(storage, "higher.md", Bytes(higher));
        await strategy.IndexAsync(storage, "lower.md", Bytes(lower));
        var results = await strategy.SearchAsync(storage, "needle");
        Assert.Equal(2, results.Count);
        Assert.Equal("higher.md", results[0].Key);
        Assert.True(results[0].Score > results[1].Score);
        Assert.All(results, result => { Assert.InRange(result.Score, double.Epsilon, 1); Assert.Null(result.Data); });
    }

    [Fact]
    public async Task InvalidUtf8UsesReplacementAndEmptyContentStillIndexesItsKey()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        await strategy.IndexAsync(storage, "binary.bin", Bytes("needle ").Concat(new byte[] { 0x80, 0xff }).ToArray());
        await strategy.IndexAsync(storage, "emptytopic.md", []);
        Assert.Equal("binary.bin", Assert.Single(await strategy.SearchAsync(storage, "needle")).Key);
        Assert.Equal("emptytopic.md", Assert.Single(await strategy.SearchAsync(storage, "emptytopic")).Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("the and is")]
    [InlineData("a I")]
    [InlineData("-- : \" \"")]
    public async Task FilteredQueriesDoNotCreateTheRootOrDatabase(string query)
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy();
        Assert.Empty(await strategy.SearchAsync(new LocalFileStorage(directory.Root), query));
        Assert.False(Directory.Exists(directory.Root));
        Assert.Empty(Directory.GetFiles(directory.Parent));
    }

    [Fact]
    public async Task PrefixTermsUseAndAcrossKeyAndContent()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        await strategy.IndexAsync(storage, "authentication.md", Bytes("tokens lifecycle"));
        await strategy.IndexAsync(storage, "other.md", Bytes("authentication unrelated"));
        Assert.Equal("authentication.md", Assert.Single(await strategy.SearchAsync(storage, "the AUTH tok")).Key);
        Assert.Empty(await strategy.SearchAsync(storage, "authentication impossible"));
    }

    [Fact]
    public async Task ReplacingContentRemovesThePriorMatch()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        await strategy.IndexAsync(storage, "topic.md", Bytes("authentication tokens"));
        await strategy.IndexAsync(storage, "topic.md", Bytes("deployment pipelines"));
        Assert.Empty(await strategy.SearchAsync(storage, "authentication"));
        Assert.Equal("topic.md", Assert.Single(await strategy.SearchAsync(storage, "deployment")).Key);
    }

    [Fact]
    public async Task OnlyHiddenFinalBasenamesAreExcluded()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        await strategy.IndexAsync(storage, ".secret", Bytes("hiddenword"));
        await strategy.IndexAsync(storage, ".hidden/visible.md", Bytes("visibleword"));
        Assert.Empty(await strategy.SearchAsync(storage, "hiddenword"));
        Assert.Equal(".hidden/visible.md", Assert.Single(await strategy.SearchAsync(storage, "visibleword")).Key);
    }

    [Fact]
    public async Task CloseAndRootSwitchesKeepDefaultPersistentIndexesSeparate()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy();
        var first = new LocalFileStorage(directory.Root);
        var second = new LocalFileStorage(Path.Combine(directory.Parent, "other"));
        await strategy.IndexAsync(first, "same.md", Bytes("firstneedle"));
        await strategy.IndexAsync(second, "same.md", Bytes("secondneedle"));
        Assert.Empty(await strategy.SearchAsync(second, "firstneedle"));
        Assert.Single(await strategy.SearchAsync(first, "firstneedle"));
        await strategy.CloseAsync();
        Assert.Single(await strategy.SearchAsync(first, "firstneedle"));
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("")]
    public async Task TemporaryDatabaseModesAreLostAtReusableClose(string path)
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = path });
        var storage = new LocalFileStorage(directory.Root);
        await strategy.IndexAsync(storage, "topic.md", Bytes("needle"));
        Assert.Single(await strategy.SearchAsync(storage, "needle"));
        await strategy.CloseAsync();
        Assert.Empty(await strategy.SearchAsync(storage, "needle"));
    }

    [Fact]
    public async Task ExistingFilesAreNotBackfilledAndDeleteDoesNotRemoveIndexRows()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.Root);
        File.WriteAllText(Path.Combine(directory.Root, "existing.md"), "preexistingneedle");
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root, strategy);
        Assert.Empty(await storage.SearchAsync("preexistingneedle"));
        await storage.WriteAsync("new.md", Bytes("indexedneedle"));
        await storage.DeleteAsync("new.md");
        Assert.Null(await storage.ReadAsync("new.md"));
        Assert.Equal("new.md", Assert.Single(await storage.SearchAsync("indexedneedle")).Key);
    }

    [Fact]
    public async Task InputBytesAreCopiedBeforeAnAwaitCanRetainThem()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        var input = Bytes("originalneedle");
        var pending = strategy.IndexAsync(storage, "topic.md", input);
        Array.Clear(input, 0, input.Length);
        await pending;
        Assert.Single(await strategy.SearchAsync(storage, "originalneedle"));
    }

    [Fact]
    public async Task UnchangedHashSkipsMutationAndFailedReplacementRollsBackBothTables()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        var storage = new LocalFileStorage(directory.Root);
        var bytes = Bytes("originalneedle");
        await strategy.IndexAsync(storage, "topic.md", bytes);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_hash BEFORE INSERT ON doc_hashes BEGIN SELECT RAISE(FAIL, 'rejected hash'); END";
            command.ExecuteNonQuery();
        }
        await strategy.IndexAsync(storage, "topic.md", bytes);
        var error = await Assert.ThrowsAsync<SqliteException>(() => strategy.IndexAsync(storage, "topic.md", Bytes("replacementneedle")).AsTask());
        Assert.Contains("rejected hash", error.Message);
        Assert.Single(await strategy.SearchAsync(storage, "originalneedle"));
        Assert.Empty(await strategy.SearchAsync(storage, "replacementneedle"));
    }

    [Fact]
    public async Task UnsupportedStorageIsRejectedBeforeHostIo()
    {
        await using var strategy = new Bm25SearchStrategy();
        var storage = new InMemoryStorage();
        await Assert.ThrowsAsync<ArgumentException>(() => strategy.IndexAsync(storage, "topic.md", Bytes("needle")).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => strategy.SearchAsync(storage, "needle").AsTask());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanceledAdmissionRetainsItsCallerTokenAndCreatesNothing(bool index)
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy();
        var storage = new LocalFileStorage(directory.Root);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (index) await strategy.IndexAsync(storage, "topic.md", Bytes("needle"), caller.Token);
            else await strategy.SearchAsync(storage, "needle", caller.Token);
        });
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.False(Directory.Exists(directory.Root));
        Assert.Empty(Directory.GetFiles(directory.Parent));
    }

    [Fact]
    public async Task TerminalDisposalRejectsEvenNoOpOperations()
    {
        using var directory = new TestDirectory();
        var strategy = new Bm25SearchStrategy();
        await strategy.DisposeAsync();
        await strategy.DisposeAsync();
        var storage = new LocalFileStorage(directory.Root);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => strategy.SearchAsync(storage, "").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => strategy.IndexAsync(storage, ".hidden", []).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => strategy.CloseAsync().AsTask());
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}

internal sealed class TestDirectory : IDisposable
{
    internal TestDirectory()
    {
        Parent = Path.Combine(Path.GetTempPath(), "bm25-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Parent);
    }
    internal string Parent { get; }
    internal string Root => Path.Combine(Parent, "storage");
    public void Dispose() { if (Directory.Exists(Parent)) Directory.Delete(Parent, true); }
}
