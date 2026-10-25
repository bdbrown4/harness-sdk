namespace Strands.Agents.Mcp;

/// <summary>How the router exposes an MCP tool's explicit error result.</summary>
public enum McpRouterErrorPolicy
{
    /// <summary>Throws a router exception, matching the TypeScript router.</summary>
    Throw,
    /// <summary>Returns the correlated rich error result, matching the Python router.</summary>
    ReturnResult
}

/// <summary>A fixed application-controlled allowlist and model-facing router settings.</summary>
public sealed record McpRouterOptions
{
    /// <summary>Named factories which transfer a fresh connected client to the router. A factory must not return a borrowed or previously returned client.</summary>
    public required IReadOnlyDictionary<string, Func<CancellationToken, Task<McpClient>>> Servers { get; init; }
    /// <summary>The registered tool name, defaulting to mcp_router.</summary>
    public string Name { get; init; } = "mcp_router";
    /// <summary>A description override; null includes the sorted allowlisted server names.</summary>
    public string? Description { get; init; }
    /// <summary>The maximum admitted connections per agent, defaulting to ten.</summary>
    public int MaxConnections { get; init; } = 10;
    /// <summary>The explicit MCP error-result policy, defaulting to the TypeScript throwing behavior.</summary>
    public McpRouterErrorPolicy ErrorPolicy { get; init; }
    /// <summary>An optional discovery predicate receiving the server name and original tool name. It does not prohibit direct calls.</summary>
    public Func<string, string, bool>? ToolFilter { get; init; }
}

/// <summary>A router command or MCP tool error.</summary>
public sealed class McpRouterToolException : InvalidOperationException
{
    /// <summary>Creates a router failure with an actionable command diagnostic.</summary>
    public McpRouterToolException(string message) : base(message) { }
    internal McpRouterToolException(string message, Exception? innerException) : base(message, innerException) { }
}
