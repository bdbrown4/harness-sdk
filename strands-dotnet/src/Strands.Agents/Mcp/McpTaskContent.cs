using System.Text.Json;

namespace Strands.Agents.Mcp;

// The pinned task extension uses a narrower sampling union than the general MCP ContentBlock DTO.
internal static class McpTaskContent
{
    internal static void Validate(JsonElement content, bool sampling)
    {
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray()) ValidateBlock(item, sampling);
        }
        else ValidateBlock(content, sampling);
    }

    private static void ValidateBlock(JsonElement block, bool sampling)
    {
        Object(block);
        Optional(block, "_meta", JsonValueKind.Object);
        var type = String(block, "type");
        switch (type)
        {
            case "text": String(block, "text"); break;
            case "image":
            case "audio": String(block, "data"); String(block, "mimeType"); break;
            case "tool_use" when sampling:
                String(block, "id"); String(block, "name"); Object(Required(block, "input")); break;
            case "tool_result" when sampling:
                String(block, "toolUseId");
                var results = Required(block, "content");
                if (results.ValueKind != JsonValueKind.Array) throw Invalid();
                Validate(results, sampling: false);
                Boolean(block, "isError");
                break;
            case "resource_link" when !sampling:
                String(block, "name"); String(block, "uri"); break;
            case "resource" when !sampling:
                var resource = Required(block, "resource"); Object(resource); String(resource, "uri");
                Optional(resource, "mimeType", JsonValueKind.String);
                Optional(resource, "_meta", JsonValueKind.Object);
                Optional(resource, "text", JsonValueKind.String);
                Optional(resource, "blob", JsonValueKind.String);
                if (!resource.TryGetProperty("text", out _) && !resource.TryGetProperty("blob", out _)) throw Invalid();
                break;
            default: throw Invalid();
        }
        if (block.TryGetProperty("annotations", out var annotations))
        {
            Object(annotations);
            Optional(annotations, "lastModified", JsonValueKind.String);
            if (annotations.TryGetProperty("priority", out var priority) &&
                (priority.ValueKind != JsonValueKind.Number || !priority.TryGetDouble(out var number) || number < 0 || number > 1)) throw Invalid();
            if (annotations.TryGetProperty("audience", out var audience))
            {
                if (audience.ValueKind != JsonValueKind.Array) throw Invalid();
                foreach (var role in audience.EnumerateArray())
                    if (role.ValueKind != JsonValueKind.String || role.GetString() is not ("user" or "assistant")) throw Invalid();
            }
        }
    }

    private static JsonElement Required(JsonElement value, string field) => value.TryGetProperty(field, out var result) ? result : throw Invalid();
    private static string String(JsonElement value, string field)
    {
        var result = Required(value, field);
        return result.ValueKind == JsonValueKind.String ? result.GetString()! : throw Invalid();
    }
    private static void Object(JsonElement value) { if (value.ValueKind != JsonValueKind.Object) throw Invalid(); }
    private static void Optional(JsonElement value, string field, JsonValueKind kind)
    { if (value.TryGetProperty(field, out var result) && result.ValueKind != kind) throw Invalid(); }
    private static void Boolean(JsonElement value, string field)
    { if (value.TryGetProperty(field, out var result) && result.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(); }
    private static InvalidDataException Invalid() => new("MCP task content does not match the pinned content schema.");
}
