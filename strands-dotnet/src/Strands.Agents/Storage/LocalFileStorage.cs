namespace Strands.Agents.Storage;

/// <summary>Persists byte values as files below an absolute root, replacing complete files atomically.</summary>
/// <remarks>This backend uses the host filesystem. Use a sandbox-bound backend for remote execution.</remarks>
public sealed class LocalFileStorage : ISearchableStorage, IStorageCharacteristics
{
    private readonly StorageSearchStrategy _searchStrategy;
    /// <summary>Creates a file store. The root is created lazily on the first write.</summary>
    public LocalFileStorage(string directory = ".strands", StorageSearchStrategy? searchStrategy = null)
    {
        Guard.NotNullOrWhiteSpace(directory, nameof(directory));
        RootPath = Path.GetFullPath(directory);
        _searchStrategy = searchStrategy ?? new KeywordSearchStrategy();
    }
    /// <summary>The absolute filesystem root.</summary>
    public string RootPath { get; }
    /// <inheritdoc />
    public bool IsEphemeral => false;
    /// <inheritdoc />
    public bool IsNamespaced => false;
    /// <inheritdoc />
    public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(data, nameof(data));
        var normalized = StorageKey.Normalize(key);
        var path = GetPath(normalized);
        cancellationToken.ThrowIfCancellationRequested();
        var copy = (byte[])data.Clone();
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".__strands_tmp_" + Path.GetRandomFileName());
        var temporaryCreated = false;
        Exception? failure = null;
        try
        {
            RejectLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RejectLinks(path);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                temporaryCreated = true;
                await stream.WriteAsync(copy, 0, copy.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(path);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else
            {
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { File.Replace(temporary, path, null); }
            }
        }
        catch (Exception error) when (IsBackendError(error))
        { failure = new StorageException($"Failed to write storage key '{normalized}'.", error); throw failure; }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { if (temporaryCreated && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanup) when (failure is not null)
            {
                if (failure.Data["Strands.CleanupErrors"] is not List<Exception> errors) failure.Data["Strands.CleanupErrors"] = errors = [];
                errors.Add(cleanup);
            }
        }
        await _searchStrategy.IndexAsync(this, normalized, (byte[])copy.Clone(), cancellationToken).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Normalize(key);
        var path = GetPath(normalized);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            RejectLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
            using var data = new MemoryStream();
            await stream.CopyToAsync(data, 81920, cancellationToken).ConfigureAwait(false);
            return data.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (IsBackendError(error)) { throw new StorageException($"Failed to read storage key '{normalized}'.", error); }
    }
    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Normalize(key);
        var path = GetPath(normalized);
        cancellationToken.ThrowIfCancellationRequested();
        try { RejectLinks(path); File.Delete(path); }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (IsBackendError(error)) { throw new StorageException($"Failed to delete storage key '{normalized}'.", error); }
        return default;
    }
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        var normalized = StorageKey.Prefix(prefix);
        cancellationToken.ThrowIfCancellationRequested();
        // Completed dot-directory segments share the physical aliases used by file operations.
        // Preserve a final partial dot segment so "." continues to select dotfiles.
        var segments = normalized.Split('/');
        var physicalPrefix = string.Join("/", segments.Where((segment, index) => segment != "." || index == segments.Length - 1));
        var keys = new List<string>();
        try { RejectLinks(RootPath); Walk(RootPath, "", physicalPrefix, keys, cancellationToken); }
        catch (Exception error) when (IsBackendError(error)) { throw new StorageException($"Failed to list storage prefix '{normalized}'.", error); }
        if (physicalPrefix != normalized)
            for (var index = 0; index < keys.Count; index++) keys[index] = normalized + keys[index].Substring(physicalPrefix.Length);
        return new(keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
    }
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StorageSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default) => _searchStrategy.SearchAsync(this, query, cancellationToken);

    internal bool TrySearchScoped(string prefix, string query, CancellationToken cancellationToken, out ValueTask<IReadOnlyList<StorageSearchResult>> results)
    {
        if (_searchStrategy is IScopedStorageSearchStrategy scoped)
        {
            results = scoped.SearchScopedAsync(this, prefix, query, cancellationToken);
            return true;
        }
        results = default;
        return false;
    }

    private string GetPath(string key)
    {
        try
        {
            // Extended Windows roots skip normal dot folding; file keys retain the
            // same physical aliases as ordinary roots without changing logical storage keys.
            var relative = string.Join(Path.DirectorySeparatorChar.ToString(), key.Split('/').Where(segment => segment != "."));
            var path = Path.GetFullPath(Path.Combine(RootPath, relative));
            var root = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(root, comparison)) throw new StorageException("The storage key resolves outside its root.");
            return path;
        }
        catch (ArgumentException error) { throw new StorageException("The storage key is not a valid filesystem path.", error); }
        catch (NotSupportedException error) { throw new StorageException("The storage key is not a valid filesystem path.", error); }
    }

    private void RejectLinks(string path)
    {
        var relative = path.Substring(RootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = RootPath;
        CheckLink(current);
        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        { current = Path.Combine(current, segment); CheckLink(current); }
    }

    private static void CheckLink(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new StorageException("Local storage does not follow symbolic links or reparse points within its root."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void Walk(string directory, string prefix, string filter, List<string> keys, CancellationToken cancellationToken)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(directory); }
        catch (DirectoryNotFoundException) { return; }
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(entry); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new StorageException("Local storage does not list symbolic links or reparse points within its root.");
            var key = prefix + name;
            if ((attributes & FileAttributes.Directory) != 0) Walk(entry, key + "/", filter, keys, cancellationToken);
            else if (!name.Contains(".__strands_tmp") && key.StartsWith(filter, StringComparison.Ordinal)) keys.Add(key);
        }
    }

    private static bool IsBackendError(Exception error) => error is IOException or UnauthorizedAccessException && error is not StorageException;
}
