using System.Text.Json;

namespace Strands.Agents.Models;

internal sealed class ChatCompletionsResponseNormalizer(string provider)
{
    private readonly Dictionary<string, int> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _wireIndices = [];
    private readonly HashSet<int> _indices = [];
    internal JsonElement Normalize(JsonElement root, bool completeResponse)
    {
        var normalized = CopyObject(root);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return root;
        normalized["choices"] = choices.EnumerateArray().Select(choice => NormalizeChoice(choice, completeResponse)).ToArray();
        return JsonSerializer.SerializeToElement(normalized);
    }
    private Dictionary<string, object?> NormalizeChoice(JsonElement choice, bool completeResponse)
    {
        var result = CopyObject(choice);
        if (!result.ContainsKey("index")) result["index"] = 0;
        var delta = CopyObject(ProviderHttp.RequiredObject(choice, completeResponse ? "message" : "delta"));
        if (delta.TryGetValue("tool_calls", out var value) && value is JsonElement { ValueKind: JsonValueKind.Array } tools)
            delta["tool_calls"] = tools.EnumerateArray().Select(tool => NormalizeTool(tool, completeResponse)).ToArray();
        result["delta"] = delta;
        if (ProviderHttp.OptionalString(choice, "finish_reason") is { } reason)
            result["finish_reason"] = reason is "tool_calls" or "length" ? reason : "stop";
        return result;
    }
    private Dictionary<string, object?> NormalizeTool(JsonElement tool, bool completeResponse)
    {
        var result = CopyObject(tool);
        var id = ProviderHttp.OptionalString(tool, "id");
        int? wireIndex = tool.TryGetProperty("index", out _) ? ProviderHttp.RequiredIndex(tool) : null;
        int index;
        if (id is not null && _identities.TryGetValue(id, out var known)) index = known;
        else if (wireIndex is { } supplied && _wireIndices.TryGetValue(supplied, out known)) index = known;
        else if (id is not null || wireIndex is not null) index = _indices.Count;
        else if (_indices.Count == 1) index = _indices.Single();
        else throw new ModelException($"{provider} tool fragments need an unambiguous index or identifier.");
        if (wireIndex is { } wire)
        {
            if (_wireIndices.TryGetValue(wire, out var previous) && previous != index) throw new ModelException($"{provider} changed a tool's stream index.");
            _wireIndices[wire] = index;
        }
        if (id is not null) _identities[id] = index;
        _indices.Add(index);
        result["index"] = index;
        if (completeResponse && tool.TryGetProperty("function", out var function))
        {
            var normalized = CopyObject(function);
            if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object) normalized["arguments"] = arguments.GetRawText();
            result["function"] = normalized;
        }
        return result;
    }
    private Dictionary<string, object?> CopyObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ModelException($"{provider} response fields must be JSON objects.");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (result.ContainsKey(property.Name)) throw new ModelException($"{provider} response contains a duplicate '{property.Name}' field.");
            result.Add(property.Name, property.Value.Clone());
        }
        return result;
    }
}
