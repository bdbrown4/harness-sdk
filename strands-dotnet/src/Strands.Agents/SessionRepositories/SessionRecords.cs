using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.SessionRepositories;

/// <summary>The kind of session represented by the indexed repository contract.</summary>
public enum SessionRecordType
{
    /// <summary>A session containing one or more agents.</summary>
    Agent
}

/// <summary>Metadata for an indexed session, independent of whole-conversation snapshots.</summary>
public sealed record SessionRecord
{
    /// <summary>Creates immutable session metadata.</summary>
    public SessionRecord(string sessionId, SessionRecordType sessionType = SessionRecordType.Agent, DateTimeOffset? createdAt = null, DateTimeOffset? updatedAt = null)
    {
        Guard.NotNullOrWhiteSpace(sessionId, nameof(sessionId));
        if (!Enum.IsDefined(typeof(SessionRecordType), sessionType)) throw new ArgumentOutOfRangeException(nameof(sessionType));
        SessionId = sessionId; SessionType = sessionType; CreatedAt = createdAt ?? DateTimeOffset.UtcNow; UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
    }
    [JsonConstructor]
    private SessionRecord(string sessionId, SessionRecordType sessionType, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        : this(sessionId, sessionType, (DateTimeOffset?)createdAt, updatedAt) { }
    /// <summary>The opaque session identity.</summary>
    public string SessionId { get; }
    /// <summary>The session kind.</summary>
    public SessionRecordType SessionType { get; }
    /// <summary>The original creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>The supplied update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>Detached application, conversation-manager, and SDK-private state for an indexed agent.</summary>
public sealed record SessionAgentRecord
{
    /// <summary>Creates metadata by copying JSON objects before any storage operation.</summary>
    public SessionAgentRecord(string agentId, JsonElement state, JsonElement conversationManagerState, JsonElement? internalState = null, DateTimeOffset? createdAt = null, DateTimeOffset? updatedAt = null)
    {
        Guard.NotNullOrWhiteSpace(agentId, nameof(agentId));
        AgentId = agentId; State = CopyObject(state); ConversationManagerState = CopyObject(conversationManagerState);
        InternalState = internalState is { } value ? CopyObject(value) : JsonSerializer.SerializeToElement(new { });
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow; UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
    }
    [JsonConstructor]
    private SessionAgentRecord(string agentId, JsonElement state, JsonElement conversationManagerState, JsonElement internalState, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        : this(agentId, state, conversationManagerState, (JsonElement?)internalState, createdAt, updatedAt) { }
    /// <summary>The opaque agent identity within its session.</summary>
    public string AgentId { get; }
    /// <summary>Detached application state.</summary>
    public JsonElement State { get; }
    /// <summary>Detached conversation-manager state.</summary>
    public JsonElement ConversationManagerState { get; }
    /// <summary>Detached SDK-managed state; no live agent is restored by this repository.</summary>
    public JsonElement InternalState { get; }
    /// <summary>The original creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>The supplied update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; }
    internal static JsonElement CopyObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Indexed agent and multi-agent state must be JSON objects.");
        return value.Clone();
    }
}

/// <summary>An individually indexed original message and its optional redacted replacement.</summary>
public sealed record SessionMessageRecord
{
    /// <summary>Copies a native message and optional redaction, preserving their tracking identities and rich content.</summary>
    public SessionMessageRecord(Message message, long messageId, Message? redactMessage = null, DateTimeOffset? createdAt = null, DateTimeOffset? updatedAt = null)
    {
        Guard.NotNull(message, nameof(message));
        Message = Copy(message); MessageId = messageId; RedactMessage = redactMessage is null ? null : Copy(redactMessage);
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow; UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
    }
    [JsonConstructor]
    private SessionMessageRecord(Message message, long messageId, Message? redactMessage, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        : this(message, messageId, redactMessage, (DateTimeOffset?)createdAt, updatedAt) { }
    /// <summary>The original native message, retained after redaction.</summary>
    public Message Message { get; }
    /// <summary>The numeric row identity; sparse and signed Int64 values are supported.</summary>
    public long MessageId { get; }
    /// <summary>The optional effective redaction.</summary>
    public Message? RedactMessage { get; }
    /// <summary>The original creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>The supplied update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; }
    /// <summary>Returns the redacted message when present, otherwise the original.</summary>
    public Message ToMessage() => RedactMessage ?? Message;
    internal static Message Copy(Message message) => new(message.Role, message.Content, message.TrackingId, message.Metadata);
}
