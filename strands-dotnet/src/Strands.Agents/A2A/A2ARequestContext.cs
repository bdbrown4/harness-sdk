using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>The explicit native boundary for one A2A request. Protocol context remains separate from application invocation state.</summary>
public sealed class A2ARequestContext
{
    private readonly JsonElement? _currentTask;
    /// <summary>Creates a detached request bound to the server-selected task and conversation.</summary>
    public A2ARequestContext(string taskId, string contextId, JsonElement message, IReadOnlyDictionary<string, object?>? invocationState = null, JsonElement? configuration = null, JsonElement? metadata = null)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId)); Guard.NotNullOrWhiteSpace(contextId, nameof(contextId));
        A2AJson.ValidateUniqueProperties(message); A2AJson.RequireObject(message);
        if (A2AJson.RequiredString(message, "kind") != "message" || A2AJson.RequiredString(message, "role") != "user") throw new A2AProtocolException("A2A input must be a user Message.");
        _ = A2AJson.RequiredString(message, "messageId");
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0) throw new A2AProtocolException("A2A input requires nonempty parts.");
        if (A2AJson.OptionalString(message, "taskId") is { } suppliedTask && suppliedTask != taskId || A2AJson.OptionalString(message, "contextId") is { } suppliedContext && suppliedContext != contextId)
            throw new A2AProtocolException("A2A message and request-context identifiers must match.");
        foreach (var value in new[] { configuration, metadata }) if (value is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("A2A configuration and metadata must be objects.");
        TaskId = taskId; ContextId = contextId; Message = message.Clone(); InvocationState = invocationState ?? new Dictionary<string, object?>();
        Configuration = configuration?.Clone(); Metadata = metadata?.Clone();
    }
    /// <summary>The server-selected task identifier.</summary>
    public string TaskId { get; }
    /// <summary>The server-selected conversation identifier.</summary>
    public string ContextId { get; }
    /// <summary>The detached user Message with original protocol fields.</summary>
    public JsonElement Message { get; }
    /// <summary>Caller-owned application context, forwarded without SDK-owned entries.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; }
    /// <summary>Detached MessageSendConfiguration, when supplied.</summary>
    public JsonElement? Configuration { get; }
    /// <summary>Detached explicit protocol request metadata.</summary>
    public JsonElement? Metadata { get; }
    /// <summary>A trusted application or server-supplied persisted task, detached and correlated to this request. Client message JSON cannot set this property through the HTTP handler.</summary>
    public JsonElement? CurrentTask
    {
        get => _currentTask;
        init
        {
            if (value is not { } task) return;
            if (A2ATaskRecord.Validate(task) != TaskId || task.GetProperty("contextId").GetString() != ContextId)
                throw new ArgumentException("The current task must retain the request's task and context identifiers.", nameof(value));
            _currentTask = task.Clone();
        }
    }
}

/// <summary>An awaited sink receiving detached native A2A task and artifact updates.</summary>
public interface IA2AEventSink
{
    /// <summary>Publishes one protocol update before execution advances.</summary>
    ValueTask PublishAsync(JsonElement update, CancellationToken cancellationToken = default);
}
