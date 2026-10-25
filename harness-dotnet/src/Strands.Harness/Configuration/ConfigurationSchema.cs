using System.Text.Json;

namespace Strands.Harness;

// Keep parsing independent of runtime component loading. Portable configurations may describe capabilities
// not yet implemented by this runtime; conversion reports those capabilities instead of discarding them.
internal static class ConfigurationSchema
{
    internal static readonly string[] BuiltinNames = ["shell", "read", "write", "edit", "web_fetch", "web_search", "programmatic_tool_caller", "subagent"];
    private static readonly string[] PluginNames = ["todos", "environment"];
    private const string Defaults = """
        {"name":"Strands harness","description":"","instructions":"","model":"bedrock/global.anthropic.claude-opus-5","modelModule":null,"effort":"auto","tools":[],"subagents":[],"mcpServers":{},"builtinTools":["shell","read","write","edit","web_fetch","web_search","programmatic_tool_caller","subagent"],"caching":true,"contextManager":"auto","session":true,"skills":true,"memory":true,"memoryStores":[],"plugins":[],"builtinPlugins":["todos","environment"],"interventions":null,"interventionModules":[],"sandbox":null,"agentConfigModules":{},"dependencies":{"typescript":{},"python":[]},"agentConfig":{}}
        """;

    internal static JsonElement Normalize(JsonElement input)
    {
        Object(input, "agent config");
        RejectDuplicateKeys(input, string.Empty);
        using var defaults = JsonDocument.Parse(Defaults);
        var known = new HashSet<string>(defaults.RootElement.EnumerateObject().Select(item => item.Name), StringComparer.Ordinal);
        var unknown = input.EnumerateObject().Where(property => !known.Contains(property.Name)).Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) HarnessLogging.Warning("Ignoring unknown agent config keys: {Keys}.", (object)unknown);
        var values = new List<KeyValuePair<string, JsonElement>>();
        foreach (var property in defaults.RootElement.EnumerateObject())
        {
            var value = input.TryGetProperty(property.Name, out var configured) ? configured : property.Value;
            values.Add(new(property.Name, NormalizeField(property.Name, value)));
        }
        return JsonObject(values);
    }

    private static JsonElement NormalizeField(string name, JsonElement value)
    {
        switch (name)
        {
            case "name": case "model": NonBlank(value, name); break;
            case "description": case "instructions": String(value, name); break;
            case "effort": Enum(value, name, ["auto", "off", "minimal", "low", "medium", "high", "xhigh", "max"]); break;
            case "modelModule": return NullableReference(value, "model", name);
            case "sandbox": return NullableReference(value, "sandbox", name);
            case "tools": return References(value, "tool", name);
            case "subagents": return References(value, "subagent", name);
            case "plugins": return References(value, "plugin", name);
            case "memoryStores": return References(value, "memory-store", name);
            case "interventionModules": return References(value, "intervention", name);
            case "builtinTools": return Builtins(value, name);
            case "builtinPlugins": Strings(value, name, unique: true, allowed: PluginNames); break;
            case "caching": Boolean(value, name); break;
            case "contextManager":
                if (value.ValueKind == JsonValueKind.String)
                {
                    Enum(value, name, ["auto", "agentic", "off"]);
                    if (value.GetString() == "off") return Literal("false");
                }
                else if (value.ValueKind != JsonValueKind.False) Fail(name, "must be auto, agentic, off, or false.");
                break;
            case "session": BoolOrObject(value, name, ["id", "dir"]); break;
            case "memory": BoolOrObject(value, name, ["dir"]); break;
            case "skills":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) break;
                if (value.ValueKind == JsonValueKind.String) NonBlank(value, name);
                else Strings(value, name, unique: true);
                break;
            case "interventions":
                if (value.ValueKind == JsonValueKind.Null) break;
                if (value.ValueKind == JsonValueKind.String) NonBlank(value, name);
                else Strings(value, name);
                break;
            case "mcpServers":
                if (value.ValueKind == JsonValueKind.String) NonBlank(value, name);
                else Object(value, name);
                break;
            case "agentConfig": Object(value, name); break;
            case "agentConfigModules":
                Object(value, name);
                return JsonObject(value.EnumerateObject().Select(item =>
                {
                    if (string.IsNullOrWhiteSpace(item.Name)) Fail(name, "must have nonempty property names.");
                    return new KeyValuePair<string, JsonElement>(item.Name, Reference(item.Value, "agent-config", name + "." + item.Name));
                }));
            case "dependencies": return Dependencies(value, name);
            default: throw new InvalidOperationException("Unknown configuration schema field: " + name);
        }
        return value.Clone();
    }

    private static JsonElement Builtins(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Array) { Strings(value, path, unique: true, allowed: BuiltinNames); return value.Clone(); }
        Object(value, path, BuiltinNames.Concat(new[] { "*" }).ToArray());
        return JsonObject(value.EnumerateObject().Select(property => new KeyValuePair<string, JsonElement>(property.Name, ToolSetting(property.Name, property.Value, path + "." + property.Name))));
    }

    private static JsonElement ToolSetting(string name, JsonElement value, string path)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.Clone();
        if (name == "web_search") { Enum(value, path, ["exa"]); return value.Clone(); }
        var fields = name switch
        {
            "read" => new[] { "media" },
            "shell" => ["description"],
            "web_fetch" => ["model", "transport"],
            "programmatic_tool_caller" => ["allowedTools", "timeout"],
            "subagent" => ["maxDepth"],
            _ => System.Array.Empty<string>(),
        };
        if (fields.Length == 0) { Boolean(value, path); return value.Clone(); }
        Object(value, path, fields);
        var values = new List<KeyValuePair<string, JsonElement>>();
        foreach (var property in value.EnumerateObject())
        {
            var item = property.Value;
            var propertyPath = path + "." + property.Name;
            switch (property.Name)
            {
                case "media": Boolean(item, propertyPath); break;
                case "description": NonBlank(item, propertyPath); break;
                case "transport": Enum(item, propertyPath, ["curl", "direct"]); break;
                case "model":
                    if (item.ValueKind == JsonValueKind.String) NonBlank(item, propertyPath);
                    else item = Reference(item, "model", propertyPath);
                    break;
                case "allowedTools": if (item.ValueKind != JsonValueKind.Null) Strings(item, propertyPath); break;
                case "timeout":
                    if (item.ValueKind != JsonValueKind.Null && (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var seconds) || double.IsInfinity(seconds) || seconds <= 0)) Fail(propertyPath, "must be a positive finite number or null.");
                    break;
                case "maxDepth":
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var depth) || depth < 0) Fail(propertyPath, "must be a non-negative integer.");
                    break;
            }
            values.Add(new(property.Name, item));
        }
        return JsonObject(values);
    }

    private static JsonElement NullableReference(JsonElement value, string kind, string path) => value.ValueKind == JsonValueKind.Null ? value.Clone() : Reference(value, kind, path);
    private static JsonElement References(JsonElement value, string kind, string path)
    {
        Array(value, path);
        return JsonArray(value.EnumerateArray().Select((item, index) => Reference(item, kind, path + "[" + index + "]")));
    }
    private static JsonElement Reference(JsonElement value, string kind, string path)
    {
        Object(value, path);
        if (!value.TryGetProperty("module", out var module)) Fail(path + ".module", "is required.");
        NonBlank(module, path + ".module");
        if (value.TryGetProperty("kind", out var specifiedKind)) Enum(specifiedKind, path + ".kind", [kind]);
        if (value.TryGetProperty("export", out var export)) NonBlank(export, path + ".export");
        if (value.TryGetProperty("language", out var language)) Enum(language, path + ".language", ["typescript", "python", "csharp"]);
        if (value.TryGetProperty("files", out var files)) Strings(files, path + ".files");
        if (value.TryGetProperty("dependency", out var dependency)) NonBlank(dependency, path + ".dependency");
        var properties = new List<KeyValuePair<string, JsonElement>> { new("kind", JsonString(kind)) };
        properties.AddRange(value.EnumerateObject().Where(property => property.Name != "kind").Select(property => new KeyValuePair<string, JsonElement>(property.Name, property.Value)));
        return JsonObject(properties);
    }

    private static JsonElement Dependencies(JsonElement value, string path)
    {
        Object(value, path, ["typescript", "python"]);
        var typescript = value.TryGetProperty("typescript", out var ts) ? ts : Literal("{}");
        Object(typescript, path + ".typescript");
        foreach (var property in typescript.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)) Fail(path + ".typescript", "must have nonempty package names.");
            NonBlank(property.Value, path + ".typescript." + property.Name);
        }
        var python = value.TryGetProperty("python", out var py) ? py : Literal("[]");
        Strings(python, path + ".python", unique: true);
        foreach (var entry in python.EnumerateArray())
            if (entry.GetString()!.IndexOfAny(['\r', '\n']) >= 0) Fail(path + ".python", "entries must not contain line breaks.");
        return JsonObject([new("typescript", typescript), new("python", python)]);
    }

    private static void BoolOrObject(JsonElement value, string path, string[] fields)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        Object(value, path, fields);
        foreach (var property in value.EnumerateObject()) NonBlank(property.Value, path + "." + property.Name);
    }
    internal static void Object(JsonElement value, string path, string[]? allowed = null)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail(path, "must be an object.");
        if (allowed is null) return;
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) Fail(path + "." + property.Name, "is an unknown field. Allowed: " + string.Join(", ", allowed) + ".");
    }
    private static void Array(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array) Fail(path, "must be an array.");
    }
    private static void String(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String) Fail(path, "must be a string.");
    }
    private static void NonBlank(JsonElement value, string path)
    {
        String(value, path);
        if (string.IsNullOrWhiteSpace(value.GetString())) Fail(path, "must be a non-empty string.");
    }
    private static void Boolean(JsonElement value, string path)
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail(path, "must be a boolean.");
    }
    private static void Enum(JsonElement value, string path, string[] allowed)
    {
        String(value, path);
        if (!allowed.Contains(value.GetString(), StringComparer.Ordinal)) Fail(path, "must be one of: " + string.Join(", ", allowed) + ".");
    }
    private static void Strings(JsonElement value, string path, bool unique = false, string[]? allowed = null)
    {
        Array(value, path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in value.EnumerateArray())
        {
            var entryPath = path + "[" + index++ + "]";
            NonBlank(entry, entryPath);
            if (allowed is not null) Enum(entry, entryPath, allowed);
            if (unique && !seen.Add(entry.GetString()!)) Fail(path, "must not contain duplicate entries.");
        }
    }
    private static void RejectDuplicateKeys(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : path + "." + property.Name;
                if (!names.Add(property.Name)) Fail(childPath, "must not be repeated.");
                RejectDuplicateKeys(property.Value, childPath);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item, path + "[" + index++ + "]");
        }
    }
    private static JsonElement Literal(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static JsonElement JsonString(string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(text);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
    private static JsonElement JsonObject(IEnumerable<KeyValuePair<string, JsonElement>> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var item in values) { writer.WritePropertyName(item.Key); item.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
    private static JsonElement JsonArray(IEnumerable<JsonElement> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in values) item.WriteTo(writer);
            writer.WriteEndArray();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
    private static void Fail(string path, string message) => throw new ArgumentException(path + " " + message);
}
