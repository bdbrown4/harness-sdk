using Strands.Agents;
using Strands.Harness.Mcp;
using Strands.Harness.Tools;

namespace Strands.Harness;

/// <summary>Native harness composition. Unsupported Python/TypeScript features have no placeholder options.</summary>
public sealed record HarnessOptions
{
    /// <summary>Whether the harness honors OTEL_TRACES_EXPORTER for process-wide tracing setup. Disable when the application owns its tracing provider.</summary>
    public bool UseEnvironmentTelemetry { get; init; } = true;
    internal (string Identifier, IModel Instance)? ConfiguredModel { get; init; }
    internal bool ConfiguredModelFeaturesApplied { get; init; }
    internal bool ConfiguredNativeWebSearch { get; init; }
    internal bool? WebSearchExplicit { get; init; }
    internal IReadOnlyList<IAsyncDisposable> ConfiguredOwnedResources { get; init; } = [];
    internal Models.ModelResolution.PolicySnapshot? ConfiguredPolicy { get; init; }
    /// <summary>The main model, defaulting to the harness Bedrock Opus model. A core model takes precedence; supplied models remain caller-owned.</summary>
    public IModel? Model { get; init; }
    /// <summary>A provider/name model identifier when no explicit model or core model is supplied; defaults to the harness Bedrock Opus model.</summary>
    public string? ModelId { get; init; }
    /// <summary>Reasoning effort for models constructed by the harness or ModelFactory. Supplied models retain their own configuration.</summary>
    public HarnessEffort Effort { get; init; } = HarnessEffort.Auto;
    /// <summary>Prompt caching: null uses the automatic best-effort default, true explicitly enables it, and false disables harness-configured caching.</summary>
    public bool? Caching { get; init; }
    /// <summary>Constructs a caller-owned model from mapped native settings. It takes precedence over ModelResolver; an explicit model or core model takes precedence over both factories.</summary>
    public Func<HarnessModelConfiguration, IModel>? ModelFactory { get; init; }
    /// <summary>An explicit auxiliary model. String-resolved main models otherwise derive a provider-aligned small model with reasoning and caching off; supplied models are reused.</summary>
    public IModel? AuxModel { get; init; }
    /// <summary>Domain instructions appended after the shared contract, unless a core system prompt is supplied.</summary>
    public string? Instructions { get; init; }
    /// <summary>Application tools added alongside selected built-ins and core tools.</summary>
    public IReadOnlyList<ITool> Tools { get; init; } = [];
    /// <summary>Named MCP declarations, connected during agent initialization. Borrowed providers share owned generations until their final parent or child consumer leaves.</summary>
    public McpServerConfiguration? McpServers { get; init; }
    /// <summary>An exact selection of implemented built-ins. Null selects the eight default tools; http_request requires explicit selection. An empty list selects none.</summary>
    public IReadOnlyList<string>? BuiltinTools { get; init; }
    /// <summary>Model-facing settings for the standard shell tool. Execution bounds belong to the selected command sandbox.</summary>
    public ShellConfig Shell { get; init; } = new();
    /// <summary>Typed image/document reads and text paging. Media support defaults from the main model; an explicit Media value overrides detection.</summary>
    public ReadConfig Read { get; init; } = new();
    /// <summary>Bounded page retrieval. Curl uses the selected command sandbox; direct HTTP requires an explicit transport selection.</summary>
    public WebFetchConfig WebFetch { get; init; } = new();
    /// <summary>Raw host HTTP settings, used only when http_request is explicitly selected.</summary>
    public HttpRequestConfig HttpRequest { get; init; } = new();
    /// <summary>Provider-native search or explicit Exa MCP search. The built-in selection still determines whether search is enabled.</summary>
    public HarnessWebSearchOptions WebSearch { get; init; } = new();
    /// <summary>Genuine Monty guest execution and tool selection. Each call creates a fresh native worker; selected registered tools define its external authority.</summary>
    public Strands.Agents.Monty.ProgrammaticToolCallerOptions ProgrammaticToolCaller { get; init; } = new();
    /// <summary>Depth settings for the standard subagent tool.</summary>
    public SubagentConfig Subagent { get; init; } = new();
    /// <summary>Resolves provider/name identifiers to preconfigured caller-owned models. ModelFactory takes precedence and receives mapped effort, caching and search settings; preconfigured models retain their own settings.</summary>
    public Func<string, IModel>? ModelResolver { get; init; }
    /// <summary>Application plugins added before the built-in feature plugins. Instances may be shared when their mutable state remains per-agent.</summary>
    public IReadOnlyList<IPlugin> Plugins { get; init; } = [];
    /// <summary>Ordered presets, policy strings, or native handlers added after core interventions. Names must be unique across both lists.</summary>
    public IReadOnlyList<HarnessInterventionValue> Interventions { get; init; } = [];
    /// <summary>Approval callback and optional genuine Cedar integration. File reads use the selected filesystem.</summary>
    public HarnessInterventionOptions InterventionOptions { get; init; } = new();
    /// <summary>Built-in feature plugins to enable. Null selects todos and environment; an empty list disables both.</summary>
    public IReadOnlyList<string>? BuiltinPlugins { get; init; }
    /// <summary>Progressive skill disclosure, enabled by default when the selected filesystem contains ./.agent/skills.</summary>
    public HarnessSkillsOptions Skills { get; init; } = new();
    /// <summary>Cross-session memory, enabled by default through the selected binary filesystem.</summary>
    public HarnessMemoryOptions Memory { get; init; } = new();
    /// <summary>SDK context management, enabled in automatic mode unless core options or a manager plugin override it.</summary>
    public HarnessContextOptions Context { get; init; } = new();
    /// <summary>Background tool execution, enabled by default. Null disables the harness default; an explicit core configuration takes precedence.</summary>
    public Strands.Agents.Tasks.BackgroundTasksConfig? BackgroundTasks { get; init; } = new();
    /// <summary>The file and optional command implementation. The default uses host permissions without isolation, with an 8 MiB capture policy; each shell tool requests 1 MiB.</summary>
    public ISandbox Sandbox { get; init; } = Harness.CreateDefaultSandbox();
    /// <summary>Local session settings, enabled by default.</summary>
    public SessionOptions Session { get; init; } = new();
    /// <summary>Core options pass through. Explicit model, prompt, auxiliary model and session store take precedence; tools are additive.</summary>
    public AgentOptions? AgentOptions { get; init; }
}

/// <summary>Native JSON conversation persistence. The snapshot format is independent of the Python and TypeScript formats.</summary>
public sealed record SessionOptions
{
    /// <summary>Whether to create a local session store when no explicit core store is supplied.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>An optional identifier to resume. Omit it to generate a fresh eight-hex identifier.</summary>
    public string? Id { get; init; }
    /// <summary>The local snapshot directory, defaulting to ./.agent/sessions relative to the working directory.</summary>
    public string Directory { get; init; } = "./.agent/sessions";
}
