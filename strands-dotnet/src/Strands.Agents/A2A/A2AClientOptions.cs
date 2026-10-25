namespace Strands.Agents.A2A;

/// <summary>Portable A2A v0.3 HTTP transport settings. Authentication belongs to the caller's HTTP handler.</summary>
/// <param name="Url">The absolute HTTP or HTTPS discovery base URL.</param>
public sealed record A2AClientOptions(Uri Url)
{
    /// <summary>The card location resolved against Url. Defaults to the well-known origin path.</summary>
    public string AgentCardPath { get; init; } = "/.well-known/agent-card.json";
    /// <summary>An explicit endpoint URL, overriding the selected card interface URL while retaining its JSONRPC or HTTP+JSON binding.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>The maximum complete JSON response size, defaulting to 16 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
}
