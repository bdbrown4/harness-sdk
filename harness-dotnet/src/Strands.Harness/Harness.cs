using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Context;
using Strands.Agents.Files;
using Strands.Agents.Memory;
using Strands.Agents.Skills;
using Strands.Agents.Storage;
using Strands.Harness.Mcp;
using Strands.Harness.Models;
using Strands.Harness.Plugins;
using Strands.Harness.Storage;
using Strands.Harness.Tools;

namespace Strands.Harness;

/// <summary>Composes a native Strands agent with a shared behavioral prompt, text file tools and optional local sessions.</summary>
public static class Harness
{
    /// <summary>The default provider/model identifier, matching both upstream harnesses.</summary>
    public const string DefaultModel = "bedrock/global.anthropic.claude-opus-5";
    /// <summary>The default built-in capabilities, in registration order. Optional http_request requires explicit selection.</summary>
    public static IReadOnlyList<string> BuiltinToolNames { get; } = Array.AsReadOnly(new[] { "shell", "read", "write", "edit", "web_fetch", "web_search", "programmatic_tool_caller", "subagent" });
    /// <summary>Built-in feature plugins implemented by this harness, in registration order.</summary>
    public static IReadOnlyList<string> BuiltinPluginNames { get; } = Array.AsReadOnly(new[] { "todos", "environment" });

    internal static LocalFileSandbox CreateDefaultSandbox() => new(shellOptions: new() { MaxOutputBytes = 8 * 1024 * 1024 });

    /// <summary>Returns an ordinary agent. File tools use host permissions unless a custom sandbox enforces an access policy.</summary>
    public static async Task<Agent> CreateAsync(HarnessOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var core = options.AgentOptions;
        Agent? agent = null;
        // Native allocations made by ToOptions transfer before Agent construction. Caller resources
        // in core.OwnedResources stay with the caller if construction fails before an Agent exists.
        var acquired = new List<IAsyncDisposable>(options.ConfiguredOwnedResources);
        try
        {
            Guard.NotNull(options.Sandbox, nameof(options.Sandbox));
            Guard.NotNull(options.Session, nameof(options.Session));
            Guard.NotNull(options.Tools, nameof(options.Tools));
            Guard.NotNull(options.Plugins, nameof(options.Plugins));
            Guard.NotNull(options.Interventions, nameof(options.Interventions));
            Guard.NotNull(options.InterventionOptions, nameof(options.InterventionOptions));
            Guard.NotNull(options.Skills, nameof(options.Skills));
            Guard.NotNull(options.Memory, nameof(options.Memory));
            Guard.NotNull(options.Context, nameof(options.Context));
            Guard.NotNull(options.WebFetch, nameof(options.WebFetch));
            Guard.NotNull(options.WebSearch, nameof(options.WebSearch));
            Guard.NotNull(options.Read, nameof(options.Read));
            options.Read.Validate();
            if (!Enum.IsDefined(typeof(HarnessEffort), options.Effort)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown reasoning effort.");
            cancellationToken.ThrowIfCancellationRequested();
            var backgroundTasks = core?.BackgroundTasks ?? options.BackgroundTasks;
            if (core?.BackgroundTasks is not null) backgroundTasks?.Validate();
            else HarnessBackgroundTasks.Validate(backgroundTasks);
            if (options.UseEnvironmentTelemetry) HarnessTelemetry.EnsureConfigured();
            var resolved = ModelResolution.Main(options, acquired);
            core ??= new AgentOptions { Model = resolved.Model, Name = "strands_harness", OwnsModel = resolved.Owned };
            options = options with
            {
                ConfiguredModel = resolved.Identifier is { } identifier ? (identifier, resolved.Model) : null,
                ConfiguredModelFeaturesApplied = true,
                ConfiguredNativeWebSearch = resolved.NativeWebSearch,
            };
            Guard.NotNull(core.Plugins, nameof(core.Plugins));
            Guard.NotNull(core.Interventions, nameof(core.Interventions));
            Guard.NotNull(core.OwnedResources, nameof(core.OwnedResources));
            var plugins = core.Plugins.Concat(options.Plugins).ToList();
            plugins.AddRange(SelectPlugins(options, plugins));
            var context = core.ContextManager ?? (plugins.OfType<ContextManager>().Any() || core.ConversationManager is not null ? null : SelectContext(options.Context));
            var childContextMode = context is null && !plugins.OfType<ContextManager>().Any() ? (ContextMode?)null
                : core.ContextManager is not null || plugins.OfType<ContextManager>().Any() || options.Context.Manager is not null ? ContextMode.Auto : options.Context.Mode;
            var fileSystem = core.FileSystem ?? core.Sandbox ?? SandboxFileSystem.Create(options.Sandbox);
            var interventions = core.Interventions.Concat(await HarnessInterventions.ResolveAsync(options.Interventions,
                options.InterventionOptions with { FileSystem = fileSystem }, cancellationToken).ConfigureAwait(false)).ToArray();
            HarnessInterventions.ValidateNames(interventions);
            var skills = await SelectSkillsAsync(options.Skills, plugins, fileSystem, cancellationToken).ConfigureAwait(false);
            if (skills is not null) plugins.Add(skills);
            IModel? auxiliary = null;
            IModel DefaultAuxiliary() => auxiliary ??= AuxiliaryModels.Resolve(options, core, acquired);
            IModel? webFetchAuxiliary = null;
            IModel WebFetchAuxiliary() => webFetchAuxiliary ??= ResolveWebFetchModel(options, acquired, DefaultAuxiliary);
            var memory = core.MemoryManager ?? (plugins.OfType<MemoryManager>().Any() ? null : SelectMemory(options.Memory, fileSystem, WebFetchAuxiliary));
            var inheritMemory = core.MemoryManager is null && !plugins.OfType<MemoryManager>().Any() && options.Memory.Manager is null;
            Guard.NotNull(core.ToolProviders, nameof(core.ToolProviders));
            var applicationProviders = core.ToolProviders.ToArray();
            var declaredProviders = options.McpServers is null ? Array.Empty<Agents.Mcp.McpToolProvider>()
                : await options.McpServers.CreateToolProvidersAsync(options.Sandbox, cancellationToken).ConfigureAwait(false);
            var providers = applicationProviders.Concat(declaredProviders).ToArray();
            var toolOptions = options;
            if ((options.BuiltinTools ?? BuiltinToolNames).Contains("web_fetch", StringComparer.Ordinal) && options.WebFetch.Transport == WebFetchTransport.Curl)
                toolOptions = options with { WebFetch = options.WebFetch with { Shell = await CurlWebFetch.ResolveShellAsync(options.Sandbox, options.WebFetch.Shell, cancellationToken).ConfigureAwait(false) } };
            var tools = SelectBuiltins(toolOptions, WebFetchAuxiliary).Concat(core.Tools).Concat(options.Tools).ToArray();
            var sessionStore = core.SessionStore;
            var sessionId = core.SessionId;
            if (sessionStore is null && options.Session.Enabled)
            {
                Guard.NotBlank(options.Session.Directory, nameof(options.Session.Directory));
                sessionStore = new FileSessionStore(options.Session.Directory);
                sessionId ??= options.Session.Id is null ? Guid.NewGuid().ToString("N").Substring(0, 8) : SanitizeSessionId(options.Session.Id);
            }
            agent = new Agent(core with
            {
                AuxModel = core.AuxModel ?? options.AuxModel,
                SystemPrompt = core.SystemPrompt ?? HarnessPrompt.Build(options.Instructions),
                Tools = tools,
                ToolProviders = providers,
                SessionStore = sessionStore,
                SessionId = sessionId,
                Plugins = plugins,
                Interventions = interventions,
                OwnedResources = core.OwnedResources.Concat(acquired.Where(resource => !core.OwnedResources.Any(existing => ReferenceEquals(existing, resource)))).ToArray(),
                FileSystem = core.FileSystem ?? (core.Sandbox is null ? fileSystem : null),
                Sandbox = core.Sandbox ?? SelectedSandboxAdapter.Create(options.Sandbox),
                MemoryManager = memory,
                ContextManager = context,
                BackgroundTasks = backgroundTasks,
                BackgroundTasksPolicyNormalizer = core.BackgroundTasks is null ? HarnessBackgroundTasks.Resolve : null,
                InitializationTools = (options.BuiltinTools ?? BuiltinToolNames).Contains("subagent", StringComparer.Ordinal)
                    ? initialized =>
                    {
                        Guard.NotNull(options.Subagent, nameof(options.Subagent));
                        return new[] { StandardSubagent.Create(initialized.Tools.Select(tool => initialized.RegisteredToolSpec(tool).Name).Concat(new[] { "subagent" }).ToArray(), options.Subagent, options, McpProviderBindings.Bind(initialized, providers), inheritMemory, childContextMode) };
                    }
                : null,
            });
            await agent.InitializeAsync(cancellationToken).ConfigureAwait(false);
            CheckNameCollisions(agent.Tools.Select(agent.RegisteredToolSpec));
            return agent;
        }
        catch (Exception failure)
        {
            await CleanupFailedConstructionAsync(agent, core, acquired, failure).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task CleanupFailedConstructionAsync(Agent? agent, AgentOptions? core, IReadOnlyList<IAsyncDisposable> acquired, Exception failure)
    {
        if (agent is not null)
        {
            try { await agent.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { RecordCleanupFailure(failure, cleanup); }
            return;
        }
        for (var index = acquired.Count - 1; index >= 0; index--)
        {
            try { await acquired[index].DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { RecordCleanupFailure(failure, cleanup); }
        }
        if (core?.OwnsModel != true) return;
        try
        {
            if (core.Model is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
            else if (core.Model is IDisposable disposable) disposable.Dispose();
        }
        catch (Exception cleanup) { RecordCleanupFailure(failure, cleanup); }
    }

    private static void RecordCleanupFailure(Exception failure, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (failure.Data[key] is not List<Exception> errors) failure.Data[key] = errors = [];
        errors.Add(cleanup);
    }

    /// <summary>Resolves a native Bedrock provider/name model lazily. Other providers require an explicit ModelResolver binding.</summary>
    /// <param name="identifier">A nonempty bedrock/model identifier.</param>
    /// <returns>A caller-owned model; dispose it when it is no longer used.</returns>
    public static IModel ResolveModel(string identifier)
    {
        Guard.NotBlank(identifier, nameof(identifier));
        const string prefix = "bedrock/";
        if (!identifier.StartsWith(prefix, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(identifier.Substring(prefix.Length)))
            throw new ArgumentException($"Unknown model identifier '{identifier}'. Native resolution currently accepts bedrock/model; bind other providers through HarnessConfigBindings.ModelResolver.", nameof(identifier));
        return new BedrockModel(new BedrockModelOptions { ModelId = identifier.Substring(prefix.Length) });
    }

    private static IEnumerable<IPlugin> SelectPlugins(HarnessOptions options, IReadOnlyList<IPlugin> existing)
    {
        var selected = new HashSet<string>(options.BuiltinPlugins ?? BuiltinPluginNames, StringComparer.Ordinal);
        foreach (var name in selected)
            if (!BuiltinPluginNames.Contains(name, StringComparer.Ordinal))
                throw new ArgumentException($"Unknown built-in plugin '{name}'. Available: {string.Join(", ", BuiltinPluginNames)}.", nameof(options));
        if (selected.Contains("todos") && !existing.Any(plugin => plugin is Todos)) yield return new Todos();
        if (selected.Contains("environment") && !existing.Any(plugin => plugin is EnvironmentContext)) yield return new EnvironmentContext(options.Sandbox);
    }

    private static async Task<AgentSkills?> SelectSkillsAsync(HarnessSkillsOptions options, IReadOnlyList<IPlugin> existing, IReadOnlyFileSystem fileSystem, CancellationToken cancellationToken)
    {
        if (existing.Any(plugin => plugin is AgentSkills) || !options.Enabled) return null;
        if (options.Plugin is not null) return options.Plugin;
        if (options.Sources is not null) return options.Sources.Count == 0 ? null : new AgentSkills(new() { Skills = options.Sources });
        const string defaultDirectory = "./.agent/skills";
        try { await fileSystem.ListFilesAsync(defaultDirectory, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
        cancellationToken.ThrowIfCancellationRequested();
        return new AgentSkills(new() { Skills = new SkillSource[] { defaultDirectory } });
    }

    private static MemoryManager? SelectMemory(HarnessMemoryOptions options, IReadOnlyFileSystem fileSystem, Func<IModel> auxiliaryModel)
    {
        if (options.Manager is not null) return options.Manager;
        if (!options.Enabled) return null;
        Guard.NotNull(options.Stores, nameof(options.Stores));
        var stores = options.Stores.ToArray();
        if (stores.Length == 0)
        {
            Guard.NotBlank(options.Directory, nameof(options.Directory));
            var storage = options.Storage?.Namespace("") ?? (fileSystem is IFileSystem bytes
                ? new SandboxStorage(bytes, options.Directory)
                : throw new NotSupportedException("Default memory requires the selected filesystem's IFileSystem byte capabilities. Supply an explicit memory Storage, Stores, or Manager, or disable Memory."));
            stores = [new FileMemoryStore(new() { Name = "memory", Storage = storage, Extraction = new() { Model = auxiliaryModel() } })];
        }
        return new(new() { Stores = stores, Injection = new() { Trigger = MemoryInjectionTrigger.EveryTurn } });
    }

    private static ContextManager? SelectContext(HarnessContextOptions options) => options.Manager ?? (options.Enabled ? new(new() { Mode = options.Mode }) : null);

    private static IModel ResolveWebFetchModel(HarnessOptions options, ICollection<IAsyncDisposable> owned, Func<IModel> auxiliary)
    {
        if (options.WebFetch.Model is { } explicitModel) return explicitModel;
        if (options.WebFetch.ModelIdentifier is not { } identifier) return auxiliary();
        var resolved = ModelResolution.Identifier(options, identifier, HarnessEffort.Off, false, false, false, owned);
        ModelResolution.OwnAuxiliary(resolved, owned);
        return resolved.Model;
    }

    private static IEnumerable<ITool> SelectBuiltins(HarnessOptions options, Func<IModel> webFetchModel)
    {
        var selected = new HashSet<string>(options.BuiltinTools ?? BuiltinToolNames, StringComparer.Ordinal);
        foreach (var name in selected)
            if (!BuiltinToolNames.Contains(name, StringComparer.Ordinal) && name != "http_request")
                throw new ArgumentException($"Unavailable built-in tool '{name}'. Supported native tools: {string.Join(", ", BuiltinToolNames)}, http_request (explicit only).", nameof(options));
        if (selected.Contains("shell"))
        {
            Guard.NotNull(options.Shell, nameof(options.Shell));
            if (options.Sandbox is not ICommandSandbox commands) throw new ArgumentException("The shell built-in requires an ICommandSandbox. Disable shell or supply an explicitly command-capable sandbox.", nameof(options));
            if (commands is LocalFileSandbox { SupportsHostCommands: false }) throw new PlatformNotSupportedException("The .NET Standard host shell supports Windows only. Disable shell or supply ICommandSandbox for this platform.");
            yield return Shell.Create(commands, options.Shell);
        }
        if (selected.Contains("read")) yield return FileTools.CreateRead(options.Sandbox, options.Read, options.AgentOptions?.Model ?? options.Model ?? options.ConfiguredModel?.Instance, options.ConfiguredModel?.Identifier, options.AgentOptions?.FileSystem ?? options.Sandbox as IReadOnlyFileSystem);
        if (selected.Contains("write")) yield return FileTools.CreateWrite(options.Sandbox);
        if (selected.Contains("edit")) yield return FileTools.CreateEdit(options.Sandbox);
        if (selected.Contains("web_fetch")) yield return Tools.WebFetch.Create(options.Sandbox, webFetchModel(), options.WebFetch);
        if (selected.Contains("http_request")) yield return Tools.HttpRequest.Create(options.HttpRequest);
        if (selected.Contains("web_search") && options.WebSearch.Provider == WebSearchProvider.Exa)
            yield return WebSearch.CreateExa(options.WebSearch.Exa);
        if (selected.Contains("programmatic_tool_caller")) yield return Strands.Agents.Monty.ProgrammaticToolCaller.Create(options.ProgrammaticToolCaller);
    }

    private static void CheckNameCollisions(IEnumerable<ToolSpec> tools)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            Guard.NotNull(tool, nameof(tools));
            if (!seen.Add(tool.Name.Replace('-', '_')))
                throw new ArgumentException($"Tool name '{tool.Name}' is registered more than once. Rename the tool or disable its built-in before replacing it.", nameof(tools));
        }
    }

    private static string SanitizeSessionId(string value)
    {
        Guard.NotBlank(value, nameof(value));
        var sanitized = new string(value.Trim().ToLowerInvariant().Select(character => Guard.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '-').ToArray());
        return sanitized.Length == 0 ? "default" : sanitized;
    }
}
