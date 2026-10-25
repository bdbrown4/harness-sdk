using System.Text.Json;
using System.Text.Json.Nodes;

namespace Strands.Agents.Bedrock;

internal static class BedrockStrictSchema
{
    private static readonly string[] MapKeywords = ["$defs", "definitions", "properties"];
    private static readonly string[] ListKeywords = ["anyOf", "allOf", "oneOf"];
    private static readonly string[] ValueKeywords = ["items", "additionalProperties"];
    private static readonly string[] UnsupportedKeywords = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength"];

    internal static JsonElement Apply(JsonElement schema, out IReadOnlyList<string> warnings)
    {
        try { return Transform(schema, out warnings); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new ModelException("The strict tool schema is invalid or exceeds the supported JSON representation.", error); }
    }

    private static JsonElement Transform(JsonElement schema, out IReadOnlyList<string> warnings)
    {
        var root = JsonNode.Parse(schema.GetRawText()) as JsonObject ?? throw new ModelException("A strict tool schema must be an object.");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var remaining = 100_000;
        Visit(root, new HashSet<string>(StringComparer.Ordinal), 0);
        warnings = Array.AsReadOnly(found.ToArray());
        return JsonSerializer.SerializeToElement(root);

        void Visit(JsonObject node, HashSet<string> inlining, int depth)
        {
            if (--remaining < 0 || depth > 64) throw new ModelException("The strict tool schema exceeds its transformation depth or node limit.");
            var objectType = node["type"] is JsonValue value && value.TryGetValue<string>(out var type) && type == "object"
                || node["type"] is JsonArray types && types.Any(item => item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemType) && itemType == "object");
            if (objectType && !node.ContainsKey("additionalProperties") && !node.ContainsKey("$ref")) node["additionalProperties"] = false;
            foreach (var child in Children(node)) Visit(child, inlining, depth + 1);
            if (node["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var path) && node.Count > 1)
            {
                var resolved = inlining.Contains(path) ? null : ExpandReference(path, new HashSet<string>(inlining, StringComparer.Ordinal), depth + 1);
                if (inlining.Contains(path) || resolved is null) found.Add("$ref");
                else
                {
                    var merged = (JsonObject)resolved.DeepClone();
                    foreach (var property in node) merged[property.Key] = property.Value?.DeepClone();
                    merged.Remove("$ref");
                    node.Clear();
                    foreach (var property in merged) node[property.Key] = property.Value?.DeepClone();
                    Visit(node, new HashSet<string>(inlining, StringComparer.Ordinal) { path }, depth + 1);
                }
            }
            foreach (var keyword in UnsupportedKeywords) if (node.ContainsKey(keyword)) found.Add(keyword);
            if (node.TryGetPropertyValue("additionalProperties", out var additional) && !(additional is JsonValue flag && flag.TryGetValue<bool>(out var allow) && !allow)) found.Add("additionalProperties");
        }

        JsonObject? ExpandReference(string reference, HashSet<string> seen, int depth)
        {
            if (--remaining < 0 || depth > 64) throw new ModelException("The strict tool schema exceeds its transformation depth or node limit.");
            if (!seen.Add(reference) || Resolve(root, reference) is not { } target) { found.Add("$ref"); return null; }
            var copy = (JsonObject)target.DeepClone();
            if (copy["$ref"] is not JsonValue next || !next.TryGetValue<string>(out var nextReference)) return copy;
            var parent = ExpandReference(nextReference, seen, depth + 1);
            if (parent is null) return null;
            foreach (var property in copy) if (property.Key != "$ref") parent[property.Key] = property.Value?.DeepClone();
            return parent;
        }
    }

    private static IEnumerable<JsonObject> Children(JsonObject node)
    {
        foreach (var keyword in MapKeywords)
            if (node[keyword] is JsonObject map)
                foreach (var child in map.Select(property => property.Value).OfType<JsonObject>()) yield return child;
        foreach (var keyword in ListKeywords)
            if (node[keyword] is JsonArray list)
                foreach (var child in list.OfType<JsonObject>()) yield return child;
        foreach (var keyword in ValueKeywords) if (node[keyword] is JsonObject child) yield return child;
    }

    private static JsonObject? Resolve(JsonObject root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        JsonNode? current = root;
        foreach (var segment in reference.Substring(2).Split('/'))
        {
            var key = segment.Replace("~1", "/").Replace("~0", "~");
            if (current is not JsonObject map || !map.TryGetPropertyValue(key, out current)) return null;
        }
        return current as JsonObject;
    }
}
