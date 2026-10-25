namespace Strands.Agents.Files;

/// <summary>Optional byte-exact read, write, and delete capabilities for the selected filesystem.</summary>
public interface IFileSystem : IReadOnlyFileSystem
{
    /// <summary>Reads an independent byte buffer; missing files throw FileNotFoundException or DirectoryNotFoundException.</summary>
    ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Creates parent directories and replaces complete file bytes without retaining the caller's mutable buffer.</summary>
    ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default);
    /// <summary>Deletes a file; a missing file is a no-op.</summary>
    ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default);
}
