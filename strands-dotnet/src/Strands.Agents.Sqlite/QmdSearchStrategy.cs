using Microsoft.Data.Sqlite;

namespace Strands.Agents.Storage;

/// <summary>Refreshes a host filesystem index before QMD-style lexical retrieval.</summary>
/// <remarks>This separate strategy leaves Python-style incremental BM25 and default keyword search unchanged.</remarks>
public sealed partial class QmdSearchStrategy : StorageSearchStrategy, IAsyncDisposable, IScopedStorageSearchStrategy
{
    private readonly string? _databasePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TaskCompletionSource<bool> _workStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _drained;
    private Task? _disposeTask;
    private bool _disposed;
    private int _admitted;
    private SqliteConnection? _connection;
    private string? _storageRoot;

    /// <summary>Creates a lazy strategy without accessing files or opening an index.</summary>
    public QmdSearchStrategy(QmdSearchStrategyOptions? options = null) => _databasePath = options?.DatabasePath;

    // Observes entry to accepted physical work, not a particular SQLite opcode.
    internal Task PhysicalWorkStarted => _workStarted.Task;

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(IStorage storage, string query, CancellationToken cancellationToken = default)
    {
        var root = StorageRoot(storage);
        Guard.NotNull(query, nameof(query));
        return SearchRootAsync(root, query, cancellationToken);
    }

    private ValueTask<IReadOnlyList<StorageSearchResult>> SearchRootAsync(string root, string query, CancellationToken cancellationToken, string? authorityRoot = null)
        => new(ExecuteAsync<IReadOnlyList<StorageSearchResult>>(() =>
        {
            if (authorityRoot is not null)
            {
                CheckLink(authorityRoot);
                var boundary = authorityRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var path = authorityRoot;
                if (root.StartsWith(boundary, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    foreach (var segment in root.Substring(boundary.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                    { path = Path.Combine(path, segment); CheckLink(path); }
            }
            var connection = Connection(root);
            Refresh(connection, root, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var match = BuildMatch(query);
            return match is null ? Array.Empty<StorageSearchResult>() : Query(connection, match);
        }, cancellationToken));

    ValueTask<IReadOnlyList<StorageSearchResult>> IScopedStorageSearchStrategy.SearchScopedAsync(LocalFileStorage storage, string prefix, string query, CancellationToken cancellationToken)
    {
        Guard.NotNull(query, nameof(query));
        var root = storage.RootPath;
        var selected = StorageKey.Prefix(prefix).TrimEnd('/');
        var path = root;
        foreach (var segment in selected.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            path = Path.Combine(path, segment);
        path = Path.GetFullPath(path);
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var boundary = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.Equals(root, path, comparison) && !path.StartsWith(boundary, comparison))
            throw new StorageException("The selected QMD namespace resolves outside its storage root.");
        return SearchRootAsync(path, query, cancellationToken, root);
    }

    /// <summary>Refreshes the selected host root without issuing a search.</summary>
    public ValueTask UpdateAsync(IStorage storage, CancellationToken cancellationToken = default)
    {
        var root = StorageRoot(storage);
        return new(ExecuteAsync(() => { Refresh(Connection(root), root, cancellationToken); return true; }, cancellationToken));
    }

    /// <summary>Closes the index and permits later reuse. Admitted close physically joins preceding work.</summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        => new(ExecuteAsync(() => { CloseConnection(); return true; }, cancellationToken, close: true));

    /// <summary>Rejects new work, joins admitted physical work and releases the owned index.</summary>
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
            if (_disposed) throw new ObjectDisposedException(nameof(QmdSearchStrategy));
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
                _workStarted.TrySetResult(true);
                return action();
            }).ConfigureAwait(false);
            if (!close) cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (!close && cancellationToken.IsCancellationRequested)
        {
            if (error is OperationCanceledException canceled && canceled.CancellationToken == cancellationToken) throw;
            throw new OperationCanceledException("QMD storage work was canceled by its caller.", error, cancellationToken);
        }
        finally
        {
            if (entered) _serial.Release();
            TaskCompletionSource<bool>? drained = null;
            lock (_gate) { if (--_admitted == 0) drained = _drained; }
            drained?.TrySetResult(true);
        }
    }

    private static string StorageRoot(IStorage storage)
    {
        Guard.NotNull(storage, nameof(storage));
        return storage is LocalFileStorage local ? local.RootPath : throw new ArgumentException("QMD requires a direct host LocalFileStorage.", nameof(storage));
    }
}
