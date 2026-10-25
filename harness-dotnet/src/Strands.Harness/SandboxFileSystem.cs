using Strands.Agents.Files;

namespace Strands.Harness;

internal class SandboxFileSystem(ISandbox sandbox) : IReadOnlyFileSystem
{
    internal static IReadOnlyFileSystem Create(ISandbox sandbox) => sandbox is IFileSystem files ? new BinarySandboxFileSystem(sandbox, files) : new SandboxFileSystem(sandbox);

    public async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        if (sandbox is IReadOnlyFileSystem files) return await files.ReadTextAsync(await ResolveAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return await sandbox.ReadTextAsync(await ResolveAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sandbox is IReadOnlyFileSystem files) return await files.ListFilesAsync(await ResolveAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (sandbox is not IEnvironmentSandbox environment)
            throw new NotSupportedException("The selected sandbox does not support directory listing. Implement IReadOnlyFileSystem or IEnvironmentSandbox to discover skill directories.");
        var entries = await environment.ListFilesAsync(await ResolveAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(entries.Select(entry => new FileEntry(entry.Name, entry.IsDirectory)).ToArray());
    }

    protected async ValueTask<string> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        Guard.NotBlank(path, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        if (sandbox is IReadOnlyFileSystem && sandbox is not LocalFileSandbox) return path;
        if (path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            (path.Length >= 3 && Guard.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\')) return path;
        if (sandbox is not IEnvironmentSandbox environment)
            throw new NotSupportedException("Relative filesystem paths require the selected sandbox's working directory capability.");
        var workingDirectory = await environment.GetWorkingDirectoryAsync(cancellationToken).ConfigureAwait(false);
        FileTools.ValidatePath(workingDirectory);
        if (sandbox is LocalFileSandbox) return Path.GetFullPath(Path.Combine(workingDirectory, path));
        var combined = workingDirectory.Replace('\\', '/').TrimEnd('/') + "/" + path.Replace('\\', '/');
        var prefix = combined.StartsWith("//", StringComparison.Ordinal) ? "//" : combined.StartsWith("/", StringComparison.Ordinal) ? "/" : combined.Substring(0, 3);
        var minimum = prefix == "//" ? 2 : 0;
        var segments = new List<string>();
        foreach (var segment in combined.Substring(prefix.Length).Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (segments.Count > minimum) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        return prefix + string.Join("/", segments);
    }

    private sealed class BinarySandboxFileSystem(ISandbox sandbox, IFileSystem files) : SandboxFileSystem(sandbox), IFileSystem
    {
        public async ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
            => await files.ReadBytesAsync(await ResolveAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        public async ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
            => await files.WriteBytesAsync(await ResolveAsync(path, cancellationToken).ConfigureAwait(false), data, cancellationToken).ConfigureAwait(false);
        public async ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
            => await files.DeleteFileAsync(await ResolveAsync(path, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }
}
