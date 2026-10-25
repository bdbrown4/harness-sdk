using System.Text;
using Strands.Agents.Files;
using CoreSandbox = Strands.Agents.Sandbox.Sandbox;

namespace Strands.Harness;

/// <summary>Exposes a selected core sandbox to harness tools without borrowing host filesystem or command authority.</summary>
public sealed class SandboxAdapter : ICommandSandbox, IEnvironmentSandbox, IFileSystem, ICodeSandbox
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    /// <summary>Creates an adapter around a caller-owned sandbox.</summary>
    public SandboxAdapter(CoreSandbox sandbox) { Guard.NotNull(sandbox, nameof(sandbox)); Sandbox = sandbox; }
    /// <summary>The selected caller-owned core environment.</summary>
    public CoreSandbox Sandbox { get; }
    /// <inheritdoc/>
    public IAsyncEnumerable<Strands.Agents.Sandbox.SandboxStreamEvent> ExecuteCodeStreamingAsync(string code, string language, Strands.Agents.Sandbox.ExecuteOptions? options = null, CancellationToken cancellationToken = default) => Sandbox.ExecuteCodeStreamingAsync(code, language, options, cancellationToken);
    /// <inheritdoc/>
    public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => Sandbox.ReadTextAsync(path, cancellationToken);
    /// <inheritdoc/>
    public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(content, nameof(content));
        return Sandbox.WriteBytesAsync(path, StrictUtf8.GetBytes(content), cancellationToken);
    }
    /// <inheritdoc/>
    public ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default) => Sandbox.ReadBytesAsync(path, cancellationToken);
    /// <inheritdoc/>
    public ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default) => Sandbox.WriteBytesAsync(path, data, cancellationToken);
    /// <inheritdoc/>
    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default) => Sandbox.DeleteFileAsync(path, cancellationToken);
    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => (await Sandbox.ListFilesAsync(path, cancellationToken).ConfigureAwait(false)).Select(entry => new SandboxEntry(entry.Name, entry.IsDirectory)).ToArray();
    ValueTask<IReadOnlyList<FileEntry>> IReadOnlyFileSystem.ListFilesAsync(string path, CancellationToken cancellationToken) => Sandbox.ListFilesAsync(path, cancellationToken);
    /// <inheritdoc/>
    public async ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default) => await DiscoverAsync("pwd -P", cancellationToken).ConfigureAwait(false);
    /// <inheritdoc/>
    public async ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default) => await DiscoverAsync("uname -s", cancellationToken).ConfigureAwait(false);
    /// <inheritdoc/>
    public async ValueTask<SandboxExecutionResult> ExecuteAsync(string command, ShellExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        try
        {
            return Convert(await Sandbox.ExecuteAsync(command, new() { Timeout = options.Timeout, MaximumOutputBytes = options.MaxOutputBytes }, cancellationToken).ConfigureAwait(false));
        }
        catch (Strands.Agents.Sandbox.SandboxTimeoutException error)
        {
            var converted = new SandboxTimeoutException(error.Timeout, Convert(error.PartialResult), error);
            foreach (System.Collections.DictionaryEntry entry in error.Data) converted.Data[entry.Key] = entry.Value;
            throw converted;
        }
    }

    private async ValueTask<string> DiscoverAsync(string command, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.OutputTruncated) throw new IOException("The selected sandbox could not report its environment: " + result.StandardError);
        var value = result.StandardOutput.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0) throw new InvalidDataException("The selected sandbox returned an invalid environment value.");
        return value;
    }
    private static SandboxExecutionResult Convert(Strands.Agents.Sandbox.ExecutionResult result) => new(result.Stdout, result.Stderr, result.ExitCode, result.OutputTruncated);
}
