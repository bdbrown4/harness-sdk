using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Strands.Agents.Storage;

/// <summary>Maintains an incremental host SQLite FTS5 index with Python-style BM25 retrieval.</summary>
/// <remarks>Only indexed writes are searchable. Close permits reuse; disposal is terminal. Storage remains borrowed.</remarks>
public sealed class Bm25SearchStrategy : StorageSearchStrategy, IAsyncDisposable
{
    private readonly string? _databasePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TaskCompletionSource<bool> _sqlStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _drained;
    private Task? _disposeTask;
    private bool _disposed;
    private int _admitted;
    private SqliteConnection? _connection;
    private string? _storageRoot;
    private static readonly HashSet<string> StopWords = new(StopWordText.Split(' '), StringComparer.Ordinal);
    private const string StopWordText = "a about above after again all although an and any are as at be because been before being below between both but by can could dare did do does during each either every few for from further had has have he her him his how i if in into is it its just may me might more most my need neither no nor not of off on once only or other ought our out over own same shall she should since so some such than that the their them then these they this those though through to too under unless until up us used very was we were what when where which while who whom why will with would yet you your";

    /// <summary>Creates a strategy without accessing storage or opening a database.</summary>
    public Bm25SearchStrategy(Bm25SearchStrategyOptions? options = null) => _databasePath = options?.DatabasePath;

    // This observes accepted synchronous SQL action entry, not a particular SQLite opcode.
    internal Task SqlWorkStarted => _sqlStarted.Task;

    /// <inheritdoc />
    public override ValueTask IndexAsync(IStorage storage, string key, byte[] data, CancellationToken cancellationToken = default)
    {
        var root = StorageRoot(storage);
        Guard.NotNull(data, nameof(data));
        var normalized = StorageKey.Normalize(key);
        CheckAdmission(cancellationToken);
        if (normalized.Substring(normalized.LastIndexOf('/') + 1).StartsWith(".", StringComparison.Ordinal)) return default;
        var copy = (byte[])data.Clone();
        return new(ExecuteAsync(() => { Upsert(Connection(root), normalized, copy); return true; }, cancellationToken));
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default)
    {
        var root = StorageRoot(storage);
        Guard.NotNull(query, nameof(query));
        CheckAdmission(cancellationToken);
        var terms = KeywordSearchStrategy.Tokenize(query).Where(term => term.Length > 1 && !StopWords.Contains(term)).OrderBy(term => term, StringComparer.Ordinal).ToArray();
        if (terms.Length == 0) return new(Array.Empty<StorageSearchResult>());
        var match = string.Join(" ", terms.Select(term => term + "*"));
        return new(ExecuteAsync<IReadOnlyList<StorageSearchResult>>(() => Query(Connection(root), match), cancellationToken));
    }

    /// <summary>Closes the owned connection and permits reuse. Cancellation controls admission only; admitted close always joins.</summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        => new(ExecuteAsync(() => { CloseConnection(); return true; }, cancellationToken, close: true));

    /// <summary>Rejects new operations, joins admitted physical work, and releases only this strategy's connection.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? completion = null;
        Task wait = Task.CompletedTask;
        Task result;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                wait = _admitted == 0 ? Task.CompletedTask : _drained!.Task;
            }
            result = _disposeTask;
        }
        if (completion is not null) _ = DisposeCoreAsync(wait, completion);
        return new(result);
    }

    private async Task DisposeCoreAsync(Task wait, TaskCompletionSource<bool> completion)
    {
        try
        {
            await wait.ConfigureAwait(false);
            try { await Task.Run(CloseConnection).ConfigureAwait(false); }
            finally { _serial.Dispose(); }
            completion.TrySetResult(true);
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken cancellationToken, bool close = false)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(Bm25SearchStrategy));
            if (_admitted++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return RunAsync(action, cancellationToken, close);
    }

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken, bool close)
    {
        var entered = false;
        try
        {
            await _serial.WaitAsync(close ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            entered = true;
            var result = await Task.Run(() =>
            {
                if (!close) cancellationToken.ThrowIfCancellationRequested();
                _sqlStarted.TrySetResult(true);
                return action();
            }).ConfigureAwait(false);
            if (!close) cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (entered) _serial.Release();
            TaskCompletionSource<bool>? drained = null;
            lock (_gate) { if (--_admitted == 0) drained = _drained; }
            drained?.TrySetResult(true);
        }
    }

    private void CheckAdmission(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(Bm25SearchStrategy));
        }
    }

    private static string StorageRoot(IStorage storage)
    {
        Guard.NotNull(storage, nameof(storage));
        return storage is LocalFileStorage local ? local.RootPath : throw new ArgumentException("BM25 requires a direct host LocalFileStorage.", nameof(storage));
    }

    private SqliteConnection Connection(string root)
    {
        if (_connection is not null && _storageRoot == root) return _connection;
        CloseConnection();
        var path = DatabasePath(root);
        Directory.CreateDirectory(root);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open();
            Transaction(connection, transaction =>
            {
                Execute(connection, transaction, "CREATE VIRTUAL TABLE IF NOT EXISTS documents USING fts5(key, content)");
                Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS doc_hashes (key TEXT PRIMARY KEY, hash TEXT NOT NULL)");
            });
        }
        catch (Exception error)
        {
            try { connection.Dispose(); }
            catch (Exception cleanup) { AttachCleanup(error, cleanup); }
            throw;
        }
        _connection = connection;
        _storageRoot = root;
        return connection;
    }

    private string DatabasePath(string root)
    {
        if (_databasePath is { } configured)
            return configured is "" or ":memory:" ? configured : Path.GetFullPath(configured);
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var isRoot = trimmed.Length < (Path.GetPathRoot(root)?.Length ?? 0);
        var basename = isRoot ? "" : Path.GetFileName(trimmed);
        var parent = isRoot ? root : Path.GetDirectoryName(trimmed) ?? root;
        return Path.Combine(parent, ".__strands_" + basename + "-fts5.sqlite");
    }

    private void CloseConnection()
    {
        var connection = _connection;
        _connection = null;
        _storageRoot = null;
        connection?.Dispose();
    }

    private static void Upsert(SqliteConnection connection, string key, byte[] data)
    {
        using var fingerprint = MD5.Create();
        var hash = BitConverter.ToString(fingerprint.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT hash FROM doc_hashes WHERE key = $key";
            lookup.Parameters.AddWithValue("$key", key);
            if (string.Equals(lookup.ExecuteScalar() as string, hash, StringComparison.Ordinal)) return;
        }
        var content = Encoding.UTF8.GetString(data);
        Transaction(connection, transaction =>
        {
            Execute(connection, transaction, "DELETE FROM documents WHERE key = $key", ("$key", key));
            Execute(connection, transaction, "INSERT INTO documents (key, content) VALUES ($key, $content)", ("$key", key), ("$content", content));
            Execute(connection, transaction, "INSERT OR REPLACE INTO doc_hashes (key, hash) VALUES ($key, $hash)", ("$key", key), ("$hash", hash));
        });
    }

    private static IReadOnlyList<StorageSearchResult> Query(SqliteConnection connection, string match)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, rank FROM documents WHERE documents MATCH $match ORDER BY rank";
        command.Parameters.AddWithValue("$match", match);
        using var reader = command.ExecuteReader();
        var results = new List<StorageSearchResult>();
        while (reader.Read())
        {
            var raw = Math.Abs(reader.GetDouble(1));
            results.Add(new(reader.GetString(0), raw / (1 + raw)));
        }
        return results.ToArray();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string text, params (string Key, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static void Transaction(SqliteConnection connection, Action<SqliteTransaction> action)
    {
        var transaction = connection.BeginTransaction();
        Exception? primary = null;
        try { action(transaction); transaction.Commit(); }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { transaction.Dispose(); }
            catch (Exception cleanup) when (primary is not null) { AttachCleanup(primary, cleanup); }
        }
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }
}
