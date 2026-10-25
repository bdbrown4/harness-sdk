namespace Strands.Agents.Files;

/// <summary>Optional removal of a named file with failure when that name is absent.</summary>
/// <remarks>Implementations define their selected filesystem authority. This capability does not imply isolation or physical draining of other open handles.</remarks>
public interface IStrictFileRemover
{
    /// <summary>Removes the named file; an absent path or an unsuccessful removal throws.</summary>
    /// <remarks>Local and POSIX implementations unlink a terminal symbolic link itself and use normal parent-path resolution. Cancellation requested before the operation prevents admission; successful removal is not relabeled by later cancellation.</remarks>
    ValueTask RemoveFileAsync(string path, CancellationToken cancellationToken = default);
}
