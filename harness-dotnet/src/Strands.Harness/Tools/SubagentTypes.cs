using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>Developer authority over one axis of a delegated agent's configuration.</summary>
public abstract record Authority;
/// <summary>Pins a developer-supplied value and exposes no model parameter for this axis.</summary>
/// <param name="Value">The fixed axis value, or null to use its default.</param>
public sealed record Fixed(object? Value = null) : Authority;
/// <summary>Inherits the corresponding configuration from the calling agent.</summary>
public sealed record Inherit : Authority;
/// <summary>Allows the model to supply ad hoc instructions.</summary>
public sealed record Open : Authority;

/// <summary>A named, described option that maps model input onto a developer-supplied value.</summary>
public sealed record Option
{
    /// <summary>Creates an option. An omitted value resolves to its name.</summary>
    public Option(string name, object? value = null, string description = "")
    {
        Guard.NotBlank(name, nameof(name));
        Name = name; Value = value ?? name; Description = description ?? throw new ArgumentNullException(nameof(description));
    }
    /// <summary>The enum entry the model selects.</summary>
    public string Name { get; }
    /// <summary>The actual developer-supplied value associated with the name.</summary>
    public object? Value { get; }
    /// <summary>Model-facing guidance for this option.</summary>
    public string Description { get; }
    /// <summary>Creates a named option for an explicit null choice.</summary>
    public static Option Null(string name, string description = "") => new(name, description);
    private Option(string name, string description) : this(name, value: null, description: description)
    {
        Value = null;
    }
    /// <summary>Uses a bare name as an option with the same value.</summary>
    public static implicit operator Option(string name) => new(name);
}

/// <summary>Allows the model to select one or several options from a closed developer-supplied set.</summary>
public sealed record Choice : Authority
{
    /// <summary>Creates a choice, copying its options and rejecting duplicate names.</summary>
    public Choice(IReadOnlyList<Option> options, bool multiple = false)
    {
        Guard.NotNull(options, nameof(options));
        if (options.Any(option => option is null)) throw new ArgumentException("Choice options cannot contain null.", nameof(options));
        if (options.Select(option => option.Name).Distinct(StringComparer.Ordinal).Count() != options.Count) throw new ArgumentException("Choice option names must be unique.", nameof(options));
        Options = Array.AsReadOnly(options.ToArray()); Multiple = multiple;
    }
    /// <summary>The available named options.</summary>
    public IReadOnlyList<Option> Options { get; }
    /// <summary>Whether the model selects an array of names rather than one name.</summary>
    public bool Multiple { get; }
}

/// <summary>A named role that supplies default values for a delegated task.</summary>
public sealed record Preset
{
    /// <summary>The role instructions, or null to use the axis default.</summary>
    public string? Instructions { get; init; }
    /// <summary>The tool names granted by the role; null inherits the parent's tools.</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    /// <summary>An IModel or provider/name string; null inherits the parent model.</summary>
    public object? Model { get; init; }
    /// <summary>How the role shares parent context: none, all, or no_tools.</summary>
    public string Context { get; init; } = "none";
    /// <summary>An optional message cap, widened to keep complete tool exchanges in all mode.</summary>
    public int? LastMessages { get; init; }
    /// <summary>Model-facing guidance for choosing the role.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>The resolved delegation contract supplied to a native child builder.</summary>
public sealed record AgentSpec
{
    /// <summary>The self-contained task exactly as requested.</summary>
    public required string Task { get; init; }
    /// <summary>The selected role, or null for an ad hoc task.</summary>
    public string? AgentType { get; init; }
    /// <summary>Resolved system instructions, or null to inherit builder defaults.</summary>
    public string? Instructions { get; init; }
    /// <summary>Resolved tool names; null inherits all parent tools. The tool also validates the built child against the parent's authority.</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    /// <summary>Resolved named MCP server grants, independent of ordinary tool selection. Null inherits the parent's server grants.</summary>
    public IReadOnlyList<string>? McpServers { get; init; }
    /// <summary>An IModel or provider/name string; null inherits the parent's model.</summary>
    public object? Model { get; init; }
    /// <summary>The selected context mode.</summary>
    public string Context { get; init; } = "none";
    /// <summary>The resolved message cap before widening to a valid exchange boundary.</summary>
    public int? LastMessages { get; init; }
    /// <summary>Owned initial history for the child. A builder must apply it when constructing the fresh child.</summary>
    public IReadOnlyList<Message> Messages { get; init; } = [];
    /// <summary>The initial user prompt; null continues the provided history, which already contains the framed task.</summary>
    public string? Prompt { get; init; }
}

/// <summary>Settings for a real delegation tool with authority-derived JSON input schema.</summary>
public sealed record SubagentOptions
{
    /// <summary>Builds a fresh native child from the resolved contract. Apply its Messages and Prompt; cancellation is forwarded to both builder and child.</summary>
    public required Func<AgentSpec, ToolContext, CancellationToken, ValueTask<Agent>> Builder { get; init; }
    /// <summary>Selectable named roles, in display order.</summary>
    public IReadOnlyDictionary<string, Preset> Presets { get; init; } = new Dictionary<string, Preset>();
    /// <summary>The default role; null selects the first supplied role.</summary>
    public string? DefaultPreset { get; init; }
    /// <summary>Instruction authority, defaulting to Open.</summary>
    public Authority Instructions { get; init; } = new Open();
    /// <summary>Tool authority. Null creates a multiple Choice over InheritedTools.</summary>
    public Authority? Tools { get; init; }
    /// <summary>Model authority, defaulting to Inherit.</summary>
    public Authority Model { get; init; } = new Inherit();
    /// <summary>Context authority, defaulting to Fixed none.</summary>
    public Authority Context { get; init; } = new Fixed("none");
    /// <summary>Tool names used by the default selection schema. Actual grants are revalidated against the calling parent.</summary>
    public IReadOnlyList<string> InheritedTools { get; init; } = [];
    /// <summary>Named, already connected MCP tool groups. Groups are copied when the tool is created; children borrow the connections.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ITool>> InheritedMcpServers { get; init; } = new Dictionary<string, IReadOnlyList<ITool>>();
    /// <summary>MCP server authority. Null creates a multiple Choice over the inherited named groups, or Inherit when no groups are available.</summary>
    public Authority? McpServers { get; init; }
    /// <summary>Maximum delegation levels; zero prevents delegation. Defaults to two.</summary>
    public int MaxDepth { get; init; } = 2;
    /// <summary>The tool name, defaulting to subagent.</summary>
    public string Name { get; init; } = "subagent";
}

/// <summary>Harness settings for its standard delegation tool.</summary>
public sealed record SubagentConfig
{
    /// <summary>The maximum remaining delegation levels for a root agent. Defaults to two.</summary>
    public int MaxDepth { get; init; } = 2;
}
