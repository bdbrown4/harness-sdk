using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness;

/// <summary>File tools routed through a caller-selected filesystem.</summary>
public static class FileTools
{
    /// <summary>Creates the read tool with numbered text paging and typed media through the supplied filesystem.</summary>
    public static ITool CreateRead(ISandbox sandbox) => CreateRead(sandbox, new());

    /// <summary>Creates a paged text and typed media reader. The selected sandbox must provide binary capabilities for media paths.</summary>
    public static ITool CreateRead(ISandbox sandbox, ReadConfig options) => CreateRead(sandbox, options, null, null, sandbox as Strands.Agents.Files.IReadOnlyFileSystem, false);

    internal static ITool CreateRead(ISandbox sandbox, ReadConfig options, IModel? model, string? identifier, Strands.Agents.Files.IReadOnlyFileSystem? files, bool rebindOnDelegation = true)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        Guard.NotNull(options, nameof(options));
        options.Validate();
        var profile = options.PagingProfile;
        var text = new FunctionTool(new("read", "Read a UTF-8 text file using numbered lines.",
            ReadSchema(profile)),
            async (input, _, cancellationToken) =>
            {
                var path = RequiredString(input, "path");
                ValidatePath(path);
                var paging = ReadPaging.Parse(input, profile);
                var lines = (await sandbox.ReadTextAsync(path, cancellationToken).ConfigureAwait(false)).Split('\n');
                return paging.Render(lines);
            });
        return new ReadFileTool(files, text, options, model, identifier, rebindOnDelegation);
    }

    /// <summary>Creates the write tool through the supplied filesystem.</summary>
    public static ITool CreateWrite(ISandbox sandbox)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        return new FunctionTool(new("write", "Write a UTF-8 text file, creating it or overwriting it. Use edit for a precise replacement.",
            Schema("""{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}""")),
            async (input, _, cancellationToken) =>
            {
                var path = RequiredString(input, "path");
                var content = RequiredString(input, "content");
                ValidatePath(path);
                await sandbox.WriteTextAsync(path, content, cancellationToken).ConfigureAwait(false);
                var lineCount = content.Length == 0 ? 0 : content.Count(character => character == '\n') + 1;
                return $"Wrote {lineCount} lines to {path}.";
            });
    }

    /// <summary>Creates the edit tool, requiring one exact non-empty match before writing.</summary>
    public static ITool CreateEdit(ISandbox sandbox)
    {
        Guard.NotNull(sandbox, nameof(sandbox));
        return new FunctionTool(new("edit", "Replace an exact string in a UTF-8 text file. old_str must be non-empty and appear exactly once.",
            Schema("""{"type":"object","properties":{"path":{"type":"string"},"old_str":{"type":"string","minLength":1},"new_str":{"type":"string"}},"required":["path","old_str","new_str"],"additionalProperties":false}""")),
            async (input, _, cancellationToken) =>
            {
                var path = RequiredString(input, "path");
                var oldText = RequiredString(input, "old_str");
                var newText = RequiredString(input, "new_str");
                ValidatePath(path);
                if (oldText.Length == 0) throw new ArgumentException("old_str must be non-empty.", nameof(input));
                var content = await sandbox.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
                var first = content.IndexOf(oldText, StringComparison.Ordinal);
                if (first < 0) throw new ArgumentException($"old_str did not appear verbatim in {path}.", nameof(input));
                if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
                    throw new ArgumentException($"old_str appears more than once in {path}; make it unique.", nameof(input));
                await sandbox.WriteTextAsync(path, content.Substring(0, first) + newText + content.Substring(first + oldText.Length), cancellationToken).ConfigureAwait(false);
                return $"Edited {path}.";
            });
    }

    internal static void ValidatePath(string path)
    {
        Guard.NotBlank(path, nameof(path));
        var driveRoot = path.Length >= 3 && Guard.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';
        var absolute = path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\\\", StringComparison.Ordinal) || driveRoot;
        if (!absolute) throw new ArgumentException("The path must be absolute: a POSIX root, Windows drive, or UNC share.", nameof(path));
        if (path.Split(new[] { '/', '\\' }).Contains("..", StringComparer.Ordinal))
            throw new ArgumentException("Invalid path: path traversal is not allowed.", nameof(path));
        if (path.IndexOf('\0') >= 0) throw new ArgumentException("Paths cannot contain a null character.", nameof(path));
    }

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static string RequiredString(JsonElement input, string name) => input.GetProperty(name).GetString()
        ?? throw new ArgumentException($"{name} must be a string.", nameof(input));
    private static JsonElement ReadSchema(ReadPagingProfile profile) => Schema(profile switch
    {
        ReadPagingProfile.Python => """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":["number","string","boolean","null"]},"limit":{"type":["number","string","boolean","null"]}},"required":["path"],"additionalProperties":false}""",
        ReadPagingProfile.TypeScript => """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":"number"},"limit":{"type":"number"}},"required":["path"],"additionalProperties":false}""",
        _ => """{"type":"object","properties":{"path":{"type":"string"},"offset":{"type":["integer","null"]},"limit":{"type":["integer","null"]}},"required":["path"],"additionalProperties":false}"""
    });

}
