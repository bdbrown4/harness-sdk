using System.Globalization;
using System.Text;
using System.Text.Json;
using Strands.Agents.Files;

namespace Strands.Agents.Sandbox;

/// <summary>Creates the combined view, create, replacement and insertion tool for one selected sandbox.</summary>
public static partial class SandboxFileEditor
{
    private const int MaximumFileBytes = 1048576;
    private const int SnippetLines = 4;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    /// <summary>Describes the combined editor protocol.</summary>
    public const string DefaultDescription = "Filesystem editor tool for viewing, creating, and editing files. Supports view (with line ranges), create, str_replace, and insert operations. Files must use absolute paths.";
    /// <summary>Creates a tool resolving the invocation Agent's sandbox.</summary>
    public static ITool Create() => CreateForAgent();
    /// <summary>Creates a configured tool resolving the invocation Agent's sandbox.</summary>
    public static ITool CreateForAgent(SandboxFileEditorOptions? options = null) => CreateTool(null, options);
    /// <summary>Creates a tool borrowing the selected sandbox.</summary>
    public static ITool Create(Sandbox sandbox, SandboxFileEditorOptions? options = null)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        return CreateTool(sandbox, options);
    }

    private static ITool CreateTool(Sandbox? boundSandbox, SandboxFileEditorOptions? options)
    {
        options ??= new();
        var profile = options.InputProfile;
        if (profile is not (SandboxFileEditorInputProfile.Native or SandboxFileEditorInputProfile.TypeScript or SandboxFileEditorInputProfile.Python))
            throw new ArgumentOutOfRangeException(nameof(options), "The editor input profile is invalid.");
        var typeScript = profile == SandboxFileEditorInputProfile.TypeScript;
        var python = profile == SandboxFileEditorInputProfile.Python;
        var policy = options.DirectoryErrorPolicy;
        if (policy is not (SandboxDirectoryErrorPolicy.SkipIoFailures or SandboxDirectoryErrorPolicy.SkipNonCancellationFailures))
            throw new ArgumentOutOfRangeException(nameof(options), "The directory error policy is invalid.");
        var spec = new ToolSpec(options.Name, options.Description ?? DefaultDescription, JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", @enum = new[] { "view", "create", "str_replace", "insert" } },
                path = new { type = "string" },
                file_text = new { type = python ? (object)new[] { "string", "null" } : "string" },
                view_range = python
                    ? (object)new { anyOf = new object[] { new { type = "null" }, new { type = "array", items = new { type = new[] { "number", "string", "boolean" } } } } }
                    : typeScript
                    ? (object)new { type = "array", items = new { type = "number" }, minItems = 2, maxItems = 2 }
                    : new
                    {
                        anyOf = new object[]
                {
                    new { type = "null" },
                    new { type = "array", maxItems = 0 },
                    new { type = "array", items = new { type = "integer" }, minItems = 2, maxItems = 2 },
                }
                    },
                old_str = new { type = python ? (object)new[] { "string", "null" } : "string" },
                new_str = new { type = typeScript ? (object)"string" : new[] { "string", "null" } },
                insert_line = new { type = python ? (object)new[] { "number", "string", "boolean", "null" } : typeScript ? "number" : "integer" },
            },
            required = new[] { "command", "path" },
        }));
        return new FunctionTool(spec, async (input, context, cancellationToken) =>
        {
            Guard.NotNull(context, nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            if (profile == SandboxFileEditorInputProfile.TypeScript)
                return await InvokeTypeScriptAsync(boundSandbox, context, input, policy, cancellationToken).ConfigureAwait(false);
            if (profile == SandboxFileEditorInputProfile.Python)
                return await InvokePythonAsync(boundSandbox, context, input, policy, cancellationToken).ConfigureAwait(false);
            var arguments = Capture(input);
            var sandbox = boundSandbox;
            if (sandbox is null)
            {
                Guard.NotNull(context.Agent, nameof(context.Agent));
                sandbox = context.Agent.Sandbox;
            }
            return arguments.Command switch
            {
                "view" => await ViewAsync(sandbox, arguments.Path, arguments.Range, policy, cancellationToken).ConfigureAwait(false),
                "create" => await CreateAsync(sandbox, arguments.Path, arguments.FileText!, cancellationToken).ConfigureAwait(false),
                "str_replace" => await ReplaceAsync(sandbox, arguments.Path, arguments.OldText!, arguments.NewText ?? string.Empty, cancellationToken).ConfigureAwait(false),
                "insert" => await InsertAsync(sandbox, arguments.Path, arguments.InsertLine!.Value, arguments.NewText!, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The prepared editor command is invalid."),
            };
        });
    }

    private sealed record Arguments(string Command, string Path, string? FileText, int[]? Range, string? OldText, string? NewText, int? InsertLine);

    private static Arguments Capture(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Editor input must be an object.", nameof(input));
        var command = Text(input, "command") ?? throw new ArgumentException("command must be a string.", nameof(input));
        var path = Text(input, "path") ?? throw new ArgumentException("path must be a string.", nameof(input));
        var fileText = Text(input, "file_text");
        var oldText = Text(input, "old_str");
        var newText = Text(input, "new_str");
        int[]? range = null;
        if (input.TryGetProperty("view_range", out var configuredRange) && configuredRange.ValueKind != JsonValueKind.Null
            && !(configuredRange.ValueKind == JsonValueKind.Array && configuredRange.GetArrayLength() == 0))
        {
            if (configuredRange.ValueKind != JsonValueKind.Array || configuredRange.GetArrayLength() != 2 || configuredRange[0].ValueKind != JsonValueKind.Number || configuredRange[1].ValueKind != JsonValueKind.Number || !TryLineNumber(configuredRange[0], out var start) || !TryLineNumber(configuredRange[1], out var end))
                throw new ArgumentException("view_range must contain exactly two integer line numbers.", nameof(input));
            range = [start, end];
        }
        int? insertLine = null;
        if (input.TryGetProperty("insert_line", out var configuredLine) && configuredLine.ValueKind != JsonValueKind.Null)
        {
            if (configuredLine.ValueKind != JsonValueKind.Number || !TryLineNumber(configuredLine, out var line)) throw new ArgumentException("insert_line must be an integer.", nameof(input));
            insertLine = line;
        }
        switch (command)
        {
            case "view": break;
            case "create" when fileText is null: throw new ArgumentException("Parameter `file_text` is required for command: create", nameof(input));
            case "create": break;
            case "str_replace" when oldText is null: throw new ArgumentException("Parameter `old_str` is required for command: str_replace", nameof(input));
            case "str_replace": break;
            case "insert" when insertLine is null || newText is null: throw new ArgumentException("Parameters `insert_line` and `new_str` are required for command: insert", nameof(input));
            case "insert": break;
            default: throw new ArgumentException("Unknown command: " + command, nameof(input));
        }
        path = path.TrimEnd('/', '\\');
        ValidatePath(path);
        return new(command, path, fileText, range, oldText, newText, insertLine);
    }

    private static bool TryLineNumber(JsonElement value, out int line)
    {
        line = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (value.TryGetInt32(out line)) return true;
        var raw = value.GetRawText().AsSpan();
        var negative = raw[0] == '-';
        var start = negative ? 1 : 0;
        var exponentAt = raw.IndexOfAny('e', 'E');
        var end = exponentAt < 0 ? raw.Length : exponentAt;
        var digits = 0;
        var leadingZeros = 0;
        var trailingZeros = 0;
        var fractionalDigits = 0;
        var fraction = false;
        var nonzero = false;
        for (var index = start; index < end; index++)
        {
            var digit = raw[index];
            if (digit == '.') { fraction = true; continue; }
            digits++;
            if (fraction) fractionalDigits++;
            if (digit == '0')
            {
                trailingZeros++;
                if (!nonzero) leadingZeros++;
            }
            else { nonzero = true; trailingZeros = 0; }
        }
        if (!nonzero) return true;
        long exponent = 0;
        if (exponentAt >= 0)
        {
            var index = exponentAt + 1;
            var exponentNegative = raw[index] == '-';
            if (raw[index] is '-' or '+') index++;
            // Larger exponents cannot change integrality or the ten-digit range decision.
            var saturation = (long)raw.Length + 11;
            for (; index < raw.Length; index++)
                exponent = Math.Min(saturation, exponent * 10 + raw[index] - '0');
            if (exponentNegative) exponent = -exponent;
        }
        var scale = exponent - fractionalDigits + trailingZeros;
        var significantDigits = digits - leadingZeros - trailingZeros;
        if (scale < 0 || significantDigits + scale > 10) return false;
        long magnitude = 0;
        var skip = leadingZeros;
        var remaining = significantDigits;
        for (var index = start; index < end && remaining > 0; index++)
        {
            if (raw[index] == '.') continue;
            if (skip > 0) { skip--; continue; }
            magnitude = magnitude * 10 + raw[index] - '0';
            remaining--;
        }
        // The range decision bounds accumulation to ten digits and at most nine zeros.
        for (long count = 0; count < scale; count++) magnitude *= 10;
        if (magnitude > (negative ? 2147483648L : int.MaxValue)) return false;
        line = (int)(negative ? -magnitude : magnitude);
        return true;
    }

    private static string? Text(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " must be a string.", nameof(input));
        return value.GetString();
    }

    private static void ValidatePath(string path)
    {
        if (path.IndexOf('\0') >= 0) throw new ArgumentException("A file path cannot contain NUL.", nameof(path));
        var pieces = path.Split(new[] { '/', '\\' });
        if (pieces.Any(value => value == "..")) throw new ArgumentException("Invalid path: path traversal is not allowed", nameof(path));
        var posix = path.StartsWith("/", StringComparison.Ordinal);
        var drive = path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':' && (path[2] == '/' || path[2] == '\\');
        var unc = path.StartsWith("\\\\", StringComparison.Ordinal) && pieces.Length >= 4 && pieces[2].Length != 0 && pieces[3].Length != 0;
        if (!posix && !drive && !unc) throw new ArgumentException("The path " + path + " is not an absolute path. Use a POSIX, fully qualified drive, or UNC path.", nameof(path));
    }

    private static async ValueTask<(bool Exists, bool Directory)> ProbeAsync(Sandbox sandbox, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = path.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        var parent = index == 0 ? "/" : index == 2 && normalized[1] == ':' ? normalized.Substring(0, 3) : normalized.Substring(0, index);
        var name = normalized.Substring(index + 1);
        IReadOnlyList<FileEntry> entries;
        try { entries = await sandbox.ListFilesAsync(parent, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException error) when (!cancellationToken.IsCancellationRequested && !HasCleanup(error)) { return (false, false); }
        catch (DirectoryNotFoundException error) when (!cancellationToken.IsCancellationRequested && !HasCleanup(error)) { return (false, false); }
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = entries.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Any(value => value is null)) throw new InvalidDataException("The selected sandbox returned a null directory entry.");
        var entry = snapshot.FirstOrDefault(value => value.Name == name);
        return entry is null ? (false, false) : (true, entry.IsDirectory);
    }

    private static async ValueTask<string> ReadAsync(Sandbox sandbox, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = await sandbox.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var size = Utf8.GetByteCount(text);
        if (size > MaximumFileBytes) throw new InvalidOperationException("File size (" + size.ToString(CultureInfo.InvariantCulture) + " bytes) exceeds maximum allowed size (1048576 bytes)");
        return text;
    }

    private static async ValueTask WriteAsync(Sandbox sandbox, string path, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = Utf8.GetBytes(text);
        cancellationToken.ThrowIfCancellationRequested();
        await sandbox.WriteBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
        // An accepted successful write is not relabeled as uncommitted by a later cancellation.
    }

    private static async ValueTask<string> ViewAsync(Sandbox sandbox, string path, int[]? range, SandboxDirectoryErrorPolicy policy, CancellationToken cancellationToken)
    {
        var found = await ProbeAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        RequireExists(found.Exists, path);
        if (found.Directory)
        {
            if (range is not null) throw new ArgumentException("The `view_range` parameter is not allowed when `path` points to a directory.", nameof(range));
            return await ListDirectoryAsync(sandbox, path, policy, cancellationToken).ConfigureAwait(false);
        }
        var text = await ReadAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        if (range is null) return Output(text, path, 1);
        var lines = Lines(text);
        var start = range[0];
        var end = range[1];
        if (start < 1 || start > lines.Length || (end != -1 && (end > lines.Length || end < start)))
            throw new ArgumentOutOfRangeException(nameof(range), "view_range must be within the file's lines, with end -1 or at least start.");
        return Output(string.Join("\n", lines.Skip(start - 1).Take((end == -1 ? lines.Length : end) - start + 1)), path, start);
    }

    private static async ValueTask<string> CreateAsync(Sandbox sandbox, string path, string text, CancellationToken cancellationToken)
    {
        if ((await ProbeAsync(sandbox, path, cancellationToken).ConfigureAwait(false)).Exists)
            throw new InvalidOperationException("File already exists at: " + path + ". Cannot overwrite files using command `create`.");
        await WriteAsync(sandbox, path, text, cancellationToken).ConfigureAwait(false);
        return "File created successfully at: " + path;
    }

    private static async ValueTask<string> ReplaceAsync(Sandbox sandbox, string path, string oldText, string replacement, CancellationToken cancellationToken)
    {
        var text = await ReadFileAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        if (oldText.Length == 0) throw new ArgumentException("No replacement was performed, old_str must not be empty.", nameof(oldText));
        var occurrences = new List<int>();
        var offset = 0;
        while ((offset = text.IndexOf(oldText, offset, StringComparison.Ordinal)) >= 0)
        {
            occurrences.Add(offset);
            offset += oldText.Length;
        }
        if (occurrences.Count == 0) throw new InvalidOperationException("No replacement was performed, old_str `" + oldText + "` did not appear verbatim in " + path + ".");
        if (occurrences.Count > 1)
        {
            var positions = occurrences.Select(index => text.Substring(0, index).Count(character => character == '\n') + 1).ToArray();
            throw new InvalidOperationException("No replacement was performed. Multiple occurrences of old_str `" + oldText + "` in lines " + JsonSerializer.Serialize(positions) + ". Please ensure it is unique");
        }
        var match = occurrences[0];
        var updated = text.Substring(0, match) + replacement + text.Substring(match + oldText.Length);
        var replacementLine = text.Substring(0, match).Count(character => character == '\n');
        var lineDifference = Lines(replacement).Length - Lines(oldText).Length;
        var lines = Lines(updated);
        var start = Math.Max(0, replacementLine - SnippetLines);
        var end = Math.Min(lines.Length, replacementLine + SnippetLines + lineDifference + 1);
        var snippet = Slice(lines, start, end);
        await WriteAsync(sandbox, path, updated, cancellationToken).ConfigureAwait(false);
        return "The file " + path + " has been edited. " + Output(snippet, "a snippet of " + path, start + 1) + "Review the changes and make sure they are as expected. Edit the file again if necessary.";
    }

    private static async ValueTask<string> InsertAsync(Sandbox sandbox, string path, int line, string inserted, CancellationToken cancellationToken)
    {
        var text = await ReadFileAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        var lines = Lines(text);
        if (line < 0 || line > lines.Length) throw new ArgumentOutOfRangeException(nameof(line), "insert_line must be within [0, " + lines.Length.ToString(CultureInfo.InvariantCulture) + "].");
        var insertedLines = Lines(inserted);
        var updated = text.Length == 0 ? insertedLines : lines.Take(line).Concat(insertedLines).Concat(lines.Skip(line)).ToArray();
        var start = Math.Max(0, line - SnippetLines);
        var end = Math.Min(updated.Length, line + insertedLines.Length + SnippetLines);
        var snippet = Slice(updated, start, end);
        await WriteAsync(sandbox, path, string.Join("\n", updated), cancellationToken).ConfigureAwait(false);
        return "The file " + path + " has been edited. " + Output(snippet, "a snippet of the edited file", start + 1) + "Review the changes and make sure they are as expected (correct indentation, no duplicate lines, etc). Edit the file again if necessary.";
    }

    private static async ValueTask<string> ReadFileAsync(Sandbox sandbox, string path, CancellationToken cancellationToken)
    {
        var found = await ProbeAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        RequireExists(found.Exists, path);
        if (found.Directory) throw new InvalidOperationException("The path " + path + " is a directory and only the `view` command can be used on directories");
        return await ReadAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string> ListDirectoryAsync(Sandbox sandbox, string path, SandboxDirectoryErrorPolicy policy, CancellationToken cancellationToken)
    {
        var items = new List<string>();
        await WalkAsync(path, string.Empty, 0).ConfigureAwait(false);
        items.Sort(StringComparer.Ordinal);
        return "Here's the files and directories up to 2 levels deep in " + path + ", excluding hidden items:\n" + string.Join("\n", items) + "\n";

        async ValueTask WalkAsync(string current, string prefix, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FileEntry> supplied;
            try
            {
                supplied = await sandbox.ListFilesAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not OperationCanceledException && !HasCleanup(error) && (policy == SandboxDirectoryErrorPolicy.SkipNonCancellationFailures || error is IOException or UnauthorizedAccessException))
            {
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var entries = supplied.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Any(entry => entry is null)) throw new InvalidDataException("The selected sandbox returned a null directory entry.");
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                items.Add(relative);
                if (entry.IsDirectory && depth < 2) await WalkAsync(current + "/" + entry.Name, relative, depth + 1).ConfigureAwait(false);
            }
        }
    }

    private static bool HasCleanup(Exception error) => error.Data.Contains("Strands.CleanupErrors") || error.Data.Contains("Strands.CleanupTask") || error.Data.Contains("Strands.IteratorCleanupErrors");
    private static void RequireExists(bool exists, string path)
    {
        if (!exists) throw new InvalidOperationException("The path " + path + " does not exist. Please provide a valid path.");
    }
    private static string[] Lines(string text) => text.Split(new[] { '\n' });
    private static string Slice(string[] lines, int start, int end)
    {
        // Both pinned sources use slice semantics, including a negative snippet end after a large deletion.
        end = end < 0 ? Math.Max(0, lines.Length + end) : Math.Min(lines.Length, end);
        return string.Join("\n", lines.Skip(start).Take(Math.Max(0, end - start)));
    }
    private static string Output(string content, string descriptor, int firstLine) => "Here's the result of running `cat -n` on " + descriptor + ":\n" +
        string.Join("\n", Lines(content.Replace("\t", "        ")).Select((line, index) => (firstLine + index).ToString(CultureInfo.InvariantCulture).PadLeft(6) + "  " + line)) + "\n";
}
