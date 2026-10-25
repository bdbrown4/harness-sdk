using Strands.Agents.Files;
using Strands.Agents.Storage;

namespace Strands.Harness.Storage;

internal sealed class SandboxStorage : IStorage, IStorageCharacteristics
{
    private readonly IFileSystem _files;
    private readonly string _directory;
    internal SandboxStorage(IFileSystem files, string directory)
    {
        Guard.NotNull(files, nameof(files)); Guard.NotBlank(directory, nameof(directory));
        _files = files;
        _directory = directory.Replace('\\', '/').TrimEnd('/');
        if (_directory.Length == 0) _directory = "/";
    }
    public bool IsEphemeral => false;
    public bool IsNamespaced => true;
    public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(data, nameof(data));
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(key, false);
        return new(WriteCoreAsync(normalized, (byte[])data.Clone(), cancellationToken));
    }

    private async Task WriteCoreAsync(string key, byte[] data, CancellationToken cancellationToken)
    {
        try { await _files.WriteBytesAsync(PathFor(key), data, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (BackendError(error)) { throw new StorageException("Failed to write sandbox storage key '" + key + "'.", error); }
    }

    public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(key, false);
        try { return (byte[])(await _files.ReadBytesAsync(PathFor(normalized), cancellationToken).ConfigureAwait(false)).Clone(); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception error) when (BackendError(error)) { throw new StorageException("Failed to read sandbox storage key '" + normalized + "'.", error); }
    }

    public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(key, false);
        try { await _files.DeleteFileAsync(PathFor(normalized), cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (BackendError(error)) { throw new StorageException("Failed to delete sandbox storage key '" + normalized + "'.", error); }
    }

    public async ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(prefix, true);
        var keys = new List<string>();
        try { await WalkAsync("", 0).ConfigureAwait(false); }
        catch (Exception error) when (BackendError(error)) { throw new StorageException("Failed to list sandbox storage prefix '" + normalized + "'.", error); }
        return Array.AsReadOnly(keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());

        async Task WalkAsync(string relative, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 256) throw new StorageException("Sandbox storage listing exceeds 256 directory levels. The backend may contain a directory cycle.");
            IReadOnlyList<FileEntry> entries;
            try { entries = await _files.ListFilesAsync(relative.Length == 0 ? _directory : PathFor(relative), cancellationToken).ConfigureAwait(false); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsSymbolicLink) throw new StorageException("Sandbox storage does not traverse symbolic links or reparse points.");
                var key = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (entry.IsDirectory) await WalkAsync(key, depth + 1).ConfigureAwait(false);
                else if (key.StartsWith(normalized, StringComparison.Ordinal)) keys.Add(key);
            }
        }
    }

    private string PathFor(string key) => _directory.TrimEnd('/') + "/" + key;
    private static bool BackendError(Exception error) => error is IOException or UnauthorizedAccessException && error is not StorageException;
    private static string Normalize(string key, bool prefix)
    {
        Guard.NotNull(key, nameof(key));
        if (key.IndexOfAny(new[] { '\\', '\0' }) >= 0) throw new StorageException("Storage keys cannot contain backslashes or null characters.");
        var segments = key.Split('/').Where(segment => segment.Length > 0).ToArray();
        if (segments.Contains("..", StringComparer.Ordinal)) throw new StorageException("Storage keys cannot contain '..' path segments.");
        var value = string.Join("/", segments);
        if (value.Length == 0 && !prefix) throw new StorageException("Storage keys cannot be empty.");
        return prefix && value.Length > 0 && key.EndsWith("/", StringComparison.Ordinal) ? value + "/" : value;
    }
}
