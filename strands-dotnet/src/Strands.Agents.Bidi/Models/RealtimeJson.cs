using System.Text.Json;

namespace Strands.Agents.Bidi.Models;

internal static class RealtimeJson
{
    internal static JsonElement Object(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var result) || result.ValueKind != JsonValueKind.Object) throw new ModelException("Realtime requires object field '" + name + "'.");
        return result;
    }
    internal static string Text(JsonElement value, string name)
        => OptionalText(value, name) is { Length: > 0 } text ? text : throw new ModelException("Realtime requires string field '" + name + "'.");
    internal static string? OptionalText(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (field.ValueKind != JsonValueKind.String) throw new ModelException("Realtime field '" + name + "' must be a string.");
        return field.GetString();
    }
    internal static long Count(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field)) return 0;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var count) || count < 0) throw new ModelException("Realtime token counters must be nonnegative integers.");
        return count;
    }
    internal static Dictionary<string, object?> Copy(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Realtime configuration must be an object.", nameof(value));
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (result.ContainsKey(property.Name)) throw new ArgumentException("Duplicate Realtime JSON field '" + property.Name + "'.", nameof(value));
            if (property.Value.ValueKind == JsonValueKind.Array) ValidateArray(property.Value);
            result.Add(property.Name, property.Value.ValueKind == JsonValueKind.Object ? Copy(property.Value) : property.Value.Clone());
        }
        return result;
    }
    private static void ValidateArray(JsonElement value)
    {
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) _ = Copy(item);
            else if (item.ValueKind == JsonValueKind.Array) ValidateArray(item);
        }
    }
    internal static void Merge(Dictionary<string, object?> target, Dictionary<string, object?> values)
    {
        foreach (var item in values)
        {
            if (item.Value is Dictionary<string, object?> nested && target.TryGetValue(item.Key, out var previous) && previous is Dictionary<string, object?> existing) Merge(existing, nested);
            else target[item.Key] = item.Value;
        }
    }
}
