namespace Strands.Agents.Files;
/// <summary>An optional byte reader that enforces a per-read allocation bound without truncating files.</summary>
public interface IBoundedFileReader
{
    /// <summary>Reads a complete file into an independent buffer, throwing IOException when its size exceeds maximumBytes. Implementations must bound allocation and detect growth during the read.</summary>
    ValueTask<byte[]> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken = default);
}
