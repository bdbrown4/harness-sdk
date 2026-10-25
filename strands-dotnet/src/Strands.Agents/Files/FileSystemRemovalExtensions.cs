namespace Strands.Agents.Files;

/// <summary>Strict removal through an explicitly selected filesystem capability.</summary>
public static class FileSystemRemovalExtensions
{
    /// <summary>Removes a named file through the selected backend's optional strict-removal capability.</summary>
    /// <exception cref="NotSupportedException">The selected filesystem does not provide strict removal.</exception>
    /// <remarks>Legacy DeleteFileAsync keeps its missing-file no-op contract. This operation has no host fallback or read-before-delete substitute.</remarks>
    public static ValueTask RemoveFileAsync(this IFileSystem fileSystem, string path, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(fileSystem, nameof(fileSystem));
        cancellationToken.ThrowIfCancellationRequested();
        if (fileSystem is not IStrictFileRemover remover) throw new NotSupportedException("The selected filesystem does not provide strict file removal.");
        return remover.RemoveFileAsync(path, cancellationToken);
    }
}
