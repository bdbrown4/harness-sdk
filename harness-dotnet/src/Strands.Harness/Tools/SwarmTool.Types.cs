using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>Resolved configuration for one model-defined swarm participant.</summary>
public sealed record SwarmAgentSpec
{
    /// <summary>The participant name, used as its swarm node identity.</summary>
    public required string Name { get; init; }
    /// <summary>The selected named preset, or null for an ad hoc participant.</summary>
    public string? AgentType { get; init; }
    /// <summary>The resolved system instructions.</summary>
    public string? Instructions { get; init; }
    /// <summary>Selected parent tool names; null inherits all admitted parent tools.</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    /// <summary>A borrowed native model or a developer-selected native provider/name.</summary>
    public object? Model { get; init; }
}

/// <summary>Developer authority and limits for a model-defined team tool.</summary>
public sealed record SwarmToolOptions
{
    /// <summary>The model-facing tool name.</summary>
    public string Name { get; init; } = "swarm";
    /// <summary>The tool description; null selects the pinned default description.</summary>
    public string? Description { get; init; }
    /// <summary>An optional builder that transfers a fresh child to the helper.</summary>
    public Func<SwarmAgentSpec, ToolContext, CancellationToken, ValueTask<Agent>>? Builder { get; init; }
    /// <summary>Named roles in declaration order.</summary>
    public IReadOnlyDictionary<string, Preset> Presets { get; init; } = new Dictionary<string, Preset>();
    /// <summary>The default role, or null to select the first configured role.</summary>
    public string? DefaultPreset { get; init; }
    /// <summary>Instruction authority, defaulting to model-supplied text.</summary>
    public Authority Instructions { get; init; } = new Open();
    /// <summary>Tool authority, defaulting to hidden inheritance.</summary>
    public Authority Tools { get; init; } = new Inherit();
    /// <summary>Model authority, defaulting to hidden inheritance.</summary>
    public Authority Model { get; init; } = new Inherit();
    /// <summary>The maximum participants per call, defaulting to twenty.</summary>
    public int MaxAgents { get; init; } = 20;
    /// <summary>An optional override of the primitive's twenty completed-visit budget.</summary>
    public int? MaxHandoffs { get; init; }
    /// <summary>An optional override of the primitive's twenty logical-visit budget.</summary>
    public int? MaxIterations { get; init; }
    /// <summary>An optional override of the primitive's 900-second execution budget.</summary>
    public TimeSpan? ExecutionTimeout { get; init; }
    /// <summary>An optional override of the primitive's 300-second node budget.</summary>
    public TimeSpan? NodeTimeout { get; init; }
    /// <summary>An optional repetition window; zero disables detection.</summary>
    public int? RepetitiveHandoffDetectionWindow { get; init; }
    /// <summary>An optional minimum distinct-agent count for repetition detection.</summary>
    public int? RepetitiveHandoffMinUniqueAgents { get; init; }
}
