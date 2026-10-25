using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>Optional text notebooks stored under the current Agent's notebooks state key.</summary>
public static class Notebook
{
    private static readonly ConditionalWeakTable<StateStore, object> Gates = new();
    private const string StateKey = "notebooks";
    private const string TypeScriptDescription = "Manages text notebooks for note-taking and documentation. Supports create, list, read, write (append, replace, or insert), and clear operations. In write mode: newStr alone appends to the end; newStr with oldStr replaces matching text; newStr with insertLine inserts at a position. A write only succeeds on a notebook that already exists: use list to check, or create to start a new one (create replaces any existing content). Notebooks persist within the agent invocation.";
    private const string PythonDescription = "Manages text notebooks for note-taking and documentation. Supports create, list, read, write (append, replace, or insert), and clear operations. In write mode: new_str alone appends to the end; new_str with old_str replaces matching text; new_str with insert_line inserts at a position. Write operations only succeed on notebooks that already exist; use list to check or create to initialize one (create overwrites any existing content).";
    /// <summary>Creates a notebook tool. The Agent and its state remain borrowed.</summary>
    public static ITool Create(NotebookConfig? config = null)
    {
        config ??= new();
        Guard.NotBlank(config.Name, nameof(config.Name));
        if (config.MaximumNotebookSizeBytes < 1) throw new ArgumentOutOfRangeException(nameof(config.MaximumNotebookSizeBytes));
        if (config.Behavior is not (NotebookBehavior.TypeScript or NotebookBehavior.Python)) throw new ArgumentOutOfRangeException(nameof(config.Behavior));
        var python = config.Behavior == NotebookBehavior.Python;
        var maximumBytes = config.MaximumNotebookSizeBytes;
        var properties = new Dictionary<string, object?>
        {
            ["mode"] = new { type = "string", @enum = new[] { "create", "list", "read", "write", "clear" } },
            ["name"] = new { type = python ? new[] { "string", "null" } : new[] { "string" } },
            [python ? "new_str" : "newStr"] = new { type = python ? new[] { "string", "null" } : new[] { "string" } },
            [python ? "old_str" : "oldStr"] = new { type = python ? new[] { "string", "null" } : new[] { "string" } },
            [python ? "insert_line" : "insertLine"] = new { type = python ? new[] { "string", "integer", "null" } : new[] { "string", "integer" } },
            [python ? "read_range" : "readRange"] = new Dictionary<string, object?> { ["type"] = python ? new[] { "array", "null" } : new[] { "array" }, ["items"] = new { type = "integer" }, ["minItems"] = 2 }
        };
        if (python) ((Dictionary<string, object?>)properties["read_range"]!)["maxItems"] = 2;
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties, required = new[] { "mode" } });
        return new FunctionTool(new ToolSpec(config.Name, config.Description ?? (python ? PythonDescription : TypeScriptDescription), schema),
            (input, context, token) => new ValueTask<string>(Execute(input, context, token, python, maximumBytes)));
    }

    private static string Execute(JsonElement input, ToolContext context, CancellationToken cancellationToken, bool python, int maximumBytes)
    {
        var signal = context.CancelSignal;
        CheckCancellation();
        var request = Parse(input, python);
        var state = context.Agent.State;
        lock (Gates.GetValue(state, _ => new object()))
        {
            CheckCancellation();
            var notebooks = ReadNotebooks(state);
            if (notebooks.Count == 0) notebooks.Add(new("default", ""));
            var position = notebooks.FindIndex(pair => string.Equals(pair.Key, request.Name, StringComparison.Ordinal));
            string result;
            switch (request.Mode)
            {
                case "create":
                    Set(request.NewText ?? "");
                    result = "Created notebook '" + request.Name + "'" + (string.IsNullOrEmpty(request.NewText) ? " (empty)" : " with specified content");
                    break;
                case "list":
                    IEnumerable<KeyValuePair<string, string>> ordered = notebooks;
                    if (!python) ordered = notebooks.Where(pair => ArrayIndex(pair.Key, out _)).OrderBy(pair => uint.Parse(pair.Key, CultureInfo.InvariantCulture))
                        .Concat(notebooks.Where(pair => !ArrayIndex(pair.Key, out _)));
                    result = "Available notebooks:\n" + string.Join("\n", ordered.Select(pair => "- " + pair.Key + ": " + (pair.Value.Length == 0 ? "Empty" : pair.Value.Split('\n').Length.ToString(CultureInfo.InvariantCulture) + " lines")));
                    break;
                case "read":
                    result = Read(Content(), request.Name, request.Range);
                    break;
                case "write":
                    result = Write(Content(), request, Set);
                    break;
                case "clear":
                    _ = Content();
                    Set("");
                    result = "Cleared notebook '" + request.Name + "'";
                    break;
                default: throw new ArgumentException("Unknown notebook mode.", nameof(input));
            }
            if (request.Mode is "create" or "write")
            {
                var encoding = python ? new UTF8Encoding(false, true) : Encoding.UTF8;
                var bytes = encoding.GetByteCount(notebooks[position].Value);
                if (bytes > maximumBytes) throw new ArgumentException("Notebook '" + request.Name + "' content (" + bytes.ToString(CultureInfo.InvariantCulture) + " bytes) would exceed maximum of " + maximumBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
            }
            if (request.Mode is "create" or "write" or "clear")
            {
                var candidate = Serialize(notebooks);
                CheckCancellation();
                state.Set(StateKey, candidate);
            }
            return result;

            string Content() => position >= 0 ? notebooks[position].Value : throw new ArgumentException("Notebook '" + request.Name + "' not found");
            void Set(string text)
            {
                if (position < 0) { position = notebooks.Count; notebooks.Add(new(request.Name, text)); }
                else notebooks[position] = new(request.Name, text);
            }
        }
        void CheckCancellation() { cancellationToken.ThrowIfCancellationRequested(); signal.ThrowIfCancellationRequested(); }
    }

    private static List<KeyValuePair<string, string>> ReadNotebooks(StateStore state)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (state.Get(StateKey) is not { } value || value.ValueKind == JsonValueKind.Null) return result;
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Malformed notebooks state: expected an object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || !names.Add(property.Name)) throw new ArgumentException("Malformed notebooks state: keys must be unique and values must be strings");
            result.Add(new(property.Name, property.Value.GetString()!));
        }
        return result;
    }

    private static JsonElement Serialize(IEnumerable<KeyValuePair<string, string>> notebooks)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var pair in notebooks) writer.WriteString(pair.Key, pair.Value);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static string Read(string content, string name, double[]? range)
    {
        if (range is null) return content.Length == 0 ? "Notebook '" + name + "' is empty" : content;
        var lines = content.Split('\n');
        var start = range[0] < 0 ? lines.Length + range[0] + 1 : range[0];
        var end = range[1] < 0 ? lines.Length + range[1] + 1 : range[1];
        var first = Math.Max(start, 1); var last = Math.Min(end, lines.Length);
        if (first > last) return "No lines found in range [" + Number(range[0]) + ", " + Number(range[1]) + "]. Notebook '" + name + "' has " + lines.Length.ToString(CultureInfo.InvariantCulture) + " line(s).";
        var selected = new List<string>();
        for (var index = (int)first - 1; index < (int)last; index++) selected.Add((index + 1).ToString(CultureInfo.InvariantCulture) + ": " + lines[index]);
        return string.Join("\n", selected);
    }

    private static string Write(string content, Request request, Action<string> set)
    {
        if (request.NewText is null) throw new ArgumentException("Write operation requires new text, optionally with a replacement or insertion anchor");
        if (request.OldText is not null && request.Anchor is not null) throw new ArgumentException("Write operation is ambiguous: replacement and insertion cannot be combined");
        if (request.OldText is { } old)
        {
            var index = content.IndexOf(old, StringComparison.Ordinal);
            if (index < 0) throw new ArgumentException("String '" + old + "' not found in notebook '" + request.Name + "'");
            set(content.Substring(0, index) + request.NewText + content.Substring(index + old.Length));
            return "Replaced text in notebook '" + request.Name + "'";
        }
        if (request.Anchor is { } anchor)
        {
            var lines = content.Split('\n').ToList();
            double line;
            if (anchor is string text)
            {
                line = lines.FindIndex(value => value.IndexOf(text, StringComparison.Ordinal) >= 0);
                if (line < 0) throw new ArgumentException("Text '" + text + "' not found in notebook '" + request.Name + "'");
            }
            else { var number = (double)anchor; line = number < 0 ? lines.Count + number : number - 1; }
            if (line < -1 || line > lines.Count) throw new ArgumentException("Line number out of range");
            lines.Insert((int)Math.Min(line + 1, lines.Count), request.NewText);
            set(string.Join("\n", lines));
            return "Inserted text at line " + Number(line + 2) + " in notebook '" + request.Name + "'";
        }
        if (request.NewText.Length == 0) return "No changes made to notebook '" + request.Name + "'";
        set(content + (content.Length > 0 && !content.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "") + request.NewText);
        return "Appended text to notebook '" + request.Name + "'";
    }

    private static Request Parse(JsonElement input, bool python)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Notebook input must be an object.", nameof(input));
        var mode = OptionalString("mode") ?? throw new ArgumentException("Notebook mode is required.");
        if (mode is not ("create" or "list" or "read" or "write" or "clear")) throw new ArgumentException("Unknown notebook mode: " + mode);
        var name = OptionalString("name") ?? "default";
        var newText = OptionalString(python ? "new_str" : "newStr");
        var oldText = OptionalString(python ? "old_str" : "oldStr");
        object? anchor = null;
        if (Optional(python ? "insert_line" : "insertLine") is { } inserted)
            anchor = inserted.ValueKind == JsonValueKind.String ? inserted.GetString()! : Integer(inserted);
        double[]? range = null;
        if (Optional(python ? "read_range" : "readRange") is { } requested)
        {
            if (requested.ValueKind != JsonValueKind.Array || requested.GetArrayLength() < 2 || (python && requested.GetArrayLength() != 2)) throw new ArgumentException("Read range must contain " + (python ? "exactly" : "at least") + " two integers.");
            range = requested.EnumerateArray().Select(Integer).ToArray();
        }
        return new(mode, name, newText, oldText, anchor, range);

        JsonElement? Optional(string key)
        {
            if (!input.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Null && python) return null;
            return value;
        }
        string? OptionalString(string key)
        {
            if (Optional(key) is not { } value) return null;
            if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(key + " must be a string.");
            return value.GetString();
        }
    }

    private static double Integer(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number) || !IntegralLexeme(value.GetRawText())) throw new ArgumentException("Notebook line indices must be finite integral JSON numbers.");
        return number;
    }
    private static bool IntegralLexeme(string raw)
    {
        // Check the actual JSON number, rather than a rounded double such as 1.000...001.
        var exponentAt = raw.IndexOfAny(new[] { 'e', 'E' });
        var end = exponentAt < 0 ? raw.Length : exponentAt;
        var dot = raw.IndexOf('.');
        var fraction = dot < 0 ? 0 : end - dot - 1;
        var trailing = 0; var nonzero = false;
        for (var index = 0; index < end; index++)
        {
            var digit = raw[index];
            if (digit is '-' or '.') continue;
            if (digit != '0') { nonzero = true; trailing = 0; } else trailing++;
        }
        if (!nonzero) return true;
        var exponent = 0;
        if (exponentAt >= 0 && !int.TryParse(raw.Substring(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return raw[exponentAt + 1] != '-';
        return (long)fraction - exponent <= trailing;
    }
    private static bool ArrayIndex(string name, out uint index) => uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index < uint.MaxValue && index.ToString(CultureInfo.InvariantCulture) == name;
    private static string Number(double number) => number == 0 ? "0" : number.ToString("G", CultureInfo.InvariantCulture);
    private sealed record Request(string Mode, string Name, string? NewText, string? OldText, object? Anchor, double[]? Range);
}
