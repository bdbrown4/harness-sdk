namespace Strands.Agents.Files;

/// <summary>Reads text and directory entries from one filesystem under its implementation's access policy.</summary>
public interface IReadOnlyFileSystem
{
    /// <summary>Reads a text file. A missing path throws a filesystem exception.</summary>
    ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Lists direct children of a directory. A missing directory throws a filesystem exception.</summary>
    ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>A direct child of a filesystem directory.</summary>
public sealed record FileEntry
{
    private readonly bool? _directoryStatus;
    private readonly long? _size;
    /// <summary>Creates an entry whose name is one relative path segment.</summary>
    public FileEntry(string name, bool isDirectory, bool isSymbolicLink = false)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        if (name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            throw new ArgumentException("A directory entry name must be a single path segment.", nameof(name));
        Name = name;
        IsDirectory = isDirectory;
        IsSymbolicLink = isSymbolicLink;
        _directoryStatus = isDirectory;
    }
    private FileEntry(string name, bool? isDirectory, long? size, bool isSymbolicLink) : this(name, isDirectory == true, isSymbolicLink)
    {
        _directoryStatus = isDirectory;
        _size = size;
    }
    /// <summary>The filename without parent directory segments.</summary>
    public string Name { get; }
    /// <summary>Whether the entry is a directory.</summary>
    public bool IsDirectory { get; }
    /// <summary>Whether the entry is a symbolic link or reparse point under the backend's policy.</summary>
    public bool IsSymbolicLink { get; }
    /// <summary>The reported directory status, or null when it is unknown.</summary>
    public bool? DirectoryStatus => _directoryStatus;
    /// <summary>The reported byte size, or null when it is unknown.</summary>
    public long? Size => _size;
    /// <summary>Creates an entry from the backend's optional metadata.</summary>
    public static FileEntry FromMetadata(string name, bool? isDirectory, long? size = null, bool isSymbolicLink = false) => new(name, isDirectory, size, isSymbolicLink);
}
