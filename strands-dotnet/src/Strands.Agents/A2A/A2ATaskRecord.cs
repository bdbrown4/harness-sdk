using System.Text.Json;

namespace Strands.Agents.A2A;

internal static class A2ATaskRecord
{
    internal static string Validate(JsonElement task)
    {
        A2AJson.ValidateUniqueProperties(task); A2AJson.RequireObject(task);
        if (A2AJson.RequiredString(task, "kind") != "task") throw new A2AProtocolException("A stored record must be an A2A Task.");
        var id = A2AJson.RequiredString(task, "id"); _ = A2AJson.RequiredString(task, "contextId");
        var status = A2AJson.RequiredObject(task, "status");
        if (A2AJson.RequiredString(status, "state") is not ("submitted" or "working" or "input-required" or "auth-required" or "completed" or "canceled" or "failed" or "rejected" or "unknown"))
            throw new A2AProtocolException("The stored task state is invalid.");
        if (status.TryGetProperty("message", out var message)) ValidateMessage(message);
        if (task.TryGetProperty("history", out var history))
        {
            RequireArray(history, "history"); foreach (var item in history.EnumerateArray()) ValidateMessage(item);
        }
        if (task.TryGetProperty("artifacts", out var artifacts))
        {
            RequireArray(artifacts, "artifacts");
            foreach (var artifact in artifacts.EnumerateArray())
            {
                _ = A2AJson.RequiredString(artifact, "artifactId");
                if (!artifact.TryGetProperty("parts", out var parts)) throw new A2AProtocolException("A stored artifact requires parts.");
                ValidateParts(parts);
            }
        }
        return id;
    }
    private static void ValidateMessage(JsonElement message)
    {
        if (A2AJson.RequiredString(message, "kind") != "message" || A2AJson.RequiredString(message, "role") is not ("user" or "agent"))
            throw new A2AProtocolException("Stored history and status messages must be A2A Messages.");
        _ = A2AJson.RequiredString(message, "messageId");
        if (!message.TryGetProperty("parts", out var parts)) throw new A2AProtocolException("A stored message requires parts.");
        ValidateParts(parts);
    }
    private static void ValidateParts(JsonElement parts)
    {
        RequireArray(parts, "parts");
        foreach (var part in parts.EnumerateArray())
        {
            switch (A2AJson.RequiredString(part, "kind"))
            {
                case "text":
                    if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new A2AProtocolException("A text part requires string text.");
                    break;
                case "data": _ = A2AJson.RequiredObject(part, "data"); break;
                case "file":
                    var file = A2AJson.RequiredObject(part, "file");
                    var hasBytes = file.TryGetProperty("bytes", out var bytes); var hasUri = file.TryGetProperty("uri", out var uri);
                    if (hasBytes == hasUri || (hasBytes ? bytes : uri).ValueKind != JsonValueKind.String) throw new A2AProtocolException("A file part requires exactly one string bytes or uri field.");
                    break;
                default: throw new A2AProtocolException("The stored part kind is invalid.");
            }
        }
    }
    private static void RequireArray(JsonElement value, string name)
    { if (value.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("Stored " + name + " must be an array."); }
}
