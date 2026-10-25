using System.Text;
using Strands.Agents.Files;

namespace Strands.Harness;

/// <summary>The text filesystem used by harness tools. Implementations determine access policy; this interface does not provide isolation.</summary>
public interface ISandbox
{
    /// <summary>Reads text from an absolute path under the implementation's access policy.</summary>
    ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Creates or replaces the text at an absolute path under the implementation's access policy.</summary>
    ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default);
}

/// <summary>Reads host files and executes bounded host commands with process permissions. This implementation provides no filesystem, network, or process isolation.</summary>
/// <remarks>Modern targets use the platform's process-tree termination API, which cannot guarantee cleanup of inaccessible or already orphaned descendants. The legacy asset supports Windows jobs only and fails before command execution on other platforms.</remarks>
public sealed class LocalFileSandbox : IEnvironmentSandbox, ICommandSandbox, IFileSystem, IBoundedFileReader, ICodeSandbox
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly int _maxReadBytes;
    private readonly LocalShellOptions _shellOptions;
    private readonly Lazy<Strands.Agents.Sandbox.NotASandboxLocalEnvironment> _codeEnvironment;
    internal bool SupportsHostCommands
    {
        get
        {
#if NETSTANDARD2_0
            return Path.DirectorySeparatorChar == '\\';
#else
            return true;
#endif
        }
    }

    /// <summary>Creates a host filesystem adapter with a bounded text read size.</summary>
    /// <param name="maxReadBytes">Maximum bytes read from a text file, defaulting to 5 MiB.</param>
    /// <param name="shellOptions">Host command policy, defaulting to a two-minute deadline and 1 MiB output limit.</param>
    public LocalFileSandbox(int maxReadBytes = 5 * 1024 * 1024, LocalShellOptions? shellOptions = null)
    {
        Guard.Positive(maxReadBytes, nameof(maxReadBytes));
        _maxReadBytes = maxReadBytes;
        _shellOptions = Internal.HostShellRunner.SnapshotOptions(shellOptions ?? new());
        _codeEnvironment = new(() => new Strands.Agents.Sandbox.NotASandboxLocalEnvironment(new()
        {
            MaximumDuration = _shellOptions.MaxDuration,
            MaximumOutputBytes = _shellOptions.MaxOutputBytes,
            Environment = _shellOptions.Environment,
        }, _shellOptions.WorkingDirectory), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Strands.Agents.Sandbox.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Strands.Agents.Sandbox.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => _codeEnvironment.Value.ExecuteCodeStreamingAsync(code, language, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default) => Internal.HostShellRunner.ExecuteAsync(command, options ?? new(), _shellOptions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string>(Directory.GetCurrentDirectory());
    }

    /// <inheritdoc />
    public ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var description = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        // Distribution descriptions such as "Ubuntu 24.04" do not identify the command language.
        // This is the local sandbox's own platform, never a substitute for a custom sandbox's report.
        var family = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "Windows" :
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux) ? "Linux" :
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "macOS" : null;
        return new ValueTask<string?>(family is null ? description : family + " " + description);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidateLocalPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<SandboxEntry>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(new SandboxEntry(Path.GetFileName(entry), Directory.Exists(entry)));
        }
        return new ValueTask<IReadOnlyList<SandboxEntry>>(entries.AsReadOnly());
    }

    ValueTask<IReadOnlyList<FileEntry>> IReadOnlyFileSystem.ListFilesAsync(string path, CancellationToken cancellationToken)
    {
        ValidateLocalPath(path);
        return new LocalFileSystem().ListFilesAsync(path, cancellationToken);
    }

    ValueTask<byte[]> IFileSystem.ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        ValidateLocalPath(path);
        return new LocalFileSystem().ReadBytesAsync(path, cancellationToken);
    }

    ValueTask<byte[]> IBoundedFileReader.ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        ValidateLocalPath(path);
        return new LocalFileSystem().ReadBytesAsync(path, maximumBytes, cancellationToken);
    }

    ValueTask IFileSystem.WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        ValidateLocalPath(path);
        return new LocalFileSystem().WriteBytesAsync(path, data, cancellationToken);
    }

    ValueTask IFileSystem.DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        ValidateLocalPath(path);
        return new LocalFileSystem().DeleteFileAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        ValidateLocalPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
#if NET8_0_OR_GREATER
        await using var disposal = stream.ConfigureAwait(false);
#else
        using var disposal = stream;
#endif
        // The extra byte detects files that grow after the initial length check.
        if (stream.Length > _maxReadBytes) throw new IOException($"Text file exceeds the {_maxReadBytes}-byte read limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, _maxReadBytes)];
        while (true)
        {
            var remaining = (long)_maxReadBytes - buffer.Length + 1;
            var count = await stream.ReadAsync(chunk, 0, (int)Math.Min(chunk.Length, remaining), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            buffer.Write(chunk, 0, count);
            if (buffer.Length > _maxReadBytes) throw new IOException($"Text file exceeds the {_maxReadBytes}-byte read limit.");
        }
        var bytes = buffer.ToArray();
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }

    /// <summary>Stages UTF-8 text in a sibling file and replaces the destination after flushing and checking cancellation.</summary>
    /// <remarks>Terminal symlink/reparse destinations are rejected. Windows replacement merges destination metadata. Modern .NET builds preserve Unix mode bits; the .NET Standard asset uses platform defaults. Ownership and extended Unix ACLs are not preserved.</remarks>
    public async ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ValidateLocalPath(path);
        Guard.NotNull(content, nameof(content));
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = StrictUtf8.GetBytes(content);
        var destination = Path.GetFullPath(path);
        ValidateWriteDestination(destination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".strands-{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteTemporaryAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            var exists = ValidateWriteDestination(destination);
#if NET8_0_OR_GREATER
            if (exists && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
#endif
            cancellationToken.ThrowIfCancellationRequested();
            if (exists) File.Replace(temporary, destination, null, ignoreMetadataErrors: false);
            else File.Move(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task WriteTemporaryAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        await using var streamDisposal = stream.ConfigureAwait(false);
#else
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
#endif
        await stream.WriteAsync(content, 0, content.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static bool ValidateWriteDestination(string path)
    {
        var file = new FileInfo(path);
#if NET8_0_OR_GREATER
        if (file.LinkTarget is not null)
            throw new IOException("Writing to a symbolic link or reparse-point destination is not supported. Select the regular target file explicitly.");
#endif
        var attributes = GetAttributesIfPresent(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Writing to a symbolic link or reparse-point destination is not supported. Select the regular target file explicitly.");
        if (file.Exists && file.IsReadOnly) throw new UnauthorizedAccessException("The destination file is read-only.");
        return file.Exists;
    }

    private static FileAttributes GetAttributesIfPresent(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    private static void ValidateLocalPath(string path)
    {
        FileTools.ValidatePath(path);
        if (!Guard.IsFullyQualifiedLocalPath(path))
            throw new ArgumentException("The path must be absolute on the host operating system.", nameof(path));
    }
}
