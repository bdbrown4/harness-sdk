using System.Text;
using Microsoft.Data.Sqlite;
using Strands.Agents.Storage;

namespace Strands.Sqlite.Tests;

public sealed class QmdSearchStrategyLifetimeTests
{
    [Theory]
    [InlineData("search")]
    [InlineData("update")]
    [InlineData("close")]
    public async Task CanceledAdmissionDoesNotCreateTheSelectedRootOrIndex(string operation)
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var storage = new LocalFileStorage(directory.Root);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "search" => strategy.SearchAsync(storage, "needle", caller.Token).AsTask(),
            "update" => strategy.UpdateAsync(storage, caller.Token).AsTask(),
            _ => strategy.CloseAsync(caller.Token).AsTask()
        });
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.False(Directory.Exists(directory.Root));
        Assert.False(Directory.Exists(directory.Parent));
    }

    [Fact]
    public async Task CanceledPhysicalSqlAndDisposeStayPendingUntilOwnedNativeWorkReturns()
    {
        using var directory = new QmdDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        var storage = new LocalFileStorage(directory.Root);
        directory.Write("seed.md", "seedneedle");
        await SeedAsync(storage, database);
        using var held = new QmdHeldWriter(database);
        var strategy = new QmdSearchStrategy(new() { DatabasePath = database });
        using var caller = new CancellationTokenSource();
        Task pending = Task.CompletedTask;
        Task disposing = Task.CompletedTask;
        try
        {
            pending = strategy.SearchAsync(storage, "seedneedle", caller.Token).AsTask();
            Assert.Same(strategy.PhysicalWorkStarted, await Task.WhenAny(strategy.PhysicalWorkStarted, pending));
            caller.Cancel();
            Assert.False(pending.IsCompleted);
            disposing = strategy.DisposeAsync().AsTask();
            Assert.Same(disposing, strategy.DisposeAsync().AsTask());
            Assert.False(disposing.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => strategy.SearchAsync(storage, "needle").AsTask());
        }
        finally { held.Release(); }
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        finally { await strategy.DisposeAsync(); }
        await disposing;
        using (var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
        // Storage remains borrowed and usable after index disposal.
        await storage.WriteAsync("later.md", Encoding.UTF8.GetBytes("laterbytes"));
        Assert.Equal("laterbytes", Encoding.UTF8.GetString((await storage.ReadAsync("later.md"))!));
    }

    [Fact]
    public async Task CancelingASerialWaitDoesNoPhysicalWorkForAnotherRoot()
    {
        using var directory = new QmdDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        var first = new LocalFileStorage(directory.Root);
        var second = new LocalFileStorage(Path.Combine(directory.Parent, "unopened"));
        directory.Write("seed.md", "seedneedle");
        await SeedAsync(first, database);
        using var held = new QmdHeldWriter(database);
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = database });
        using var caller = new CancellationTokenSource();
        Task pending = Task.CompletedTask;
        try
        {
            pending = strategy.SearchAsync(first, "seedneedle").AsTask();
            Assert.Same(strategy.PhysicalWorkStarted, await Task.WhenAny(strategy.PhysicalWorkStarted, pending));
            var waiting = strategy.SearchAsync(second, "needle", caller.Token).AsTask();
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.False(Directory.Exists(second.RootPath));
            Assert.False(pending.IsCompleted);
        }
        finally { held.Release(); }
        await pending;
    }

    [Fact]
    public async Task AdmittedCloseIgnoresLaterCancellationAndReopensAfterPhysicalJoin()
    {
        using var directory = new QmdDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        var storage = new LocalFileStorage(directory.Root);
        directory.Write("seed.md", "seedneedle");
        await SeedAsync(storage, database);
        using var held = new QmdHeldWriter(database);
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = database });
        using var caller = new CancellationTokenSource();
        Task pending = Task.CompletedTask;
        Task closing = Task.CompletedTask;
        try
        {
            pending = strategy.SearchAsync(storage, "seedneedle").AsTask();
            Assert.Same(strategy.PhysicalWorkStarted, await Task.WhenAny(strategy.PhysicalWorkStarted, pending));
            closing = strategy.CloseAsync(caller.Token).AsTask();
            caller.Cancel();
            Assert.False(closing.IsCompleted);
        }
        finally { held.Release(); }
        await pending;
        await closing;
        using (var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.True(exclusive.Length > 0);
        Assert.Equal("seed.md", Assert.Single(await strategy.SearchAsync(storage, "seedneedle")).Key);
    }

    [Fact]
    public async Task IndependentSqlFailureRollsBackAndCanBeRetriedAfterRepair()
    {
        using var directory = new QmdDirectory();
        var database = Path.Combine(directory.Parent, "index.sqlite");
        var storage = new LocalFileStorage(directory.Root);
        directory.Write("seed.md", "seedneedle");
        await SeedAsync(storage, database);
        Execute(database, "CREATE TRIGGER reject_insert BEFORE INSERT ON qmd_documents BEGIN SELECT RAISE(FAIL, 'independent SQL failure'); END");
        directory.Write("new.md", "newneedle");
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = database });
        var error = await Assert.ThrowsAsync<SqliteException>(() => strategy.SearchAsync(storage, "newneedle").AsTask());
        Assert.Contains("independent SQL failure", error.Message);
        Execute(database, "DROP TRIGGER reject_insert");
        Assert.Equal("new.md", Assert.Single(await strategy.SearchAsync(storage, "newneedle")).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOrCorruptDatabaseIsRetainedUntilCallerRepairsIt(bool corrupt)
    {
        using var directory = new QmdDirectory();
        directory.Write("topic.md", "repairedneedle");
        var database = Path.Combine(directory.Parent, "foreign.sqlite");
        if (corrupt) File.WriteAllBytes(database, Enumerable.Repeat((byte)0x41, 4096).ToArray());
        else Execute(database, "CREATE TABLE existing_authority(value TEXT); INSERT INTO existing_authority VALUES ('keep')");
        var before = File.ReadAllBytes(database);
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = database });
        if (corrupt) await Assert.ThrowsAsync<SqliteException>(() => strategy.SearchAsync(new LocalFileStorage(directory.Root), "needle").AsTask());
        else await Assert.ThrowsAsync<InvalidDataException>(() => strategy.SearchAsync(new LocalFileStorage(directory.Root), "needle").AsTask());
        Assert.True(before.SequenceEqual(File.ReadAllBytes(database)));
        File.Delete(database);
        Assert.Single(await strategy.SearchAsync(new LocalFileStorage(directory.Root), "repairedneedle"));
    }

    [Fact]
    public async Task DefaultSiblingIndexSurvivesCloseAndEmptyContentAcrossInstances()
    {
        using var directory = new QmdDirectory();
        directory.Write("topic.md", "persistentneedle");
        var storage = new LocalFileStorage(directory.Root);
        var strategy = new QmdSearchStrategy();
        Assert.Single(await strategy.SearchAsync(storage, "persistentneedle"));
        await strategy.CloseAsync();
        await strategy.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(directory.Parent, ".files-qmd.sqlite")));
        directory.Write("topic.md", " \t\n");
        await using var restored = new QmdSearchStrategy();
        Assert.Single(await restored.SearchAsync(storage, "persistentneedle"));
    }

    [Fact]
    public async Task SwitchingRootsRefreshesTheSelectedIndexWithoutLeakingPreviousKeys()
    {
        using var directory = new QmdDirectory();
        directory.Write("first.md", "firstneedle");
        var second = Path.Combine(directory.Parent, "second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "second.md"), "secondneedle");
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        Assert.Single(await strategy.SearchAsync(new LocalFileStorage(directory.Root), "firstneedle"));
        Assert.Empty(await strategy.SearchAsync(new LocalFileStorage(second), "firstneedle"));
        Assert.Single(await strategy.SearchAsync(new LocalFileStorage(second), "secondneedle"));
        Assert.Single(await strategy.SearchAsync(new LocalFileStorage(directory.Root), "firstneedle"));
    }

    [Fact]
    public async Task ConcurrentAdmittedNamespacesStayBoundToTheirSelectedPhysicalRoots()
    {
        using var directory = new QmdDirectory();
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = ":memory:" });
        var storage = new LocalFileStorage(directory.Root, strategy);
        for (var index = 0; index < 12; index++) directory.Write($"scope-{index}/topic.md", $"marker{index} unique source");
        var work = Enumerable.Range(0, 12).Select(async index =>
        {
            var result = Assert.Single(await storage.Namespace($"scope-{index}").SearchAsync($"marker{index}"));
            Assert.Equal("topic.md", result.Key);
            Assert.Empty(await storage.Namespace($"scope-{index}").SearchAsync($"marker{(index + 1) % 12}"));
        });
        await Task.WhenAll(work);
    }

    [Fact]
    public async Task LiteralDatabaseFilenameAndQuotedKeysNeverBecomeConnectionOptionsOrSql()
    {
        using var directory = new QmdDirectory();
        directory.Write("quote'key.md", "# Literal\nneedle'); DROP TABLE qmd_documents; --");
        var database = Path.Combine(directory.Parent, "data;mode=memory.sqlite");
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = database });
        var result = Assert.Single(await strategy.SearchAsync(new LocalFileStorage(directory.Root), "needle"));
        Assert.Equal("quote'key.md", result.Key);
        await strategy.CloseAsync();
        Assert.True(File.Exists(database));
        Assert.Single(await strategy.SearchAsync(new LocalFileStorage(directory.Root), "needle"));
    }

    private static async Task SeedAsync(LocalFileStorage storage, string database)
    {
        await using var strategy = new QmdSearchStrategy(new() { DatabasePath = database });
        await strategy.UpdateAsync(storage);
    }

    private static void Execute(string database, string text)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }

    private sealed class QmdHeldWriter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private bool _released;
        internal QmdHeldWriter(string database)
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            _connection.Open();
            _transaction = _connection.BeginTransaction();
        }
        internal void Release()
        {
            if (_released) return;
            _released = true;
            try { _transaction.Dispose(); }
            finally { _connection.Dispose(); }
        }
        public void Dispose() => Release();
    }
}
