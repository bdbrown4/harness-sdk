using System.Text.Json;

namespace Strands.Agents.SessionRepositories;

/// <summary>Persists independently indexed session, agent, and message records. This does not restore a live agent.</summary>
public interface ISessionRepository
{
    /// <summary>Creates session metadata; an existing session is an error.</summary>
    ValueTask<SessionRecord> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default);
    /// <summary>Reads metadata, returning null when absent.</summary>
    ValueTask<SessionRecord?> ReadSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Creates or replaces agent metadata, matching the source create/upsert contract.</summary>
    ValueTask CreateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default);
    /// <summary>Reads metadata, returning null when absent.</summary>
    ValueTask<SessionAgentRecord?> ReadAgentAsync(string sessionId, string agentId, CancellationToken cancellationToken = default);
    /// <summary>Updates existing metadata, preserving its creation timestamp. Missing metadata is an error.</summary>
    ValueTask UpdateAgentAsync(string sessionId, SessionAgentRecord agent, CancellationToken cancellationToken = default);
    /// <summary>Creates or replaces one indexed message.</summary>
    ValueTask CreateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default);
    /// <summary>Reads an indexed message, returning null when absent.</summary>
    ValueTask<SessionMessageRecord?> ReadMessageAsync(string sessionId, string agentId, long messageId, CancellationToken cancellationToken = default);
    /// <summary>Updates an existing message, preserving its creation timestamp. A missing record is an error.</summary>
    ValueTask UpdateMessageAsync(string sessionId, string agentId, SessionMessageRecord message, CancellationToken cancellationToken = default);
    /// <summary>Orders rows by numeric identity, then applies nonnegative row offset and optional limit exactly once.</summary>
    ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string sessionId, string agentId, int? limit = null, int offset = 0, CancellationToken cancellationToken = default);
}

/// <summary>Optional detached, versioned multi-agent state records keyed by explicit identities.</summary>
public interface IMultiAgentSessionRepository
{
    /// <summary>Creates or replaces a detached JSON object; the repository adds its own versioned envelope.</summary>
    ValueTask CreateMultiAgentAsync(string sessionId, string multiAgentId, JsonElement state, CancellationToken cancellationToken = default);
    /// <summary>Reads the detached JSON object, returning null when absent.</summary>
    ValueTask<JsonElement?> ReadMultiAgentAsync(string sessionId, string multiAgentId, CancellationToken cancellationToken = default);
    /// <summary>Updates existing multi-agent state. Missing state is an error.</summary>
    ValueTask UpdateMultiAgentAsync(string sessionId, string multiAgentId, JsonElement state, CancellationToken cancellationToken = default);
}

/// <summary>Controls optional byte bounds without imposing an arbitrary default media-size limit.</summary>
public sealed record SessionRepositoryOptions
{
    /// <summary>An optional positive encoded-record byte limit; null is unbounded.</summary>
    public int? MaxRecordBytes { get; init; }
    /// <summary>A storage namespace. Defaults to indexed-sessions; IDs are hashed independently beneath it.</summary>
    public string Prefix { get; init; } = "indexed-sessions";
}
