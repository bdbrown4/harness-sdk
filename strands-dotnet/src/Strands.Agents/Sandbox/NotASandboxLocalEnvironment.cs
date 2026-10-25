using Strands.Agents.Files;

namespace Strands.Agents.Sandbox;

/// <summary>Runs fresh local processes and accesses host files with the application's permissions.</summary>
/// <remarks>This environment provides no filesystem or process isolation. Source execution invokes the selected interpreter directly with UTF-8 standard input; command execution requires sh.</remarks>
public sealed class NotASandboxLocalEnvironment : Sandbox, IStrictFileRemover
{
    private readonly LocalFileSystem _files = new();
    private readonly SandboxProcessOptions _process;
    private readonly string? _workingDirectory;

    /// <summary>Creates a local environment without starting a process or accessing files.</summary>
    public NotASandboxLocalEnvironment() : this(new(), null) { }

    internal NotASandboxLocalEnvironment(SandboxProcessOptions process, string? workingDirectory)
    {
        _process = SandboxPolicy.Process(process);
        if (workingDirectory is not null) SandboxPolicy.Text(workingDirectory, nameof(workingDirectory));
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc/>
    public override IAsyncEnumerable<SandboxStreamEvent> ExecuteStreamingAsync(string command, ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(command, nameof(command));
        if (command.IndexOf('\0') >= 0) throw new ArgumentException("A command cannot contain NUL.", nameof(command));
        return ExecuteLocal("sh", new[] { "-c", command }, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override IAsyncEnumerable<SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, ExecuteOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteLocal(language, Array.Empty<string>(), SandboxPolicy.CodeOptions(code, language, options, cancellationToken), cancellationToken);

    /// <inheritdoc/>
    public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => _files.ReadBytesAsync(path, cancellationToken);
    /// <inheritdoc/>
    public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => _files.WriteBytesAsync(path, data, cancellationToken);
    /// <inheritdoc/>
    public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => _files.DeleteFileAsync(path, cancellationToken);
    /// <inheritdoc/>
    public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => _files.ReadTextAsync(path, cancellationToken);
    /// <inheritdoc/>
    public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => _files.ListFilesAsync(path, cancellationToken);

    /// <inheritdoc/>
    public ValueTask RemoveFileAsync(string path, CancellationToken cancellationToken = default) => _files.RemoveFileAsync(path, cancellationToken);

    private IAsyncEnumerable<SandboxStreamEvent> ExecuteLocal(string executable, IReadOnlyList<string> arguments, ExecuteOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        var environment = SandboxPolicy.Environment(options.Environment, posix: true);
        var directory = options.WorkingDirectory ?? _workingDirectory;
        if (directory is not null && (directory.Length == 0 || directory.IndexOf('\0') >= 0))
            throw new ArgumentException("WorkingDirectory cannot be empty or contain NUL.", nameof(options));
        var workingDirectory = Path.GetFullPath(directory ?? Directory.GetCurrentDirectory());
        var merged = _process.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var item in environment) merged[item.Key] = item.Value;
        var policy = _process with { Environment = SandboxPolicy.Environment(merged, posix: false) };
        return SandboxProcess.Execute(executable, arguments, options, policy, cancellationToken, workingDirectory);
    }
}
