using System.Text;
using Microsoft.Data.Sqlite;
using Strands.Agents.Storage;

namespace Strands.Sqlite.Tests;

public sealed class Bm25SearchStrategyLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedSqlJoinsBeforeCancellationAndKeepsIndependentFailurePrimary(bool failSql)
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        var storage = new LocalFileStorage(directory.Root);
        await SeedAsync(storage, database);
        if (failSql) Execute(database, "CREATE TRIGGER reject_hash BEFORE INSERT ON doc_hashes BEGIN SELECT RAISE(FAIL, 'independent SQL failure'); END");
        using var held = new HeldWriter(database);
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        using var caller = new CancellationTokenSource();
        Task pending;
        Task disposing;
        try
        {
            pending = strategy.IndexAsync(storage, "new.md", Bytes("committedneedle"), caller.Token).AsTask();
            Assert.Same(strategy.SqlWorkStarted, await Task.WhenAny(strategy.SqlWorkStarted, pending));
            caller.Cancel();
            Assert.False(pending.IsCompleted);
            disposing = strategy.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => strategy.SearchAsync(storage, "").AsTask());
        }
        finally { held.Release(); }
        if (failSql)
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() => pending);
            Assert.Contains("independent SQL failure", error.Message);
            Assert.Same(error, Assert.Single(pending.Exception!.InnerExceptions));
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        await disposing;
        await using var reader = new Bm25SearchStrategy(new() { DatabasePath = database });
        var results = await reader.SearchAsync(storage, "committedneedle");
        if (failSql) Assert.Empty(results);
        else Assert.Equal("new.md", Assert.Single(results).Key);
    }

    [Fact]
    public async Task CanceledSerialWaitDoesNoWorkForItsDifferentRoot()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        var first = new LocalFileStorage(directory.Root);
        var second = new LocalFileStorage(Path.Combine(directory.Parent, "unopened"));
        await SeedAsync(first, database);
        using var held = new HeldWriter(database);
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        using var caller = new CancellationTokenSource();
        Task firstWrite;
        try
        {
            firstWrite = strategy.IndexAsync(first, "first.md", Bytes("firstneedle")).AsTask();
            Assert.Same(strategy.SqlWorkStarted, await Task.WhenAny(strategy.SqlWorkStarted, firstWrite));
            var canceledWrite = strategy.IndexAsync(second, "second.md", Bytes("secondneedle"), caller.Token).AsTask();
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWrite);
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.False(Directory.Exists(second.RootPath));
            Assert.False(firstWrite.IsCompleted);
        }
        finally { held.Release(); }
        await firstWrite;
        Assert.Empty(await strategy.SearchAsync(first, "secondneedle"));
    }

    [Fact]
    public async Task AdmittedCloseIgnoresLaterCancellationAndPhysicallyReleasesTheDatabase()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        var storage = new LocalFileStorage(directory.Root);
        await SeedAsync(storage, database);
        using var held = new HeldWriter(database);
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        using var caller = new CancellationTokenSource();
        Task pending;
        Task closing;
        try
        {
            pending = strategy.IndexAsync(storage, "new.md", Bytes("committedneedle")).AsTask();
            Assert.Same(strategy.SqlWorkStarted, await Task.WhenAny(strategy.SqlWorkStarted, pending));
            closing = strategy.CloseAsync(caller.Token).AsTask();
            caller.Cancel();
            Assert.False(closing.IsCompleted);
        }
        finally { held.Release(); }
        await pending;
        await closing;
        using (var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
        Assert.Single(await strategy.SearchAsync(storage, "committedneedle"));
    }

    [Fact]
    public async Task CanceledCloseAdmissionKeepsTheInMemoryDatabase()
    {
        using var directory = new TestDirectory();
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root);
        await strategy.IndexAsync(storage, "topic.md", Bytes("retainedneedle"));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => strategy.CloseAsync(caller.Token).AsTask());
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Single(await strategy.SearchAsync(storage, "retainedneedle"));
    }

    [Fact]
    public async Task CorruptDatabaseIsNotDeletedAndCanRetryAfterCallerRepair()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Parent, "corrupt.sqlite");
        var corrupt = Enumerable.Repeat((byte)0x41, 4096).ToArray();
        File.WriteAllBytes(database, corrupt);
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        var storage = new LocalFileStorage(directory.Root);
        await Assert.ThrowsAsync<SqliteException>(() => strategy.SearchAsync(storage, "needle").AsTask());
        Assert.True(corrupt.SequenceEqual(File.ReadAllBytes(database)));
        File.Delete(database);
        await strategy.IndexAsync(storage, "repaired.md", Bytes("repairedneedle"));
        Assert.Single(await strategy.SearchAsync(storage, "repairedneedle"));
    }

    [Fact]
    public async Task DisposedIndexDoesNotOwnStorageAndAWriteFailureFollowsItsByteCommit()
    {
        using var directory = new TestDirectory();
        var strategy = new Bm25SearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root, strategy);
        await storage.WriteAsync("existing.md", Bytes("existing"));
        await strategy.DisposeAsync();
        Assert.Equal("existing", Encoding.UTF8.GetString((await storage.ReadAsync("existing.md"))!));
        Assert.Single(await storage.ListAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => storage.WriteAsync("new.md", Bytes("accepted bytes")).AsTask());
        Assert.Equal("accepted bytes", Encoding.UTF8.GetString((await storage.ReadAsync("new.md"))!));
        await storage.DeleteAsync("existing.md");
        Assert.Null(await storage.ReadAsync("existing.md"));
    }

    [Fact]
    public async Task CallerSelectedFixedDatabaseSharesItsKeyspaceAcrossRoots()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Parent, "shared.sqlite");
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        var first = new LocalFileStorage(directory.Root);
        var second = new LocalFileStorage(Path.Combine(directory.Parent, "other"));
        await strategy.IndexAsync(first, "same.md", Bytes("firstneedle"));
        await strategy.IndexAsync(second, "same.md", Bytes("secondneedle"));
        Assert.Empty(await strategy.SearchAsync(first, "firstneedle"));
        Assert.Equal("same.md", Assert.Single(await strategy.SearchAsync(first, "secondneedle")).Key);
    }

    [Fact]
    public async Task DatabaseFilenameIsLiteralAndParametersDoNotBecomeSql()
    {
        using var directory = new TestDirectory();
        var database = Path.Combine(directory.Parent, "data;mode=memory Café.sqlite");
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        var storage = new LocalFileStorage(directory.Root);
        await strategy.IndexAsync(storage, "quote'key.md", Bytes("Café needle'); DROP TABLE doc_hashes; --"));
        Assert.Single(await strategy.SearchAsync(storage, "CAFÉ needle"));
        await strategy.CloseAsync();
        Assert.True(File.Exists(database));
        Assert.Single(await strategy.SearchAsync(storage, "needle"));
    }

    private static async Task SeedAsync(LocalFileStorage storage, string database)
    {
        await using var strategy = new Bm25SearchStrategy(new() { DatabasePath = database });
        await strategy.IndexAsync(storage, "seed.md", Bytes("seedneedle"));
    }

    private static void Execute(string database, string text)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class HeldWriter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private bool _released;
        internal HeldWriter(string database)
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            _connection.Open();
            _transaction = _connection.BeginTransaction();
        }
        internal void Release()
        {
            if (_released) return;
            _released = true;
            _transaction.Dispose();
            _connection.Dispose();
        }
        public void Dispose() => Release();
    }
}
