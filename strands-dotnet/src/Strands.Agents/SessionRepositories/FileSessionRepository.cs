using Strands.Agents.Storage;

namespace Strands.Agents.SessionRepositories;

/// <summary>Indexed repository using atomically replaced files beneath an explicit application-selected directory.</summary>
public sealed class FileSessionRepository : StorageSessionRepository
{
    /// <summary>Creates the byte-storage adapter. No home directory is selected implicitly.</summary>
    public FileSessionRepository(string directory, SessionRepositoryOptions? options = null) : this(CreateStorage(directory), options) { }
    private FileSessionRepository(LocalFileStorage storage, SessionRepositoryOptions? options) : base(storage, options) { RootPath = storage.RootPath; }
    /// <summary>The absolute selected file root.</summary>
    public string RootPath { get; }
    private static LocalFileStorage CreateStorage(string directory)
    {
        Guard.NotNullOrWhiteSpace(directory, nameof(directory));
        var root = Path.GetFullPath(directory);
        if (Path.DirectorySeparatorChar == '\\' && !root.StartsWith(@"\\?\", StringComparison.Ordinal))
            root = root.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + root.Substring(2) : @"\\?\" + root;
        return new(root);
    }
}
