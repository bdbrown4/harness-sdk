namespace Strands.Agents.Bidi;

/// <summary>Explicit composition for one persistent bidirectional conversation.</summary>
public sealed record BidiAgentOptions
{
    /// <summary>The native duplex model whose connection this conversation starts and stops.</summary>
    public required IBidiModel Model { get; init; }
    /// <summary>A real core agent supplying initialized tools, schema validation, tool hooks, and tool context.</summary>
    public required Agent ToolAgent { get; init; }
    /// <summary>Optional display name; null or empty selects Strands Agents.</summary>
    public string? Name { get; init; }
    /// <summary>Optional description of this bidirectional agent.</summary>
    public string? Description { get; init; }
    /// <summary>Optional initial history, copied on construction.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];
    /// <summary>Optional application-owned lifecycle registrations; null creates a fresh registry.</summary>
    public BidiHookRegistry? Hooks { get; init; }
    /// <summary>Initial application JSON state, copied into this conversation's independent state store.</summary>
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement>? State { get; init; }
    /// <summary>Optional borrowed native session persistence. It does not store live sockets or active tool bodies.</summary>
    public ISessionStore? SessionStore { get; init; }
    /// <summary>Optional borrowed latest/immutable snapshot manager; mutually exclusive with SessionStore.</summary>
    public BidiSnapshotSessionManager? SessionManager { get; init; }
    /// <summary>The agent namespace within a snapshot session; defaults to default.</summary>
    public string AgentId { get; init; } = "default";
    /// <summary>An opaque session key; null generates eight hexadecimal characters.</summary>
    public string? SessionId { get; init; }
    /// <summary>SessionStore persistence points; defaults to each accepted message mutation plus stop/restart. SessionManager supplies its own policy.</summary>
    public BidiSessionSaveStrategy SessionSaveStrategy { get; init; } = BidiSessionSaveStrategy.Message;
    /// <summary>The system prompt; null uses the supplied core agent's current prompt.</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>Optional structured text/cache-point system content; mutually exclusive with a nonnull SystemPrompt.</summary>
    public IReadOnlyList<ContentBlock>? SystemPromptContent { get; init; }
    /// <summary>Whether disposal also disposes the supplied model. Defaults to false.</summary>
    public bool OwnsModel { get; init; }
    /// <summary>Whether disposal also disposes the supplied core agent. Defaults to false.</summary>
    public bool OwnsToolAgent { get; init; }
}
