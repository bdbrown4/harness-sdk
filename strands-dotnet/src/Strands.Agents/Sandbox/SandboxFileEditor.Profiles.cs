using System.Numerics;
using System.Text.Json;

namespace Strands.Agents.Sandbox;

public static partial class SandboxFileEditor
{
    private sealed record TypeScriptArguments(string Command, string Path, string? FileText,
        double[]? Range, string? OldText, string? NewText, double? InsertLine);

    private static TypeScriptArguments CaptureTypeScript(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Editor input must be an object.", nameof(input));
        var command = TypeScriptText(input, "command") ?? throw new ArgumentException("command must be a string.", nameof(input));
        var path = TypeScriptText(input, "path") ?? throw new ArgumentException("path must be a string.", nameof(input));
        var fileText = TypeScriptText(input, "file_text");
        var oldText = TypeScriptText(input, "old_str");
        var newText = TypeScriptText(input, "new_str");
        double[]? range = null;
        if (input.TryGetProperty("view_range", out var suppliedRange))
        {
            if (suppliedRange.ValueKind != JsonValueKind.Array || suppliedRange.GetArrayLength() != 2)
                throw new ArgumentException("view_range must contain exactly two finite line numbers.", nameof(input));
            range = [TypeScriptPosition(suppliedRange[0], "view_range"), TypeScriptPosition(suppliedRange[1], "view_range")];
        }
        double? line = input.TryGetProperty("insert_line", out var suppliedLine) ? TypeScriptPosition(suppliedLine, "insert_line") : null;
        ValidateProfileCommand(command, fileText, oldText, newText, line is not null);
        path = path.TrimEnd('/', '\\');
        ValidatePath(path);
        return new(command, path, fileText, range, oldText, newText, line);
    }

    private static string? TypeScriptText(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " must be a string when supplied.", nameof(input));
        return value.GetString();
    }

    private static double TypeScriptPosition(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
            throw new ArgumentException(name + " must be a finite number.", "input");
        return number;
    }

    private static void ValidateProfileCommand(string command, string? fileText, string? oldText, string? newText, bool hasInsertLine)
    {
        switch (command)
        {
            case "view": break;
            case "create" when fileText is null: throw new ArgumentException("Parameter `file_text` is required for command: create", "input");
            case "create": break;
            case "str_replace" when oldText is null: throw new ArgumentException("Parameter `old_str` is required for command: str_replace", "input");
            case "str_replace": break;
            case "insert" when !hasInsertLine || newText is null: throw new ArgumentException("Parameters `insert_line` and `new_str` are required for command: insert", "input");
            case "insert": break;
            default: throw new ArgumentException("Unknown command: " + command, "input");
        }
    }

    private static Sandbox ProfileSandbox(Sandbox? boundSandbox, ToolContext context)
    {
        if (boundSandbox is not null) return boundSandbox;
        Guard.NotNull(context.Agent, nameof(context.Agent));
        return context.Agent.Sandbox;
    }

    private static async ValueTask<string> InvokeTypeScriptAsync(Sandbox? boundSandbox, ToolContext context, JsonElement input,
        SandboxDirectoryErrorPolicy policy, CancellationToken cancellationToken)
    {
        var arguments = CaptureTypeScript(input);
        var sandbox = ProfileSandbox(boundSandbox, context);
        return arguments.Command switch
        {
            "view" => await ViewTypeScriptAsync(sandbox, arguments.Path, arguments.Range, policy, cancellationToken).ConfigureAwait(false),
            "create" => await CreateAsync(sandbox, arguments.Path, arguments.FileText!, cancellationToken).ConfigureAwait(false),
            "str_replace" => await ReplaceAsync(sandbox, arguments.Path, arguments.OldText!, arguments.NewText ?? string.Empty, cancellationToken).ConfigureAwait(false),
            "insert" => await InsertTypeScriptAsync(sandbox, arguments.Path, arguments.InsertLine!.Value, arguments.NewText!, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The prepared editor command is invalid."),
        };
    }

    private static async ValueTask<string> ViewTypeScriptAsync(Sandbox sandbox, string path, double[]? range,
        SandboxDirectoryErrorPolicy policy, CancellationToken cancellationToken)
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
        return NumberOutput(NumberSlice(lines, start - 1, end == -1 ? lines.Length : end), path, start);
    }

    private static async ValueTask<string> InsertTypeScriptAsync(Sandbox sandbox, string path, double line, string inserted, CancellationToken cancellationToken)
    {
        var text = await ReadFileAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        var lines = Lines(text);
        if (line < 0 || line > lines.Length) throw new ArgumentOutOfRangeException(nameof(line), "insert_line must be within the file's lines.");
        var insertedLines = Lines(inserted);
        var point = NumberSliceIndex(line, lines.Length);
        var updated = text.Length == 0 ? insertedLines : lines.Take(point).Concat(insertedLines).Concat(lines.Skip(point)).ToArray();
        var start = Math.Max(0, line - SnippetLines);
        var end = Math.Min(updated.Length, line + insertedLines.Length + SnippetLines);
        var snippet = NumberSlice(updated, start, end);
        await WriteAsync(sandbox, path, string.Join("\n", updated), cancellationToken).ConfigureAwait(false);
        return "The file " + path + " has been edited. " + NumberOutput(snippet, "a snippet of the edited file", start + 1) + "Review the changes and make sure they are as expected (correct indentation, no duplicate lines, etc). Edit the file again if necessary.";
    }

    private static int NumberSliceIndex(double value, int length)
    {
        if (value >= length) return length;
        if (value <= -length) return 0;
        var whole = Math.Truncate(value);
        if (whole < 0) whole += length;
        return whole <= 0 ? 0 : (int)whole;
    }

    private static string NumberSlice(string[] lines, double start, double end)
    {
        var first = NumberSliceIndex(start, lines.Length);
        var last = NumberSliceIndex(end, lines.Length);
        return string.Join("\n", lines.Skip(first).Take(Math.Max(0, last - first)));
    }

    private static string NumberOutput(string content, string descriptor, double firstLine) => "Here's the result of running `cat -n` on " + descriptor + ":\n" +
        string.Join("\n", Lines(content.Replace("\t", "        ")).Select((line, index) => EcmaScriptNumber.Format(index + firstLine).PadLeft(6) + "  " + line)) + "\n";

    private static async ValueTask<string> InvokePythonAsync(Sandbox? boundSandbox, ToolContext context, JsonElement input,
        SandboxDirectoryErrorPolicy policy, CancellationToken cancellationToken)
    {
        var arguments = SandboxFileEditorPythonInput.Capture(input, cancellationToken);
        ValidateProfileCommand(arguments.Command, arguments.FileText, arguments.OldText, arguments.NewText, arguments.InsertLine is not null);
        var path = arguments.Path.TrimEnd('/', '\\');
        ValidatePath(path);
        var sandbox = ProfileSandbox(boundSandbox, context);
        return arguments.Command switch
        {
            "view" => await ViewPythonAsync(sandbox, path, arguments.ViewRange, policy, cancellationToken).ConfigureAwait(false),
            "create" => await CreateAsync(sandbox, path, arguments.FileText!, cancellationToken).ConfigureAwait(false),
            "str_replace" => await ReplaceAsync(sandbox, path, arguments.OldText!, arguments.NewText ?? string.Empty, cancellationToken).ConfigureAwait(false),
            "insert" => await InsertPythonAsync(sandbox, path, arguments.InsertLine!.Value, arguments.NewText!, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The prepared editor command is invalid."),
        };
    }

    private static async ValueTask<string> ViewPythonAsync(Sandbox sandbox, string path, IReadOnlyList<BigInteger>? range,
        SandboxDirectoryErrorPolicy policy, CancellationToken cancellationToken)
    {
        var found = await ProbeAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        RequireExists(found.Exists, path);
        if (found.Directory)
        {
            if (range is { Count: > 0 }) throw new ArgumentException("The `view_range` parameter is not allowed when `path` points to a directory.", nameof(range));
            return await ListDirectoryAsync(sandbox, path, policy, cancellationToken).ConfigureAwait(false);
        }
        var text = await ReadAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        if (range is null || range.Count == 0) return Output(text, path, 1);
        if (range.Count < 2) throw new ArgumentException("view_range must have a start and end after input binding.", nameof(range));
        var lines = Lines(text);
        var start = range[0];
        var end = range[1];
        if (start < 1 || start > lines.Length || (end != -1 && (end > lines.Length || end < start)))
            throw new ArgumentOutOfRangeException(nameof(range), "view_range must be within the file's lines, with end -1 or at least start.");
        var first = (int)start;
        var last = end == -1 ? lines.Length : (int)end;
        return Output(string.Join("\n", lines.Skip(first - 1).Take(last - first + 1)), path, first);
    }

    private static async ValueTask<string> InsertPythonAsync(Sandbox sandbox, string path, BigInteger line, string inserted, CancellationToken cancellationToken)
    {
        // Every physical string-array index fits Int32; huge bound integers still reach the selected read before rejection.
        if (line >= int.MinValue && line <= int.MaxValue)
            return await InsertAsync(sandbox, path, (int)line, inserted, cancellationToken).ConfigureAwait(false);
        await ReadFileAsync(sandbox, path, cancellationToken).ConfigureAwait(false);
        throw new ArgumentOutOfRangeException(nameof(line), "insert_line must be within the file's lines.");
    }
}
