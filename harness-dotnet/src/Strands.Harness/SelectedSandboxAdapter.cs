using System.Runtime.CompilerServices;
using Strands.Agents.Files;
using Core = Strands.Agents.Sandbox;

namespace Strands.Harness;

internal sealed class SelectedSandboxAdapter : Core.Sandbox
{
    private readonly ISandbox _selected;
    private readonly IReadOnlyFileSystem _files;

    private SelectedSandboxAdapter(ISandbox selected)
    {
        _selected = selected;
        _files = SandboxFileSystem.Create(selected);
    }

    internal static Core.Sandbox Create(ISandbox selected)
    {
        Guard.NotNull(selected, nameof(selected));
        return selected is SandboxAdapter adapter ? adapter.Sandbox : new SelectedSandboxAdapter(selected);
    }

    public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        var entered = Core.SandboxPolicy.CodeOptions(code, language, options, cancellationToken);
        if (_selected is not ICodeSandbox executor) throw new NotSupportedException("The selected harness sandbox does not advertise source execution.");
        // The code capability owns source input; these bytes are validation's private snapshot, not a separate stdin request.
        return executor.ExecuteCodeStreamingAsync(code, language, entered with { StandardInput = null }, cancellationToken);
    }

    public override IAsyncEnumerable<Core.SandboxStreamEvent> ExecuteStreamingAsync(string command, Core.ExecuteOptions? options = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(command, nameof(command));
        if (command.IndexOf('\0') >= 0) throw new ArgumentException("A command cannot contain NUL.", nameof(command));
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        Core.SandboxPolicy.Duration(options.Timeout, nameof(options.Timeout));
        if (options.MaximumOutputBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        var environment = Core.SandboxPolicy.Environment(options.Environment, posix: true);
        if (options.StandardInput is not null || options.WorkingDirectory is not null || environment.Count != 0)
            throw new NotSupportedException("The selected command capability cannot represent per-call stdin, working directory or environment overrides.");
        if (_selected is not ICommandSandbox executor) throw new NotSupportedException("The selected harness sandbox does not advertise command execution.");
        return CompleteCommandAsync(executor, command, new() { Timeout = options.Timeout ?? TimeSpan.FromSeconds(120), MaxOutputBytes = options.MaximumOutputBytes }, cancellationToken);
    }

    private static async IAsyncEnumerable<Core.SandboxStreamEvent> CompleteCommandAsync(ICommandSandbox executor, string command, ShellExecutionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SandboxExecutionResult result;
        try { result = await executor.ExecuteAsync(command, options, cancellationToken).ConfigureAwait(false); }
        catch (SandboxTimeoutException error)
        {
            var converted = new Core.SandboxTimeoutException(error.Timeout, Convert(error.PartialResult), error);
            foreach (System.Collections.DictionaryEntry entry in error.Data) converted.Data[entry.Key] = entry.Value;
            throw converted;
        }
        if (result.StandardOutput.Length != 0) yield return new Core.StreamChunk(result.StandardOutput, Core.StreamType.Stdout);
        if (result.StandardError.Length != 0) yield return new Core.StreamChunk(result.StandardError, Core.StreamType.Stderr);
        yield return Convert(result);
    }

    public override ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => _files.ReadTextAsync(path, cancellationToken);
    public override ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => _files.ListFilesAsync(path, cancellationToken);
    public override ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => BinaryFiles.ReadBytesAsync(path, cancellationToken);
    public override ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(data, nameof(data));
        cancellationToken.ThrowIfCancellationRequested();
        return BinaryFiles.WriteBytesAsync(path, (byte[])data.Clone(), cancellationToken);
    }
    public override ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => BinaryFiles.DeleteFileAsync(path, cancellationToken);
    private IFileSystem BinaryFiles => _files as IFileSystem ?? throw new NotSupportedException("The selected harness sandbox does not advertise binary filesystem operations.");
    private static Core.ExecutionResult Convert(SandboxExecutionResult result) => new(result.ExitCode, result.StandardOutput, result.StandardError) { OutputTruncated = result.OutputTruncated };
}
