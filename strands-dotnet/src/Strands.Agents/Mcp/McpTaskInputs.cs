using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Strands.Agents.Mcp;

internal static class McpTaskInputs
{
    internal static void ValidateRequest(JsonElement value)
    {
        var request = value.Deserialize<InputRequest>() ?? throw Invalid("Task input request cannot be null.");
        if (request.Method == "roots/list")
        {
            if (request.Params is { ValueKind: not JsonValueKind.Object }) throw Invalid("Task roots/list params must be an object.");
            return;
        }
        if (request.Params is not { ValueKind: JsonValueKind.Object } parameters) throw Invalid("Task input request requires object params.");
        if (request.Method == "elicitation/create")
        {
            RequireString(parameters, "message");
            _ = parameters.Deserialize<ElicitRequestParams>() ?? throw Invalid("Invalid task elicitation params.");
            var mode = parameters.TryGetProperty("mode", out var valueMode) ? valueMode.GetString() : "form";
            if (mode == "form")
            {
                if (!parameters.TryGetProperty("requestedSchema", out var schema) || schema.ValueKind != JsonValueKind.Object ||
                    !schema.TryGetProperty("type", out var type) || type.GetString() != "object")
                    throw Invalid("Task form elicitation requires an object requestedSchema.");
            }
            else if (mode == "url")
            {
                var url = RequireString(parameters, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw Invalid("Task URL elicitation requires an absolute URL.");
            }
            else throw Invalid("Unknown task elicitation mode.");
            return;
        }
        if (request.Method != "sampling/createMessage") throw Invalid("Unsupported task input request method.");
#pragma warning disable MCP9005 // SEP-2663 can embed the sampling operation exposed by the official compatibility handler.
        _ = parameters.Deserialize<CreateMessageRequestParams>() ?? throw Invalid("Invalid task sampling params.");
#pragma warning restore MCP9005
        if (!parameters.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
            !parameters.TryGetProperty("maxTokens", out var maximum) || !maximum.TryGetInt32(out var count) || count <= 0)
            throw Invalid("Task sampling requires messages and positive maxTokens.");
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || RequireString(message, "role") is not ("user" or "assistant") ||
                !message.TryGetProperty("content", out var content)) throw Invalid("Task sampling messages require a user or assistant role and content.");
            McpTaskContent.Validate(content, sampling: true);
        }
    }

    internal static JsonElement ValidateResponses(IReadOnlyDictionary<string, InputResponse> responses, IReadOnlyDictionary<string, InputRequest>? requests = null)
    {
        var detached = JsonSerializer.SerializeToElement(responses);
        foreach (var pair in detached.EnumerateObject())
        {
            if (requests is not null && !requests.ContainsKey(pair.Name)) throw Invalid("Task input handler returned an unrelated response key.");
            ValidateResponse(pair.Value, requests is not null ? requests[pair.Name].Method : null);
        }
        return detached;
    }

    private static void ValidateResponse(JsonElement response, string? method)
    {
        if (response.ValueKind != JsonValueKind.Object) throw Invalid("Task input response must be an object.");
        method ??= response.TryGetProperty("action", out _) ? "elicitation/create" : response.TryGetProperty("roots", out _) ? "roots/list" : "sampling/createMessage";
        if (method == "elicitation/create")
        {
            var action = RequireString(response, "action");
            if (action is not ("accept" or "decline" or "cancel")) throw Invalid("Task elicitation handler returned an invalid action.");
            if (response.TryGetProperty("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Object) throw Invalid("Task elicitation response content must be an object.");
                foreach (var value in content.EnumerateObject().Select(property => property.Value))
                    if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number) &&
                        !(value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)))
                        throw Invalid("Task elicitation response content contains an unsupported value.");
            }
        }
        else if (method == "roots/list")
        {
            if (!response.TryGetProperty("roots", out var roots) || roots.ValueKind != JsonValueKind.Array) throw Invalid("Task roots response requires a roots array.");
            foreach (var root in roots.EnumerateArray())
                if (root.ValueKind != JsonValueKind.Object || !Uri.TryCreate(RequireString(root, "uri"), UriKind.Absolute, out _)) throw Invalid("Task root requires an absolute URI.");
        }
        else
        {
            var role = RequireString(response, "role");
            RequireString(response, "model");
            if (role is not ("user" or "assistant") || !response.TryGetProperty("content", out var content) || content.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw Invalid("Task sampling handler returned an invalid response.");
            McpTaskContent.Validate(content, sampling: true);
            var input = response.Deserialize<InputResponse>() ?? throw Invalid("Task sampling response cannot be null.");
#pragma warning disable MCP9005 // Preserve sampling input compatibility with the pinned cross-language task contracts.
            _ = input.Deserialize(InputResponse.CreateMessageResultJsonTypeInfo);
#pragma warning restore MCP9005
        }
    }

    private static string RequireString(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var result) || result.ValueKind != JsonValueKind.String) throw Invalid("Task input " + field + " must be a string.");
        return result.GetString()!;
    }
    private static InvalidDataException Invalid(string message) => new(message);
}
