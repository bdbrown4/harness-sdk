using System.Collections.ObjectModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Strands.Agents.Mcp;

/// <summary>A task's status as reported by its MCP server.</summary>
public enum McpTaskStatus
{
    /// <summary>The server is processing the request.</summary>
    Working,
    /// <summary>The server requires client responses before proceeding.</summary>
    InputRequired,
    /// <summary>The protocol request completed, possibly with an isError tool result.</summary>
    Completed,
    /// <summary>The underlying request failed with a JSON-RPC error.</summary>
    Failed,
    /// <summary>The server reports that the task was cancelled.</summary>
    Cancelled
}

/// <summary>A detached, validated SEP-2663 task handle or complete task state.</summary>
public sealed class McpTask
{
    private readonly JsonElement _definition;

    private McpTask(JsonElement definition) => _definition = definition.Clone();

    /// <summary>The complete detached protocol state, retaining vendor metadata.</summary>
    public JsonElement Definition => _definition.Clone();
    /// <summary>The server-issued task identifier.</summary>
    public string TaskId => _definition.GetProperty("taskId").GetString()!;
    /// <summary>The current server status.</summary>
    public McpTaskStatus Status => ReadStatus(_definition);
    /// <summary>Optional server-provided status context.</summary>
    public string? StatusMessage => _definition.TryGetProperty("statusMessage", out var value) ? value.GetString() : null;
    /// <summary>The task creation timestamp, including its UTC offset.</summary>
    public DateTimeOffset CreatedAt => _definition.GetProperty("createdAt").GetDateTimeOffset();
    /// <summary>The latest update timestamp, including its UTC offset.</summary>
    public DateTimeOffset LastUpdatedAt => _definition.GetProperty("lastUpdatedAt").GetDateTimeOffset();
    /// <summary>The server's retention period, or null for unlimited retention.</summary>
    public TimeSpan? TimeToLive => _definition.GetProperty("ttlMs").ValueKind == JsonValueKind.Null ? null : TimeSpan.FromMilliseconds(_definition.GetProperty("ttlMs").GetInt64());
    /// <summary>The server's suggested polling cadence, or null when absent.</summary>
    public TimeSpan? PollInterval => _definition.TryGetProperty("pollIntervalMs", out var value) ? TimeSpan.FromMilliseconds(value.GetInt64()) : null;
    /// <summary>The completed tool result. Every read returns a detached protocol object.</summary>
    public CallToolResult? Result => _definition.TryGetProperty("result", out var value) ? value.Deserialize<CallToolResult>() : null;
    /// <summary>The failed task's detached JSON-RPC error object, or null for other statuses.</summary>
    public JsonElement? Error => _definition.TryGetProperty("error", out var value) ? value.Clone() : null;
    /// <summary>The outstanding server input requests. Every read returns detached protocol objects.</summary>
    public IReadOnlyDictionary<string, InputRequest>? InputRequests => _definition.TryGetProperty("inputRequests", out var value)
        ? new ReadOnlyDictionary<string, InputRequest>(value.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.Deserialize<InputRequest>()!, StringComparer.Ordinal)) : null;

    /// <summary>Validates and detaches a complete tasks/get payload.</summary>
    /// <param name="definition">A SEP-2663 resultType=complete task state.</param>
    /// <returns>The immutable validated state.</returns>
    /// <exception cref="InvalidDataException">The task envelope or status payload is malformed.</exception>
    public static McpTask Parse(JsonElement definition) => Parse(definition, "complete");

    internal static McpTask ParseNotification(JsonElement definition) => Parse(definition, null);

    internal static McpTask Parse(JsonElement definition, string? resultType)
    {
        try
        {
            RequireObject(definition, "task");
            if (resultType is not null && RequiredString(definition, "resultType") != resultType) throw Invalid("Unexpected task resultType.");
            if (RequiredString(definition, "taskId").Length == 0) throw Invalid("A taskId cannot be empty.");
            var status = ReadStatus(definition);
            var created = Timestamp(definition, "createdAt");
            var updated = Timestamp(definition, "lastUpdatedAt");
            if (updated < created) throw Invalid("Task lastUpdatedAt precedes createdAt.");
            if (!definition.TryGetProperty("ttlMs", out var ttl)) throw Invalid("A task requires ttlMs, including null for unlimited retention.");
            if (ttl.ValueKind != JsonValueKind.Null) Duration(ttl, "ttlMs");
            if (definition.TryGetProperty("pollIntervalMs", out var poll)) Duration(poll, "pollIntervalMs");
            if (definition.TryGetProperty("statusMessage", out var message) && message.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw Invalid("Task statusMessage must be a string or null.");
            if (definition.TryGetProperty("_meta", out var meta)) RequireObject(meta, "task metadata");
            if (resultType is null or "complete") ValidatePayload(definition, status);
            return new(definition);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
        { throw new InvalidDataException("MCP returned a malformed SEP-2663 task state.", error); }
    }

    internal static void ValidateToolResult(JsonElement result)
    {
        try
        {
            RequireObject(result, "tool result");
            if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw Invalid("A task tool result requires a content array.");
            McpTaskContent.Validate(content, sampling: false);
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid("A task tool result isError must be a boolean.");
            _ = result.Deserialize<CallToolResult>() ?? throw Invalid("A task tool result cannot be null.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("MCP returned a malformed task tool result.", error); }
    }

    private static void ValidatePayload(JsonElement definition, McpTaskStatus status)
    {
        var expected = status switch
        {
            McpTaskStatus.Completed => "result",
            McpTaskStatus.Failed => "error",
            McpTaskStatus.InputRequired => "inputRequests",
            _ => null
        };
        foreach (var field in new[] { "result", "error", "inputRequests" })
            if (definition.TryGetProperty(field, out _) && field != expected) throw Invalid("Unexpected task status payload: " + field + ".");
        if (expected is null) return;
        if (!definition.TryGetProperty(expected, out var payload)) throw Invalid("Missing task status payload: " + expected + ".");
        RequireObject(payload, expected);
        if (status == McpTaskStatus.Completed) ValidateToolResult(payload);
        else if (status == McpTaskStatus.Failed)
        {
            if (!payload.TryGetProperty("code", out var code) || !code.TryGetInt32(out _)) throw Invalid("A task error requires an integer code.");
            _ = RequiredString(payload, "message");
        }
        else foreach (var property in payload.EnumerateObject())
        {
            RequireObject(property.Value, "input request");
            var method = RequiredString(property.Value, "method");
            if (method is not ("sampling/createMessage" or "roots/list" or "elicitation/create")) throw Invalid("Unsupported task input method: " + method + ".");
            McpTaskInputs.ValidateRequest(property.Value);
        }
    }

    private static McpTaskStatus ReadStatus(JsonElement definition) => RequiredString(definition, "status") switch
    {
        "working" => McpTaskStatus.Working,
        "input_required" => McpTaskStatus.InputRequired,
        "completed" => McpTaskStatus.Completed,
        "failed" => McpTaskStatus.Failed,
        "cancelled" => McpTaskStatus.Cancelled,
        _ => throw Invalid("Unknown MCP task status.")
    };

    private static DateTimeOffset Timestamp(JsonElement definition, string field)
    {
        var raw = RequiredString(definition, field);
        var offset = raw.EndsWith("Z", StringComparison.Ordinal) || raw.Length >= 6 && raw[raw.Length - 3] == ':' && raw[raw.Length - 6] is '+' or '-';
        if (!offset || !definition.GetProperty(field).TryGetDateTimeOffset(out var value)) throw Invalid("Task timestamps require ISO 8601 values with UTC offsets.");
        return value;
    }

    private static void Duration(JsonElement value, string field)
    {
        if (!value.TryGetInt64(out var milliseconds) || milliseconds < 0 || milliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            throw Invalid("Task " + field + " must be a nonnegative integer duration.");
    }

    private static string RequiredString(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var text) || text.ValueKind != JsonValueKind.String) throw Invalid("Task " + field + " must be a string.");
        return text.GetString()!;
    }
    private static void RequireObject(JsonElement value, string field)
    { if (value.ValueKind != JsonValueKind.Object) throw Invalid("MCP " + field + " must be an object."); }
    private static InvalidDataException Invalid(string message) => new(message);
}
