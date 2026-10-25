using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>A detached native A2A message, task, artifact or status update.</summary>
public sealed record A2AStreamUpdateEvent : IAgentStreamEvent
{
    /// <summary>Copies a protocol result without dropping extension fields.</summary>
    public A2AStreamUpdateEvent(JsonElement value) => Event = value.Clone();
    /// <summary>The complete protocol result with original wire names.</summary>
    public JsonElement Event { get; }
}

/// <summary>The terminal local projection and authoritative remote lifecycle identifiers.</summary>
public sealed record A2AResultEvent : IAgentStreamEvent
{
    /// <summary>Captures the local result and detached final protocol update.</summary>
    public A2AResultEvent(AgentResult result, JsonElement finalEvent, string? taskState = null, string? taskId = null, string? contextId = null)
    { Guard.NotNull(result, nameof(result)); Result = result; FinalEvent = finalEvent.Clone(); TaskState = taskState; TaskId = taskId; ContextId = contextId; }
    /// <summary>The local typed content projection. Remote execution does not report native token usage.</summary>
    public AgentResult Result { get; }
    /// <summary>The detached final protocol update, retaining extensions and remote error details.</summary>
    public JsonElement FinalEvent { get; }
    /// <summary>The final remote task state, or null for a Message-only response.</summary>
    public string? TaskState { get; }
    /// <summary>The remote task identifier, when a task was created.</summary>
    public string? TaskId { get; }
    /// <summary>The remote conversation identifier, when supplied.</summary>
    public string? ContextId { get; }
}
