using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Memory;

namespace Strands.Harness;

/// <summary>An executable reference in a portable harness definition. Resolving it requires an explicit application binding.</summary>
public sealed class HarnessModuleReference
{
    private readonly JsonElement _value;
    internal HarnessModuleReference(JsonElement value) => _value = value.Clone();
    /// <summary>The declared component kind, supplied by the containing configuration field when omitted.</summary>
    public string Kind => _value.GetProperty("kind").GetString()!;
    /// <summary>The source module or application registration identifier.</summary>
    public string Module => _value.GetProperty("module").GetString()!;
    /// <summary>The exported value identifier, defaulting to default.</summary>
    public string Export => _value.TryGetProperty("export", out var value) ? value.GetString()! : "default";
    /// <summary>The declared source language, when provided. A binding does not execute the source automatically.</summary>
    public string? Language => _value.TryGetProperty("language", out var value) ? value.GetString() : null;
    /// <summary>Source files accompanying the component, preserved as metadata.</summary>
    public IReadOnlyList<string> Files => _value.TryGetProperty("files", out var value) ? Array.AsReadOnly(value.EnumerateArray().Select(item => item.GetString()!).ToArray()) : Array.Empty<string>();
    /// <summary>The dependency declaration, preserved without installing packages.</summary>
    public string? Dependency => _value.TryGetProperty("dependency", out var value) ? value.GetString() : null;
    /// <summary>Returns an owned JSON copy including additional module metadata.</summary>
    public JsonElement ToJsonElement() => _value.Clone();
}

/// <summary>Application bindings used to turn portable JSON into native runtime objects. Components returned by bindings remain caller-owned unless ownership is explicitly transferred through core options.</summary>
public sealed record HarnessConfigBindings
{
    /// <summary>Optional fallback for native module exports when a component-specific resolver is absent. List-valued exports are flattened in declaration order for list-valued configuration fields. No loader is enabled by default.</summary>
    public Func<HarnessModuleReference, object?>? ModuleResolver { get; init; }
    /// <summary>Resolves a provider/name identifier to a preconfigured caller-owned model; null uses native provider resolution. ModelFactory takes precedence.</summary>
    public Func<string, IModel>? ModelResolver { get; init; }
    /// <summary>Constructs a caller-owned model using mapped effort, cache, and search settings. It takes precedence over ModelResolver; supplied core models and model modules take precedence over both.</summary>
    public Func<HarnessModelConfiguration, IModel>? ModelFactory { get; init; }
    /// <summary>Resolves a model module reference; a declared modelModule takes precedence over the string model.</summary>
    public Func<HarnessModuleReference, IModel>? ModelModuleResolver { get; init; }
    /// <summary>Resolves each declared application tool.</summary>
    public Func<HarnessModuleReference, ITool>? ToolResolver { get; init; }
    /// <summary>Resolves specialist agents, exposed through their ordinary AsTool behavior.</summary>
    public Func<HarnessModuleReference, Agent>? SubagentResolver { get; init; }
    /// <summary>Resolves each declared application plugin.</summary>
    public Func<HarnessModuleReference, IPlugin>? PluginResolver { get; init; }
    /// <summary>Resolves each intervention module to ordered preset/policy strings or caller-owned native handlers. Source modules are not executed automatically.</summary>
    public Func<HarnessModuleReference, IReadOnlyList<HarnessInterventionValue>>? InterventionModuleResolver { get; init; }
    /// <summary>Approval callback and optional genuine Cedar integration for portable intervention settings.</summary>
    public HarnessInterventionOptions InterventionOptions { get; init; } = new();
    /// <summary>Resolves each declared native memory backend. Declarations are resolved even when memory is disabled, matching portable module loading.</summary>
    public Func<HarnessModuleReference, MemoryStore>? MemoryStoreResolver { get; init; }
    /// <summary>Resolves the declared filesystem or sandbox implementation.</summary>
    public Func<HarnessModuleReference, ISandbox>? SandboxResolver { get; init; }
    /// <summary>Resolves each named agent-config reference to an application-owned immutable options patch. Resolvers run before native model allocation; patches run in declaration order after JSON core settings. Patches cannot change model identity or ownership. Harness model/name convenience fields and explicit AgentOptions retain precedence. No source module is loaded automatically.</summary>
    public Func<string, HarnessModuleReference, Func<AgentOptions, AgentOptions>>? AgentConfigModuleResolver { get; init; }
    /// <summary>Explicit core options that override configuration convenience values. Their tools and plugins remain additive.</summary>
    public AgentOptions? AgentOptions { get; init; }
}
