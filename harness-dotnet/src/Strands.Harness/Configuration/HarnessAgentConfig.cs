using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Context;
using Strands.Agents.Memory;
using Strands.Agents.Monty;
using Strands.Agents.Skills;
using Strands.Agents.Tasks;
using Strands.Harness.Mcp;
using Strands.Harness.Models;
using Strands.Harness.Tools;

namespace Strands.Harness;

/// <summary>An immutable, normalized portable harness definition using the shared camelCase JSON schema.</summary>
public sealed class HarnessAgentConfig
{
    private readonly JsonElement _value;
    internal HarnessAgentConfig(JsonElement value) => _value = value.Clone();
    /// <summary>The configured agent name. Portable defaults preserve the upstream display name.</summary>
    public string Name => _value.GetProperty("name").GetString()!;
    /// <summary>The configured agent description.</summary>
    public string Description => _value.GetProperty("description").GetString()!;
    /// <summary>Domain instructions appended to the shared harness prompt.</summary>
    public string Instructions => _value.GetProperty("instructions").GetString()!;
    /// <summary>The provider/name model identifier, resolved through an application binding or the native provider builders.</summary>
    public string Model => _value.GetProperty("model").GetString()!;
    /// <summary>Parses and validates portable JSON, supplying shared defaults for omitted fields.</summary>
    public static HarnessAgentConfig Parse(string json)
    {
        Guard.NotNull(json, nameof(json));
        try { using var document = JsonDocument.Parse(json); return Normalize(document.RootElement); }
        catch (JsonException exception) { throw new ArgumentException("Invalid agent config JSON: " + exception.Message, nameof(json), exception); }
    }
    /// <summary>Validates a JSON object and takes an owned copy. Unknown top-level fields are ignored with a Trace warning; recognized nested settings remain strict.</summary>
    public static HarnessAgentConfig Normalize(JsonElement value) => new(ConfigurationSchema.Normalize(value));
    /// <summary>Returns an owned JSON copy with all defaults and normalized module kinds.</summary>
    public JsonElement ToJsonElement() => _value.Clone();
    /// <summary>Serializes the complete normalized portable definition.</summary>
    public string ToJson(bool indented = false)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = indented })) _value.WriteTo(writer);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Resolves supported native capabilities. Enabled features without a native implementation fail explicitly before resolving executable components.</summary>
    /// <remarks>Model policy is bound once. Changing the model identifier, factory, effort, caching, or web-search selection on the returned record requires rebinding through ToOptions; the harness rejects a stale policy rather than silently retaining provider settings.</remarks>
    /// <param name="bindings">Application-owned typed component bindings.</param>
    /// <param name="baseDirectory">Project directory for relative session paths; defaults to the current working directory.</param>
    /// <returns>Runtime options ready for Harness.CreateAsync.</returns>
    public HarnessOptions ToOptions(HarnessConfigBindings? bindings = null, string? baseDirectory = null)
    {
        bindings ??= new();
        var tools = ResolveBuiltins(_value.GetProperty("builtinTools"));
        var programmaticToolCaller = ReadProgrammaticToolCallerConfig(_value.GetProperty("builtinTools"));
        var coreSettings = ReadCoreSettings(_value.GetProperty("agentConfig"));
        HarnessBackgroundTasks.Validate(coreSettings.BackgroundTasks);
        var modulePatches = ResolveAgentConfigModules(bindings);
        var session = _value.GetProperty("session");
        var root = Path.GetFullPath(baseDirectory ?? Directory.GetCurrentDirectory());
        var sessionDirectory = session.ValueKind == JsonValueKind.Object && session.TryGetProperty("dir", out var dir) ? dir.GetString()! : "./.agent/sessions";
        var resolvedSessionDirectory = ResolvePath(sessionDirectory, root);
        var modelReference = _value.GetProperty("modelModule");
        var builtins = _value.GetProperty("builtinTools");
        var policyOptions = new HarnessOptions
        {
            ModelId = Model,
            Effort = ModelPolicy.ParseEffort(_value.GetProperty("effort").GetString()!),
            // Both portable loaders only forward caching=false; true keeps the automatic default.
            Caching = _value.GetProperty("caching").GetBoolean() ? null : false,
            ModelFactory = bindings.ModelFactory,
            ModelResolver = bindings.ModelResolver,
            BuiltinTools = tools,
            WebSearch = ReadWebSearchConfig(builtins),
            WebSearchExplicit = IsWebSearchExplicit(builtins),
        };
        var configurable = bindings.AgentOptions is null && modelReference.ValueKind == JsonValueKind.Null && (bindings.ModelFactory is not null || bindings.ModelResolver is null);
        var nativeSearch = ModelResolution.NativeSearch(policyOptions, configurable ? Model : null);
        var configuration = configurable ? ModelPolicy.Configure(Model, policyOptions.Effort, policyOptions.Caching ?? true, false, nativeSearch) : null;
        var boundTools = _value.GetProperty("tools").EnumerateArray().SelectMany(reference => ResolveMany(bindings.ToolResolver, reference, nameof(bindings.ToolResolver), bindings)).ToList();
        boundTools.AddRange(_value.GetProperty("subagents").EnumerateArray().SelectMany(reference => ResolveMany(bindings.SubagentResolver, reference, nameof(bindings.SubagentResolver), bindings)).Select(agent => agent.AsTool()));
        var plugins = _value.GetProperty("plugins").EnumerateArray().SelectMany(reference => ResolveMany(bindings.PluginResolver, reference, nameof(bindings.PluginResolver), bindings)).ToArray();
        Guard.NotNull(bindings.InterventionOptions, nameof(bindings.InterventionOptions));
        var interventions = ReadInterventions(_value.GetProperty("interventions"), root).ToList();
        foreach (var reference in _value.GetProperty("interventionModules").EnumerateArray())
            foreach (var setting in bindings.InterventionModuleResolver is { } resolveInterventions
                ? Resolve(resolveInterventions, reference, nameof(bindings.InterventionModuleResolver), bindings)
                : ResolveMany<HarnessInterventionValue>(null, reference, nameof(bindings.InterventionModuleResolver), bindings))
            {
                Guard.NotNull(setting, nameof(bindings.InterventionModuleResolver));
                interventions.Add(setting);
            }
        var sandboxReference = _value.GetProperty("sandbox");
        var sandbox = sandboxReference.ValueKind == JsonValueKind.Null ? Harness.CreateDefaultSandbox() : Resolve(bindings.SandboxResolver, sandboxReference, nameof(bindings.SandboxResolver), bindings);
        var memoryStores = _value.GetProperty("memoryStores").EnumerateArray().SelectMany(reference => ResolveMany(bindings.MemoryStoreResolver, reference, nameof(bindings.MemoryStoreResolver), bindings)).ToArray();
        var subagent = ReadSubagentConfig(builtins);
        var shell = ReadShellConfig(builtins);
        var webFetch = ReadWebFetchConfig(builtins, bindings);
        var skills = ReadSkills(_value.GetProperty("skills"), root);
        var memory = ReadMemory(_value.GetProperty("memory"), memoryStores, root);
        var context = ReadContext(_value.GetProperty("contextManager"));
        var servers = McpServerConfiguration.FromPortable(_value.GetProperty("mcpServers"), root);
        // Resolve owned native transports last so later component-binding failures cannot strand them.
        var resources = new List<IAsyncDisposable>();
        ModelResolution.Resolved resolved;
        if (bindings.AgentOptions?.Model is { } coreModel) resolved = new(coreModel, bindings.AgentOptions.OwnsModel, null, false);
        else if (modelReference.ValueKind != JsonValueKind.Null) resolved = new(Resolve(bindings.ModelModuleResolver, modelReference, nameof(bindings.ModelModuleResolver), bindings), false, null, false);
        else
        {
            try { resolved = ModelResolution.Identifier(policyOptions, Model, policyOptions.Effort, policyOptions.Caching ?? true, false, nativeSearch, resources, configuration); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // These resources are native HTTP handles whose disposal is synchronous.
                foreach (var resource in resources.AsEnumerable().Reverse())
                    try { resource.DisposeAsync().GetAwaiter().GetResult(); }
                    catch (Exception cleanup)
                    {
                        const string key = "Strands.CleanupErrors";
                        if (exception.Data[key] is not List<Exception> errors) exception.Data[key] = errors = [];
                        errors.Add(cleanup);
                    }
                throw new InvalidOperationException($"Could not resolve model '{Model}' through ModelFactory/ModelResolver: {exception.Message}", exception);
            }
        }
        var model = resolved.Model;
        try
        {
            var initialCore = bindings.AgentOptions ?? new AgentOptions
            {
                Model = model,
                OwnsModel = resolved.Owned,
                OwnedResources = resources.ToArray(),
                Name = Name,
                Description = string.IsNullOrEmpty(Description) ? null : Description,
                MaxTurns = coreSettings.MaxTurns,
                MaxParallelTools = coreSettings.MaxParallelTools,
                ToolExecution = coreSettings.ToolExecution,
                SystemPrompt = coreSettings.SystemPrompt,
                State = coreSettings.State,
            };
            var patchedCore = ApplyAgentConfigModules(initialCore, modulePatches);
            // Portable convenience fields follow agentConfig/module values in both loaders. An
            // explicitly supplied native options record remains the final authority for core fields.
            var core = bindings.AgentOptions ?? patchedCore with
            {
                Name = Name,
                Description = string.IsNullOrEmpty(Description) ? patchedCore.Description : Description,
            };
            return new HarnessOptions
            {
                Model = model,
                ConfiguredModel = resolved.Identifier is { } identifier ? (identifier, model) : null,
                ConfiguredModelFeaturesApplied = true,
                ConfiguredPolicy = ModelResolution.Capture(policyOptions),
                ConfiguredOwnedResources = resources.ToArray(),
                ConfiguredNativeWebSearch = resolved.NativeWebSearch,
                ModelId = Model,
                ModelFactory = bindings.ModelFactory,
                Effort = policyOptions.Effort,
                Caching = policyOptions.Caching,
                WebSearch = policyOptions.WebSearch,
                WebSearchExplicit = policyOptions.WebSearchExplicit,
                AgentOptions = core,
                Instructions = Instructions,
                Tools = boundTools,
                Plugins = plugins,
                Interventions = interventions.ToArray(),
                InterventionOptions = bindings.InterventionOptions,
                ModelResolver = bindings.ModelResolver,
                Subagent = subagent,
                Shell = shell,
                Read = ReadFileConfig(builtins),
                WebFetch = webFetch,
                ProgrammaticToolCaller = programmaticToolCaller,
                BuiltinTools = tools,
                BuiltinPlugins = _value.GetProperty("builtinPlugins").EnumerateArray().Select(item => item.GetString()!).ToArray(),
                Sandbox = sandbox,
                Skills = skills,
                Memory = memory,
                Context = context,
                BackgroundTasks = coreSettings.HasBackgroundTasks ? coreSettings.BackgroundTasks : new(),
                McpServers = servers,
                Session = new()
                {
                    Enabled = session.ValueKind != JsonValueKind.False,
                    Id = session.ValueKind == JsonValueKind.Object && session.TryGetProperty("id", out var id) ? id.GetString() : null,
                    Directory = resolvedSessionDirectory,
                },
            };
        }
        catch (Exception failure)
        {
            // Only resources/model created by this conversion are ours before transfer. Explicit
            // core resources and models are still caller-owned when an application patch fails.
            var ownedModel = bindings.AgentOptions is null && modelReference.ValueKind == JsonValueKind.Null && resolved.Owned
                ? new AgentOptions { Model = model, OwnsModel = true } : null;
            Harness.CleanupFailedConstructionAsync(null, ownedModel, resources, failure).GetAwaiter().GetResult();
            throw;
        }
    }

    private IReadOnlyList<(string Name, Func<AgentOptions, AgentOptions> Apply)> ResolveAgentConfigModules(HarnessConfigBindings bindings)
    {
        var patches = new List<(string Name, Func<AgentOptions, AgentOptions> Apply)>();
        foreach (var property in _value.GetProperty("agentConfigModules").EnumerateObject())
        {
            var path = "agentConfigModules." + property.Name;
            var reference = new HarnessModuleReference(property.Value);
            var patch = bindings.AgentConfigModuleResolver is { } resolver ? resolver(property.Name, reference)
                : bindings.ModuleResolver is { } fallback ? fallback(reference) as Func<AgentOptions, AgentOptions>
                : throw new NotSupportedException(path + " requires HarnessConfigBindings.AgentConfigModuleResolver or an explicit ModuleResolver. Source modules are not loaded automatically.");
            if (patch is null) throw new InvalidOperationException(path + " returned a null or incompatible options patch.");
            patches.Add((path, patch));
        }
        return patches;
    }

    private static AgentOptions ApplyAgentConfigModules(AgentOptions initial, IReadOnlyList<(string Name, Func<AgentOptions, AgentOptions> Apply)> patches)
    {
        var current = initial;
        foreach (var patch in patches)
        {
            var model = current.Model;
            var ownsModel = current.OwnsModel;
            var resources = current.OwnedResources?.ToArray() ?? throw new ArgumentException(patch.Name + " requires nonnull owned resources.");
            var next = patch.Apply(current) ?? throw new InvalidOperationException(patch.Name + " returned null AgentOptions.");
            if (!ReferenceEquals(next.Model, model) || next.OwnsModel != ownsModel || next.OwnedResources is null ||
                next.OwnedResources.Count != resources.Length || next.OwnedResources.Where((resource, index) => !ReferenceEquals(resource, resources[index])).Any())
                throw new ArgumentException(patch.Name + " cannot change model identity or ownership (Model, OwnsModel, or OwnedResources). Configure models through model/modelModule or explicit AgentOptions before binding; replacement models and resources remain caller-owned.");
            current = next;
        }
        return current;
    }

    private static IEnumerable<HarnessInterventionValue> ReadInterventions(JsonElement value, string root)
    {
        if (value.ValueKind == JsonValueKind.Null) yield break;
        var policies = value.ValueKind == JsonValueKind.String ? new[] { value.GetString()! } : value.EnumerateArray().Select(item => item.GetString()!).ToArray();
        foreach (var policy in policies)
        {
            var text = policy.Trim();
            yield return text.EndsWith(".cedar", StringComparison.Ordinal) ? ResolvePath(text, root) : policy;
        }
    }

    private static HarnessWebSearchOptions ReadWebSearchConfig(JsonElement builtins) => builtins.ValueKind == JsonValueKind.Object &&
        builtins.TryGetProperty("web_search", out var search) && search.ValueKind == JsonValueKind.String && search.GetString() == "exa"
        ? new() { Provider = WebSearchProvider.Exa } : new();

    private static bool IsWebSearchExplicit(JsonElement builtins) => builtins.ValueKind == JsonValueKind.Array
        ? !builtins.EnumerateArray().Select(item => item.GetString()!).SequenceEqual(ConfigurationSchema.BuiltinNames) && builtins.EnumerateArray().Any(item => item.GetString() == "web_search")
        : builtins.TryGetProperty("web_search", out var search) && search.ValueKind != JsonValueKind.False;

    private static HarnessSkillsOptions ReadSkills(JsonElement value, string root)
    {
        if (value.ValueKind == JsonValueKind.False) return new() { Enabled = false };
        if (value.ValueKind == JsonValueKind.True) return new();
        var sources = value.ValueKind == JsonValueKind.String ? new[] { value.GetString()! } : value.EnumerateArray().Select(item => item.GetString()!).ToArray();
        return new() { Sources = sources.Select(source => new SkillSource(source.StartsWith("https://", StringComparison.Ordinal) ? source : ResolvePath(source, root))).ToArray() };
    }

    private static HarnessContextOptions ReadContext(JsonElement value) => value.ValueKind == JsonValueKind.False
        ? new() { Enabled = false }
        : new() { Mode = value.GetString() == "agentic" ? ContextMode.Agentic : ContextMode.Auto };

    private static HarnessMemoryOptions ReadMemory(JsonElement value, IReadOnlyList<MemoryStore> stores, string root)
    {
        if (value.ValueKind == JsonValueKind.False) return new() { Enabled = false };
        var directory = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("dir", out var path) ? path.GetString()! : "./.agent/memory";
        return new() { Directory = ResolvePath(directory, root), Stores = stores };
    }

    private static SubagentConfig ReadSubagentConfig(JsonElement builtins) => builtins.ValueKind == JsonValueKind.Object && builtins.TryGetProperty("subagent", out var config) && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("maxDepth", out var maxDepth)
        ? new() { MaxDepth = maxDepth.GetInt32() } : new();

    private static ShellConfig ReadShellConfig(JsonElement builtins) => builtins.ValueKind == JsonValueKind.Object && builtins.TryGetProperty("shell", out var config) && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("description", out var description)
        ? new() { Description = description.GetString() } : new();

    private static ReadConfig ReadFileConfig(JsonElement builtins) => builtins.ValueKind == JsonValueKind.Object && builtins.TryGetProperty("read", out var config) && config.ValueKind == JsonValueKind.Object && config.TryGetProperty("media", out var media)
        ? new() { Media = media.GetBoolean() } : new();

    private static WebFetchConfig ReadWebFetchConfig(JsonElement builtins, HarnessConfigBindings bindings)
    {
        if (builtins.ValueKind != JsonValueKind.Object || !builtins.TryGetProperty("web_fetch", out var config) || config.ValueKind != JsonValueKind.Object) return new();
        var result = new WebFetchConfig { Transport = config.TryGetProperty("transport", out var transport) && transport.GetString() == "direct" ? WebFetchTransport.Direct : WebFetchTransport.Curl };
        if (config.TryGetProperty("model", out var model)) result = model.ValueKind == JsonValueKind.String
            ? result with { ModelIdentifier = model.GetString() }
            : result with { Model = Resolve(bindings.ModelModuleResolver, model, nameof(bindings.ModelModuleResolver), bindings) };
        return result;
    }

    private static ProgrammaticToolCallerOptions ReadProgrammaticToolCallerConfig(JsonElement builtins)
    {
        if (builtins.ValueKind != JsonValueKind.Object || !builtins.TryGetProperty("programmatic_tool_caller", out var config) || config.ValueKind != JsonValueKind.Object) return new();
        var result = new ProgrammaticToolCallerOptions();
        if (config.TryGetProperty("allowedTools", out var allowed) && allowed.ValueKind != JsonValueKind.Null)
            result = result with { AllowedTools = allowed.EnumerateArray().Select(item => item.GetString()!).ToArray() };
        if (config.TryGetProperty("timeout", out var timeout))
            result = result with { Timeout = timeout.ValueKind == JsonValueKind.Null ? null : TimeSpan.FromSeconds(timeout.GetDouble()) };
        // Validate before resolving application modules or allocating provider transports.
        _ = Strands.Agents.Monty.ProgrammaticToolCaller.Create(result);
        return result;
    }

    private static T Resolve<T>(Func<HarnessModuleReference, T>? resolver, JsonElement value, string binding, HarnessConfigBindings bindings) where T : class
    {
        var reference = new HarnessModuleReference(value);
        var resolved = resolver is not null ? resolver(reference) : bindings.ModuleResolver is { } fallback ? fallback(reference)
            : throw new InvalidOperationException($"Module '{reference.Module}' (kind '{reference.Kind}', export '{reference.Export}') requires HarnessConfigBindings.{binding} or an explicit ModuleResolver. Source modules are not executed automatically.");
        if (typeof(T) == typeof(ISandbox) && resolved is Strands.Agents.Sandbox.Sandbox selected) resolved = new SandboxAdapter(selected);
        return resolved as T ?? throw new InvalidOperationException($"Module '{reference.Module}' export '{reference.Export}' returned null or a value incompatible with {typeof(T).Name}.");
    }

    private static IReadOnlyList<T> ResolveMany<T>(Func<HarnessModuleReference, T>? resolver, JsonElement value, string binding, HarnessConfigBindings bindings) where T : class
    {
        if (resolver is not null) return new[] { Resolve(resolver, value, binding, bindings) };
        var reference = new HarnessModuleReference(value);
        if (bindings.ModuleResolver is null) return new[] { Resolve(resolver, value, binding, bindings) };
        var loaded = bindings.ModuleResolver(reference);
        if (loaded is T single) return new[] { single };
        if (loaded is IReadOnlyList<T> items)
        {
            var snapshot = items.ToArray();
            if (snapshot.All(item => item is not null)) return snapshot;
        }
        throw new InvalidOperationException($"Module '{reference.Module}' export '{reference.Export}' must return {typeof(T).Name} or an ordered list of nonnull {typeof(T).Name} values.");
    }

    private static IReadOnlyList<string> ResolveBuiltins(JsonElement value)
    {
        var selected = value.ValueKind == JsonValueKind.Array
            ? new HashSet<string>(value.EnumerateArray().Select(item => item.GetString()!), StringComparer.Ordinal)
            : new HashSet<string>(!value.TryGetProperty("*", out var all) || all.GetBoolean() ? ConfigurationSchema.BuiltinNames : Array.Empty<string>(), StringComparer.Ordinal);
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "*") continue;
                if (property.Value.ValueKind == JsonValueKind.False) selected.Remove(property.Name);
                else
                {
                    selected.Add(property.Name);
                }
            }
        foreach (var name in selected)
            if (!Harness.BuiltinToolNames.Contains(name, StringComparer.Ordinal)) Unsupported("builtinTools." + name);
        return Harness.BuiltinToolNames.Where(selected.Contains).ToArray();
    }

    private static CoreSettings ReadCoreSettings(JsonElement value)
    {
        var result = new CoreSettings();
        foreach (var property in value.EnumerateObject())
            switch (property.Name)
            {
                case "maxTurns": result.MaxTurns = PositiveInteger(property.Value, "agentConfig.maxTurns"); break;
                case "maxParallelTools": result.MaxParallelTools = PositiveInteger(property.Value, "agentConfig.maxParallelTools"); break;
                case "toolExecution":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not ("concurrent" or "sequential")) throw new ArgumentException("agentConfig.toolExecution must be concurrent or sequential.");
                    result.ToolExecution = property.Value.GetString() == "sequential" ? ToolExecutionStrategy.Sequential : ToolExecutionStrategy.Concurrent;
                    break;
                case "systemPrompt":
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("agentConfig.systemPrompt must be a string or null.");
                    result.SystemPrompt = property.Value.GetString(); break;
                case "state":
                    ConfigurationSchema.Object(property.Value, "agentConfig.state");
                    result.State = property.Value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal); break;
                case "backgroundTasks":
                    result.HasBackgroundTasks = true; result.BackgroundTasks = ReadBackgroundTasks(property.Value); break;
                default: Unsupported("agentConfig." + property.Name); break;
            }
        return result;
    }

    private static int PositiveInteger(JsonElement value, string path) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : throw new ArgumentException(path + " must be a positive integer.");
    private static string ResolvePath(string path, string root)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length == 1 ? string.Empty : path.Substring(2));
        if (Path.IsPathRooted(path) && !Guard.IsFullyQualifiedLocalPath(path)) throw new ArgumentException("Session directory must be fully qualified or relative to the project directory.");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }
    private static void Unsupported(string path, string? hint = null) => throw new NotSupportedException($"{path} is not implemented by the native harness configuration converter." + (hint is null ? string.Empty : " " + hint));
    private sealed class CoreSettings
    {
        internal int MaxTurns { get; set; } = 20;
        internal int MaxParallelTools { get; set; } = 8;
        internal ToolExecutionStrategy ToolExecution { get; set; }
        internal string? SystemPrompt { get; set; }
        internal IReadOnlyDictionary<string, JsonElement> State { get; set; } = new Dictionary<string, JsonElement>();
        internal bool HasBackgroundTasks { get; set; }
        internal BackgroundTasksConfig? BackgroundTasks { get; set; }
    }
    private static BackgroundTasksConfig? ReadBackgroundTasks(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.False or JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.True) return new();
        ConfigurationSchema.Object(value, "agentConfig.backgroundTasks");
        var config = new BackgroundTasksConfig();
        foreach (var field in value.EnumerateObject())
        {
            switch (field.Name)
            {
                case "waitForCompletion":
                    if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("agentConfig.backgroundTasks.waitForCompletion must be a boolean.");
                    config = config with { WaitForCompletion = field.Value.GetBoolean() }; break;
                case "agentic": config = config with { Agentic = BackgroundNames(field.Value, field.Name) }; break;
                case "always": config = config with { Always = BackgroundNames(field.Value, field.Name) }; break;
                case "never": config = config with { Never = BackgroundNames(field.Value, field.Name) }; break;
                case "maxConcurrency": config = config with { MaxConcurrency = PositiveInteger(field.Value, "agentConfig.backgroundTasks.maxConcurrency") }; break;
                case "timeout": config = config with { Timeout = TimeSpan.FromMilliseconds(PositiveInteger(field.Value, "agentConfig.backgroundTasks.timeout")) }; break;
                default: throw new ArgumentException("Unknown agentConfig.backgroundTasks field '" + field.Name + "'.");
            }
        }
        return config;
    }
    private static IReadOnlyList<string> BackgroundNames(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            throw new ArgumentException("agentConfig.backgroundTasks." + name + " must be an array of nonempty registered tool names.");
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
