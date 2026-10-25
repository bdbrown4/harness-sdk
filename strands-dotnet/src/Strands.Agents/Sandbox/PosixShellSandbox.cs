using System.Globalization;
using System.Text;
using Strands.Agents.Files;

namespace Strands.Agents.Sandbox;

/// <summary>Implements byte-exact filesystem operations through the selected POSIX command transport.</summary>
/// <remarks>Targets require sh, base64, mkdir, mktemp, cp, mv, rm, and wc. File writes use owned stdin and a sibling temporary file. Terminal symbolic links are rejected for replacement; this is an access policy, not isolation.</remarks>
public abstract class PosixShellSandbox : Sandbox, IStrictFileRemover
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <inheritdoc/>
    public override async ValueTask<byte[]> ReadBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        var quoted = PathArgument(path);
        var result = await ExecuteAsync("p=" + quoted + "; test -e \"$p\" || exit 77; base64 < \"$p\"", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 77) throw new FileNotFoundException("The selected sandbox file does not exist.", path);
        EnsureComplete(result, "read file");
        try { return Convert.FromBase64String(result.Stdout); }
        catch (FormatException error) { throw new InvalidDataException("The selected sandbox returned malformed base64 file bytes.", error); }
    }

    /// <inheritdoc/>
    public override async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => StrictUtf8.GetString(await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc/>
    public override async ValueTask WriteBytesAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        var quoted = PathArgument(path);
        Guard.NotNull(data, nameof(data));
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(data));
        var command = "p=" + quoted + "; d=${p%/*}; mkdir -p \"$d\" || exit; " +
            "test ! -L \"$p\" || { printf '%s' 'Refusing to replace a symbolic link.' >&2; exit 78; }; " +
            "t=$(mktemp \"$d/.strands-XXXXXX\") || exit; trap 'rm -f \"$t\"' 0; trap 'exit 1' 1 2 15; " +
            "if test -e \"$p\"; then cp -p \"$p\" \"$t\" || exit; fi; " +
            "if base64 -d </dev/null >/dev/null 2>&1; then base64 -d >\"$t\"; else base64 -D >\"$t\"; fi && " +
            "test \"$(wc -c <\"$t\")\" -eq " + data.Length.ToString(CultureInfo.InvariantCulture) + " && mv -f \"$t\" \"$p\"";
        var result = await ExecuteAsync(command, new() { StandardInput = encoded }, cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "write file");
    }

    /// <inheritdoc/>
    public override async ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("rm -f " + PathArgument(path), cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "delete file");
    }

    /// <inheritdoc/>
    public override async ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        var command = "p=" + PathArgument(path) + "; test -d \"$p\" || exit 77; " +
            "for f in \"$p\"/.[!.]* \"$p\"/..?* \"$p\"/*; do " +
            "test -e \"$f\" || test -L \"$f\" || continue; n=${f##*/}; d=0; l=0; " +
            "if test -d \"$f\"; then d=1; fi; if test -L \"$f\"; then l=1; fi; printf '%s\\000%s\\000%s\\000' \"$n\" \"$d\" \"$l\"; done";
        var result = await ExecuteAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 77) throw new DirectoryNotFoundException("The selected sandbox directory does not exist: " + path);
        EnsureComplete(result, "list directory");
        var fields = result.Stdout.Split('\0');
        if (fields.Length % 3 != 1 || fields[fields.Length - 1].Length != 0) throw new InvalidDataException("The selected sandbox returned malformed directory entries.");
        var entries = new List<FileEntry>();
        for (var index = 0; index < fields.Length - 1; index += 3)
        {
            if (fields[index + 1] is not ("0" or "1") || fields[index + 2] is not ("0" or "1")) throw new InvalidDataException("The selected sandbox returned malformed directory metadata.");
            entries.Add(new(fields[index], fields[index + 1] == "1", fields[index + 2] == "1"));
        }
        return entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask RemoveFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ExecuteAsync("rm " + PathArgument(path), cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureComplete(result, "remove file");
    }

    /// <summary>Quotes a literal for the target POSIX shell.</summary>
    protected static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string PathArgument(string path)
    {
        SandboxPolicy.Text(path, nameof(path));
        return ShellQuote(path[0] == '/' ? path : "./" + path);
    }

    private static void EnsureComplete(ExecutionResult result, string operation)
    {
        if (result.OutputTruncated) throw new IOException("The selected sandbox capture policy truncated the " + operation + " response.");
        if (result.ExitCode != 0) throw new IOException("Failed to " + operation + " in the selected sandbox (exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + result.Stderr);
    }
}
