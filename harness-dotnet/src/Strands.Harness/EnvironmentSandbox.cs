namespace Strands.Harness;

/// <summary>The optional environment-discovery capabilities of a sandbox, describing the environment where its files live.</summary>
public interface IEnvironmentSandbox : ISandbox
{
    /// <summary>Returns the sandbox's absolute working directory.</summary>
    ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns the sandbox's platform description, or null when unavailable.</summary>
    ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns the direct children of an absolute sandbox directory.</summary>
    ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>One file or directory returned by a sandbox directory listing.</summary>
public sealed record SandboxEntry
{
    /// <summary>Creates a directory entry whose name is a single relative path segment.</summary>
    public SandboxEntry(string name, bool isDirectory)
    {
        Guard.NotBlank(name, nameof(name));
        if (name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            throw new ArgumentException("A directory entry name must be a single path segment.", nameof(name));
        Name = name;
        IsDirectory = isDirectory;
    }

    /// <summary>The entry's filename without parent directory segments.</summary>
    public string Name { get; }
    /// <summary>Whether this entry is a directory.</summary>
    public bool IsDirectory { get; }
}
