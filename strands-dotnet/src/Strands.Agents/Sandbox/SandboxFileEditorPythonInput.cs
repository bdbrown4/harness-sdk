using System.Numerics;
using System.Text.Json;

namespace Strands.Agents.Sandbox;

/// <summary>A detached snapshot of one registered Python-profile editor input.</summary>
internal sealed record SandboxFileEditorPythonInput(string Command, string Path, string? FileText,
    IReadOnlyList<BigInteger>? ViewRange, string? OldText, string? NewText, BigInteger? InsertLine)
{
    internal static SandboxFileEditorPythonInput Capture(JsonElement input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Editor input must be an object.", nameof(input));
        var command = RequiredString(input, "command");
        if (command is not ("view" or "create" or "str_replace" or "insert")) throw new ArgumentException("Unknown command: " + command, nameof(input));
        var path = RequiredString(input, "path");
        var fileText = OptionalString(input, "file_text");
        IReadOnlyList<BigInteger>? range = null;
        if (input.TryGetProperty("view_range", out var configuredRange) && configuredRange.ValueKind != JsonValueKind.Null)
        {
            if (configuredRange.ValueKind != JsonValueKind.Array) throw new ArgumentException("view_range must be an array or null.", nameof(input));
            var values = new BigInteger[configuredRange.GetArrayLength()];
            for (var index = 0; index < values.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                values[index] = Pydantic25Integer.Parse(configuredRange[index], "view_range");
            }
            range = Array.AsReadOnly(values);
        }
        var oldText = OptionalString(input, "old_str");
        var newText = OptionalString(input, "new_str");
        BigInteger? line = null;
        if (input.TryGetProperty("insert_line", out var configuredLine) && configuredLine.ValueKind != JsonValueKind.Null)
            line = Pydantic25Integer.Parse(configuredLine, "insert_line");
        return new(command, path, fileText, range, oldText, newText, line);
    }

    private static string RequiredString(JsonElement input, string name)
        => OptionalString(input, name) ?? throw new ArgumentException(name + " must be a string.", nameof(input));

    private static string? OptionalString(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " must be a string or null.", nameof(input));
        return value.GetString();
    }
}
