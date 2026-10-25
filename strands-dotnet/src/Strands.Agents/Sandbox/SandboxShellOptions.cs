using System.Text.Json;

namespace Strands.Agents.Sandbox;

/// <summary>Application-selected metadata for a stateless sandbox command tool.</summary>
public sealed record SandboxShellOptions
{
    /// <summary>The model-facing name, defaulting to shell.</summary>
    public string Name { get; init; } = "shell";
    /// <summary>An optional replacement for the shared description.</summary>
    public string? Description { get; init; }
    /// <summary>An optional custom JSON input schema, detached when the tool is created.</summary>
    public JsonElement? InputSchema { get; init; }
}
