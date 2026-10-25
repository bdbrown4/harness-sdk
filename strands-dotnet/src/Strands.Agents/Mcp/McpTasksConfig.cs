using System.Text.Json.Serialization;

namespace Strands.Agents.Mcp;

/// <summary>The experimental MCP task wire contract selected for a connection.</summary>
public enum McpTasksProtocolProfile
{
    /// <summary>The finalized SEP-2663 extension over the 2026-07-28 protocol revision.</summary>
    Finalized20260728,
    /// <summary>The legacy task-augmented tools flow over MCP 2025-11-25. Explicit lifecycle methods remain finalized-extension operations.</summary>
    Legacy20251125,
    /// <summary>Selects finalized or legacy task execution from the negotiated protocol, server capabilities, and applicable tool metadata.</summary>
    Negotiated
}

/// <summary>How automatic finalized MCP task calls observe task state.</summary>
public enum McpTaskObservationMode
{
    /// <summary>Observes task state through cadence-based polling.</summary>
    Polling = 0,
    /// <summary>Accepts validated finalized task notifications while retaining polling as a fallback.</summary>
    NotificationsAndPolling = 1
}

/// <summary>Bounds task-augmented tool calls independently of individual protocol requests.</summary>
public sealed record McpTasksConfig
{
    /// <summary>The experimental task wire contract. Defaults to negotiated selection; unsupported profiles fail before connecting.</summary>
    public McpTasksProtocolProfile ProtocolProfile { get; init; } = McpTasksProtocolProfile.Negotiated;
    /// <summary>The observation mode for automatic finalized task calls. Defaults to polling; legacy task execution retains its existing polling behavior.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public McpTaskObservationMode ObservationMode { get; init; } = McpTaskObservationMode.Polling;
    /// <summary>The complete automatic operation budget, including polling and input callbacks. Defaults to five minutes.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>The individual request budget, defaulting to one minute. Progress resets automatic polling requests without extending PollTimeout; explicit lifecycle operations retain a fixed overall RequestTimeout budget.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>The fallback polling interval when a server omits its cadence. Defaults to one second; server cadence is bounded below by ten milliseconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>An explicit legacy server retention request in positive whole milliseconds. Allowed for negotiated and legacy profiles, sent only for legacy execution; null omits wire TTL, matching TypeScript.</summary>
    public TimeSpan? LegacyTimeToLive { get; init; }
}

/// <summary>Native connection features layered on the official MCP client options.</summary>
public sealed record McpClientSettings
{
    /// <summary>Transfers the supplied transport factory to the client. Its asynchronous or synchronous disposal runs after session cleanup, including failed connection attempts. Default false leaves caller-supplied factories borrowed; HTTP convenience methods still borrow the supplied HttpClient.</summary>
    public bool OwnsTransport { get; init; }
    /// <summary>Enables experimental task operations and automatic completion of task-augmented tool calls. Null preserves ordinary tool calls.</summary>
    public McpTasksConfig? TasksConfig { get; init; }
    /// <summary>Default tool-call metadata, detached before connection. Must be a JSON object; null omits the default. A non-null per-call RequestOptions.Meta replaces it, including an empty object.</summary>
    /// <remarks>Applies to CallToolAsync and adapted tool calls, including automatic task completion. Explicit SubmitToolAsync does not inherit this default.</remarks>
    public System.Text.Json.JsonElement? DefaultToolCallMetadata { get; init; }
}
