using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>How remote A2A parts map to native content.</summary>
public enum A2AContentMode
{
    /// <summary>Preserve supported text, data, byte files and resource links; reject unsupported parts explicitly.</summary>
    Rich,
    /// <summary>Send and return only text, matching the pinned TypeScript remote-agent projection.</summary>
    TextOnly
}

/// <summary>Compatibility policy for an A2A task requiring input or authentication.</summary>
public enum A2ATaskStateMapping
{
    /// <summary>Map input-required and auth-required to interrupt, matching Python.</summary>
    Protocol,
    /// <summary>Return end_turn for every remote state, matching the pinned TypeScript adapter.</summary>
    AlwaysEndTurn
}

/// <summary>Remote-agent projection settings. The transport and remote conversation remain caller-owned.</summary>
public sealed record A2AAgentOptions
{
    /// <summary>An explicit identity, otherwise the discovery URL.</summary>
    public string? Id { get; init; }
    /// <summary>An explicit name, otherwise populated from the discovered card.</summary>
    public string? Name { get; init; }
    /// <summary>An explicit description, otherwise populated from the discovered card.</summary>
    public string? Description { get; init; }
    /// <summary>The content projection, defaulting to rich native content.</summary>
    public A2AContentMode ContentMode { get; init; }
    /// <summary>The stop-reason projection, defaulting to protocol-aware Python behavior.</summary>
    public A2ATaskStateMapping TaskStateMapping { get; init; }
    /// <summary>Use message/stream when the card supports streaming, otherwise message/send.</summary>
    public bool Streaming { get; init; } = true;
    /// <summary>A conservative retained-content budget, charging UTF-8 parts, identifiers and per-part/chunk bookkeeping. Defaults to 16 MiB; raw stream events have a separate 4 MiB frame limit.</summary>
    public int MaxResultBytes { get; init; } = 16 * 1024 * 1024;
}

/// <summary>Explicit remote conversation identifiers and protocol extensions for one call.</summary>
public sealed record A2AInvocationOptions
{
    /// <summary>A remote context to continue. Knowing an identifier does not authenticate a caller.</summary>
    public string? ContextId { get; init; }
    /// <summary>A remote task to continue, including one waiting for more input.</summary>
    public string? TaskId { get; init; }
    /// <summary>An explicit message identifier, otherwise a new opaque identifier.</summary>
    public string? MessageId { get; init; }
    /// <summary>Protocol MessageSendConfiguration in its original wire casing.</summary>
    public JsonElement? Configuration { get; init; }
    /// <summary>Protocol request metadata explicitly authorized for transmission.</summary>
    public JsonElement? Metadata { get; init; }
    /// <summary>Caller-owned context retained locally and returned by reference. It is never transmitted.</summary>
    public IReadOnlyDictionary<string, object?> InvocationState { get; init; } = new Dictionary<string, object?>();
}
