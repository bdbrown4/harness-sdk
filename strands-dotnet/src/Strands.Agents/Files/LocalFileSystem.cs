using System.Text;
using Strands.Agents.Storage;

namespace Strands.Agents.Files;

/// <summary>Accesses host files and directories with process permissions; this class provides no filesystem isolation.</summary>
/// <remarks>Byte operations use LocalFileStorage's atomic replacement and link policy. The byte reader is independent of the text-read limit.</remarks>
public sealed class LocalFileSystem : IFileSystem, IBoundedFileReader, IStrictFileRemover
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    /// <summary>Maximum bytes accepted by one text read.</summary>
    public int MaxReadBytes { get; }
    /// <summary>Creates a host reader with a default text-read limit of 5 MiB.</summary>
    public LocalFileSystem(int maxReadBytes = 5 * 1024 * 1024)
    {
        if (maxReadBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxReadBytes));
        MaxReadBytes = maxReadBytes;
    }
    /// <inheritdoc />
    public async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaxReadBytes) throw new IOException("The text file exceeds the configured byte limit.");
        using var bytes = new MemoryStream(Math.Min(MaxReadBytes, 8192));
        var buffer = new byte[8192];
        while (true)
        {
            var count = await file.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            if (bytes.Length + count > MaxReadBytes) throw new IOException("The text file exceeds the configured byte limit.");
            bytes.Write(buffer, 0, count);
        }
        var content = bytes.ToArray();
        var offset = content.Length >= 3 && content[0] == 0xef && content[1] == 0xbb && content[2] == 0xbf ? 3 : 0;
        return StrictUtf8.GetString(content, offset, content.Length - offset);
    }
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<FileEntry>();
        foreach (var item in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(item);
            entries.Add(CaptureListedEntry(name, () =>
            {
                var attributes = File.GetAttributes(item);
                var directory = (attributes & FileAttributes.Directory) != 0;
                var link = (attributes & FileAttributes.ReparsePoint) != 0;
                var size = directory || link ? (long?)null : new FileInfo(item).Length;
                return FileEntry.FromMetadata(name, directory, size, link);
            }, cancellationToken));
        }
        return new(Array.AsReadOnly(entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray()));
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backend = Backend(path, out var key);
        return await backend.ReadAsync(key, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("The requested file does not exist.", path);
    }
    /// <inheritdoc />
    public async ValueTask<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var absolute = Path.GetFullPath(path);
        // Match the generic byte backend's no-link policy. This uses host process permissions;
        // link checks provide no authorization policy or race-free filesystem isolation.
        foreach (var current in new[] { Path.GetDirectoryName(absolute)!, absolute })
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links and reparse points are not supported within the local byte reader's parent root.");
        using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        if (stream.Length > maximumBytes) throw new IOException("The media file exceeds the configured byte limit.");
        using var bytes = new MemoryStream(Math.Min(maximumBytes, 8192));
        var buffer = new byte[Math.Min(maximumBytes, 8192)];
        while (true)
        {
            // Read one byte beyond remaining authority to detect a file that grows after Length.
            var remaining = (long)maximumBytes - bytes.Length;
            var count = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining + 1), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) break;
            if (count > remaining) throw new IOException("The media file exceeds the configured byte limit.");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
    /// <inheritdoc />
    public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Backend(path, out var key).WriteAsync(key, data, cancellationToken);
    }
    /// <inheritdoc />
    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Backend(path, out var key).DeleteAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask RemoveFileAsync(string path, CancellationToken cancellationToken = default)
    {
        LocalFileRemoval.Remove(path, cancellationToken);
        return default;
    }

    internal static FileEntry CaptureListedEntry(string name, Func<FileEntry> readMetadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var entry = readMetadata();
            cancellationToken.ThrowIfCancellationRequested();
            return entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return FileEntry.FromMetadata(name, null);
        }
    }

    private static LocalFileStorage Backend(string path, out string key)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        var absolute = Path.GetFullPath(path);
        key = Path.GetFileName(absolute);
        return new LocalFileStorage(Path.GetDirectoryName(absolute)!);
    }
}
