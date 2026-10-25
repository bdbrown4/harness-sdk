using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>Explicit discovery metadata and portable HTTP task-host limits.</summary>
public sealed record A2AServerOptions
{
    /// <summary>The absolute public JSON-RPC endpoint advertised in the agent card.</summary>
    public required Uri Url { get; init; }
    /// <summary>The public agent name, without constructing a representative agent.</summary>
    public required string Name { get; init; }
    /// <summary>The public description. An empty value uses Name.</summary>
    public string Description { get; init; } = "";
    /// <summary>The application version advertised in discovery.</summary>
    public string Version { get; init; } = "0.0.1";
    /// <summary>The discovery default format. Native uses A2A 0.3 and configured MIME modes; language formats select their pinned protocol/text defaults without creating a representative agent.</summary>
    public A2ACardFormat CardFormat { get; init; }
    /// <summary>Explicit skill declarations, copied on construction. The default empty list matches TypeScript; use A2AAgentSkill.FromTool for Python-compatible tool metadata.</summary>
    public IReadOnlyList<A2AAgentSkill> Skills { get; init; } = [];
    /// <summary>Optional protocol card fields such as provider, security, documentation and extensions. Runtime-bound identity, modes, skills and capabilities remain authoritative.</summary>
    public JsonElement? AdditionalCardFields { get; init; }
    /// <summary>The maximum serialized UTF-8 bytes in a public or authenticated card. Defaults to 1 MiB.</summary>
    public int MaxCardBytes { get; init; } = 1024 * 1024;
    /// <summary>An optional detached authenticated extended card. Requires AuthorizeExtendedCard; the handler does not infer authentication from discovery metadata.</summary>
    public JsonElement? AuthenticatedExtendedCard { get; init; }
    /// <summary>An explicit host authorization check for each extended-card request. Returning false denies access with HTTP 403; request and callback resources remain borrowed.</summary>
    public Func<HttpRequestMessage, CancellationToken, ValueTask<bool>>? AuthorizeExtendedCard { get; init; }
    /// <summary>The accepted input MIME types, copied on construction. Defaults to text/plain and application/json; explicitly list file MIME types to enable them.</summary>
    public IReadOnlyList<string> InputModes { get; init; } = ["text/plain", "application/json"];
    /// <summary>The supported output MIME types, copied on construction and enforced during execution. Defaults to text/plain and application/json.</summary>
    public IReadOnlyList<string> OutputModes { get; init; } = ["text/plain", "application/json"];
    /// <summary>Whether disposal transfers to the supplied executor. Defaults to false.</summary>
    public bool OwnsExecutor { get; init; }
    /// <summary>An optional borrowed task store. Without one, tasks remain in the server's bounded memory cache. Task persistence alone does not restore native agent executions.</summary>
    public IA2ATaskStore? TaskStore { get; init; }
    /// <summary>Optional authorized push delivery and configuration management. Disabled by default.</summary>
    public A2APushNotificationOptions? PushNotifications { get; init; }
    /// <summary>The maximum retained tasks. Terminal tasks may be evicted; active and paused tasks are retained.</summary>
    public int MaxTasks { get; init; } = 1000;
    /// <summary>The maximum UTF-8 JSON request body size. Defaults to 1 MiB.</summary>
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>The maximum retained UTF-8 task snapshot size. Defaults to 16 MiB.</summary>
    public int MaxTaskBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>The maximum buffered events for one task execution.</summary>
    public int MaxReplayEvents { get; init; } = 1024;
    /// <summary>The maximum retained UTF-8 event bytes for one execution.</summary>
    public int MaxReplayBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>The maximum simultaneous listeners for one execution.</summary>
    public int MaxSubscribers { get; init; } = 32;
}
