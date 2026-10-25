using System.Text.Json;

namespace Strands.Agents.A2A;

internal static class A2ARestJson
{
    internal static JsonElement WriteMessageRequest(JsonElement parameters)
    {
        var result = Copy(parameters, "message", "configuration");
        result["message"] = WriteMessage(A2AJson.RequiredObject(parameters, "message"));
        if (parameters.TryGetProperty("configuration", out var configuration))
        {
            var settings = Copy(configuration, "pushNotificationConfig");
            if (configuration.TryGetProperty("pushNotificationConfig", out var push)) settings["pushNotification"] = push;
            result["configuration"] = settings;
        }
        return Element(result);
    }

    private static object WriteMessage(JsonElement message)
    {
        var result = Copy(message, "kind", "role", "parts");
        result["role"] = A2AJson.RequiredString(message, "role") switch
        {
            "user" => "ROLE_USER",
            "agent" => "ROLE_AGENT",
            _ => throw new A2AProtocolException("An A2A message role must be user or agent.")
        };
        result["content"] = Array(message, "parts").Select(WritePart).ToArray();
        return result;
    }

    private static object WritePart(JsonElement part)
    {
        var result = new Dictionary<string, object?>();
        switch (A2AJson.RequiredString(part, "kind"))
        {
            case "text": result["text"] = Text(part, "text"); break;
            case "data": result["data"] = new { data = A2AJson.RequiredObject(part, "data") }; break;
            case "file":
                var file = A2AJson.RequiredObject(part, "file");
                var converted = new Dictionary<string, object?>();
                if (file.TryGetProperty("mimeType", out _)) converted["mimeType"] = Text(file, "mimeType");
                var uri = file.TryGetProperty("uri", out _);
                var bytes = file.TryGetProperty("bytes", out _);
                if (uri == bytes) throw new A2AProtocolException("An A2A file must contain exactly one URI or byte payload.");
                converted[uri ? "fileWithUri" : "fileWithBytes"] = Text(file, uri ? "uri" : "bytes");
                result["file"] = converted;
                break;
            default: throw new A2AProtocolException("The A2A part is not supported by HTTP+JSON.");
        }
        return result;
    }

    internal static JsonElement ReadMessageResult(JsonElement value) => ReadPayload(value, streaming: false);
    internal static JsonElement ReadStreamResult(JsonElement value) => ReadPayload(value, streaming: true);

    private static JsonElement ReadPayload(JsonElement value, bool streaming)
    {
        A2AJson.RequireObject(value);
        var arms = new[] { "message", "task", "statusUpdate", "artifactUpdate" }.Where(name => value.TryGetProperty(name, out _)).ToArray();
        if (arms.Length != 1 || !streaming && arms[0] is not ("message" or "task"))
            throw new A2AProtocolException("An A2A HTTP+JSON response requires exactly one supported result.");
        var payload = A2AJson.RequiredObject(value, arms[0]);
        return Element(arms[0] switch
        {
            "message" => ReadMessage(payload),
            "task" => Task(payload),
            "statusUpdate" => StatusUpdate(payload),
            "artifactUpdate" => ArtifactUpdate(payload),
            _ => throw new A2AProtocolException("The A2A response is unsupported.")
        });
    }

    internal static JsonElement ReadTask(JsonElement value) => Element(Task(value));

    private static object Task(JsonElement task)
    {
        var result = Copy(task, "status", "artifacts", "history");
        result["kind"] = "task";
        result["status"] = Status(A2AJson.RequiredObject(task, "status"));
        if (task.TryGetProperty("artifacts", out _)) result["artifacts"] = Repeated(task, "artifacts").Select(Artifact).ToArray();
        if (task.TryGetProperty("history", out _)) result["history"] = Repeated(task, "history").Select(ReadMessage).ToArray();
        return result;
    }

    private static object ReadMessage(JsonElement message)
    {
        var result = Copy(message, "role", "content");
        result["kind"] = "message";
        result["role"] = EnumValue(message, "role", "ROLE_UNSPECIFIED", "ROLE_USER", "ROLE_AGENT") switch
        {
            "ROLE_USER" => "user",
            "ROLE_AGENT" => "agent",
            _ => throw new A2AProtocolException("An A2A response message requires a user or agent role.")
        };
        result["parts"] = Repeated(message, "content").Select(ReadPart).ToArray();
        return result;
    }

    private static object ReadPart(JsonElement part)
    {
        A2AJson.RequireObject(part);
        var arms = new[] { "text", "data", "file" }.Where(name => part.TryGetProperty(name, out _)).ToArray();
        if (arms.Length != 1) throw new A2AProtocolException("An A2A HTTP+JSON part requires exactly one payload.");
        var result = Copy(part, "text", "data", "file");
        result["kind"] = arms[0];
        switch (arms[0])
        {
            case "text": result["text"] = Text(part, "text"); break;
            case "data": result["data"] = A2AJson.RequiredObject(A2AJson.RequiredObject(part, "data"), "data"); break;
            case "file":
                var file = A2AJson.RequiredObject(part, "file");
                var converted = Copy(file, "fileWithUri", "fileWithBytes");
                var uri = file.TryGetProperty("fileWithUri", out _);
                var bytes = file.TryGetProperty("fileWithBytes", out _);
                if (uri == bytes) throw new A2AProtocolException("An A2A HTTP+JSON file requires exactly one URI or byte payload.");
                converted[uri ? "uri" : "bytes"] = Text(file, uri ? "fileWithUri" : "fileWithBytes");
                result["file"] = converted;
                break;
        }
        return result;
    }

    private static object Status(JsonElement status)
    {
        var result = Copy(status, "state", "message");
        result["state"] = EnumValue(status, "state", "TASK_STATE_UNSPECIFIED", "TASK_STATE_SUBMITTED", "TASK_STATE_WORKING", "TASK_STATE_COMPLETED", "TASK_STATE_FAILED", "TASK_STATE_CANCELLED", "TASK_STATE_INPUT_REQUIRED", "TASK_STATE_REJECTED", "TASK_STATE_AUTH_REQUIRED") switch
        {
            "TASK_STATE_UNSPECIFIED" => "unknown",
            "TASK_STATE_SUBMITTED" => "submitted",
            "TASK_STATE_WORKING" => "working",
            "TASK_STATE_COMPLETED" => "completed",
            "TASK_STATE_FAILED" => "failed",
            "TASK_STATE_CANCELLED" => "canceled",
            "TASK_STATE_INPUT_REQUIRED" => "input-required",
            "TASK_STATE_REJECTED" => "rejected",
            "TASK_STATE_AUTH_REQUIRED" => "auth-required",
            _ => throw new A2AProtocolException("The A2A task state is unsupported.")
        };
        if (status.TryGetProperty("message", out var message)) result["message"] = ReadMessage(message);
        return result;
    }

    private static object Artifact(JsonElement artifact)
    {
        var result = Copy(artifact, "parts");
        result["parts"] = Repeated(artifact, "parts").Select(ReadPart).ToArray();
        return result;
    }

    private static object StatusUpdate(JsonElement update)
    {
        var result = Copy(update, "status");
        result["kind"] = "status-update";
        result["status"] = Status(A2AJson.RequiredObject(update, "status"));
        if (!update.TryGetProperty("final", out _)) result["final"] = false;
        return result;
    }

    private static object ArtifactUpdate(JsonElement update)
    {
        var result = Copy(update, "artifact");
        result["kind"] = "artifact-update";
        result["artifact"] = Artifact(A2AJson.RequiredObject(update, "artifact"));
        return result;
    }

    internal static A2ARpcException ReadError(JsonElement value)
    {
        A2AJson.RequireObject(value);
        if (!value.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number)) throw new A2AProtocolException("An A2A HTTP+JSON error requires an integer code.");
        return new(number, A2AJson.RequiredString(value, "message"), value.TryGetProperty("data", out var data) ? data : null);
    }

    internal static object WritePushConfiguration(string taskId, JsonElement configuration) => new
    {
        name = "tasks/" + taskId + "/pushNotificationConfigs/" + (A2AJson.OptionalString(configuration, "id") ?? ""),
        pushNotificationConfig = configuration
    };

    internal static JsonElement ReadPushConfiguration(JsonElement value) => Element(PushConfiguration(value));
    internal static JsonElement ReadPushConfigurations(JsonElement value) => Element(Repeated(value, "configs").Select(PushConfiguration).ToArray());

    private static object PushConfiguration(JsonElement value)
    {
        var name = A2AJson.RequiredString(value, "name");
        const string separator = "/pushNotificationConfigs/";
        var boundary = name.LastIndexOf(separator, StringComparison.Ordinal);
        if (!name.StartsWith("tasks/", StringComparison.Ordinal) || boundary <= "tasks/".Length)
            throw new A2AProtocolException("An A2A push configuration requires a task resource name.");
        return new { taskId = name.Substring("tasks/".Length, boundary - "tasks/".Length), pushNotificationConfig = A2AJson.RequiredObject(value, "pushNotificationConfig") };
    }

    internal static JsonElement ReadCard(JsonElement card)
    {
        var result = Copy(card, "security", "securitySchemes", "skills");
        if (!card.TryGetProperty("capabilities", out _)) result["capabilities"] = new { };
        foreach (var field in new[] { "defaultInputModes", "defaultOutputModes" })
            if (!card.TryGetProperty(field, out _)) result[field] = System.Array.Empty<string>();
        result["skills"] = Repeated(card, "skills").Select(skill =>
        {
            var converted = Copy(skill, "security");
            if (skill.TryGetProperty("security", out _)) converted["security"] = Security(skill);
            return converted;
        }).ToArray();
        if (card.TryGetProperty("security", out _)) result["security"] = Security(card);
        if (card.TryGetProperty("securitySchemes", out var schemes))
        {
            A2AJson.RequireObject(schemes);
            result["securitySchemes"] = schemes.EnumerateObject().ToDictionary(property => property.Name, property => SecurityScheme(property.Value), StringComparer.Ordinal);
        }
        return Element(result);
    }

    private static object[] Security(JsonElement owner) => Repeated(owner, "security").Select(requirement =>
    {
        A2AJson.RequireObject(requirement);
        if (!requirement.TryGetProperty("schemes", out var field) || field.ValueKind == JsonValueKind.Null) return new Dictionary<string, JsonElement[]>();
        var schemes = A2AJson.RequiredObject(requirement, "schemes");
        return (object)schemes.EnumerateObject().ToDictionary(property => property.Name, property => Repeated(property.Value, "list"), StringComparer.Ordinal);
    }).ToArray();

    private static object SecurityScheme(JsonElement value)
    {
        A2AJson.RequireObject(value);
        var arms = new[] { "apiKeySecurityScheme", "httpAuthSecurityScheme", "oauth2SecurityScheme", "openIdConnectSecurityScheme", "mtlsSecurityScheme" }.Where(name => value.TryGetProperty(name, out _)).ToArray();
        if (arms.Length != 1) throw new A2AProtocolException("An A2A security scheme requires exactly one supported scheme.");
        var scheme = A2AJson.RequiredObject(value, arms[0]);
        var result = Copy(scheme, "location");
        result["type"] = arms[0] switch
        {
            "apiKeySecurityScheme" => "apiKey",
            "httpAuthSecurityScheme" => "http",
            "oauth2SecurityScheme" => "oauth2",
            "openIdConnectSecurityScheme" => "openIdConnect",
            _ => "mutualTLS"
        };
        if (arms[0] == "apiKeySecurityScheme") result["in"] = A2AJson.RequiredString(scheme, "location");
        return result;
    }

    private static JsonElement[] Repeated(JsonElement value, string field)
    {
        A2AJson.RequireObject(value);
        return !value.TryGetProperty(field, out var property) || property.ValueKind == JsonValueKind.Null ? System.Array.Empty<JsonElement>() : Array(value, field);
    }

    private static string EnumValue(JsonElement value, string field, params string[] names)
    {
        if (!value.TryGetProperty(field, out var property)) return names[0];
        if (property.ValueKind == JsonValueKind.String && names.Contains(property.GetString())) return property.GetString()!;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var index) && index >= 0 && index < names.Length) return names[index];
        throw new A2AProtocolException("The A2A enum '" + field + "' is unsupported.");
    }

    private static JsonElement[] Array(JsonElement value, string field)
    {
        A2AJson.RequireObject(value);
        if (!value.TryGetProperty(field, out var result) || result.ValueKind != JsonValueKind.Array) throw new A2AProtocolException("The A2A field '" + field + "' requires an array.");
        return result.EnumerateArray().ToArray();
    }

    private static string Text(JsonElement value, string field) => A2AJson.OptionalString(value, field) ?? throw new A2AProtocolException("The A2A field '" + field + "' requires a string.");
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static Dictionary<string, object?> Copy(JsonElement value, params string[] excluded)
    {
        A2AJson.RequireObject(value);
        return value.EnumerateObject().Where(property => !excluded.Contains(property.Name)).ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
    }
}
