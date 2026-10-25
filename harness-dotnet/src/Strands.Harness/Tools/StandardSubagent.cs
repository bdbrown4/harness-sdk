using Strands.Agents;
using Strands.Agents.Context;
using Strands.Harness.Mcp;
using Strands.Harness.Models;

namespace Strands.Harness.Tools;

internal static class StandardSubagent
{
    internal static ITool Create(IReadOnlyList<string> inheritedTools, SubagentConfig config, HarnessOptions modelOptions, McpProviderBindings providerBindings, bool inheritMemory = true, ContextMode? contextMode = null)
    {
        var servers = providerBindings.Servers;
        var remoteNames = new HashSet<string>(providerBindings.RemoteNames, StringComparer.Ordinal);
        ITool? delegation = null;
        delegation = Subagent.Create(new()
        {
            Presets = new Dictionary<string, Preset> { ["generalist"] = Subagent.Generalist },
            InheritedTools = inheritedTools.Where(name => !remoteNames.Contains(name) && (name != "web_search" || modelOptions.WebSearch.Provider != WebSearchProvider.Exa)).ToArray(),
            InheritedMcpServers = servers,
            MaxDepth = config.MaxDepth,
            Builder = async (spec, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordinary = new HashSet<string>(spec.Tools ?? context.Agent.Tools.Select(tool => context.Agent.RegisteredToolSpec(tool).Name).Where(name => !remoteNames.Contains(name)).ToArray(), StringComparer.Ordinal);
                var selectedServers = servers.Where(pair => spec.McpServers is null || spec.McpServers.Contains(pair.Key, StringComparer.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var allowed = new HashSet<string>(ordinary, StringComparer.Ordinal);
                allowed.UnionWith(selectedServers.Values.SelectMany(group => group).Select(tool => context.Agent.RegisteredToolSpec(tool).Name));
                var childProviders = providerBindings.SelectDeclarations(spec.McpServers);
                var independentSearch = modelOptions.WebSearch.Provider == WebSearchProvider.Exa && context.Agent.Tools.Any(tool => context.Agent.RegisteredToolSpec(tool).Name == "web_search");
                if (independentSearch) allowed.Add("web_search");
                var childProviderScope = McpProviderBindings.CaptureProviderScope(context.Agent, allowed);
                var search = independentSearch || modelOptions.ConfiguredNativeWebSearch;
                var inheritedIdentifier = modelOptions.ConfiguredModel is { } configured && ReferenceEquals(configured.Instance, context.Agent.Model) ? configured.Identifier : null;
                var identifier = spec.Model as string ?? inheritedIdentifier;
                var childModelOptions = modelOptions with
                {
                    AgentOptions = null,
                    ConfiguredModel = null,
                    ConfiguredModelFeaturesApplied = false,
                    Model = spec.Model as IModel ?? (identifier is null ? context.Agent.Model : null),
                    ModelId = identifier,
                    BuiltinTools = search ? new[] { "web_search" } : Array.Empty<string>(),
                    WebSearchExplicit = modelOptions.WebSearchExplicit ?? modelOptions.BuiltinTools is not null,
                };
                var resources = new List<IAsyncDisposable>();
                Agent? child = null;
                AgentOptions? childCore = null;
                try
                {
                    var resolved = ModelResolution.Main(childModelOptions, resources);
                    childModelOptions = childModelOptions with
                    {
                        ConfiguredModel = resolved.Identifier is { } name ? (name, resolved.Model) : null,
                        ConfiguredModelFeaturesApplied = true,
                        ConfiguredNativeWebSearch = resolved.NativeWebSearch,
                    };
                    child = context.Agent.CreateChild(options =>
                    {
                        var inheritedFilter = options.ToolFilter;
                        var inheritedSpecFilter = options.ProviderSpecFilter;
                        childCore = options with
                        {
                            CheckpointBehavior = CheckpointBehavior.Disabled,
                            Model = resolved.Model,
                            OwnsModel = resolved.Owned,
                            OwnedResources = resources.ToArray(),
                            Name = spec.AgentType ?? "subagent",
                            Description = "A delegated subtask",
                            SystemPrompt = spec.Instructions is null ? options.SystemPrompt : HarnessPrompt.Build(spec.Instructions),
                            Messages = spec.Messages,
                            Tools = options.Tools.Where(tool => !ReferenceEquals(tool, delegation) && !remoteNames.Contains(context.Agent.RegisteredToolSpec(tool).Name) &&
                                (ordinary.Contains(context.Agent.RegisteredToolSpec(tool).Name) || independentSearch && context.Agent.RegisteredToolSpec(tool).Name == "web_search"))
                                .Select(tool => ReadFileTool.ForModel(WebFetch.CreateForAgent(tool), resolved.Model, resolved.Identifier)).ToArray(),
                            ToolProviders = childProviders,
                            InitializationTools = options.Tools.Any(tool => ReferenceEquals(tool, delegation)) && ordinary.Contains("subagent")
                                ? initialized => new[] { Create(initialized.Tools.Select(tool => initialized.RegisteredToolSpec(tool).Name).Concat(new[] { "subagent" }).ToArray(),
                                    config, childModelOptions, McpProviderBindings.Bind(initialized, childProviders), inheritMemory, contextMode) }
                                : null,
                            Plugins = options.Plugins.Where(plugin => plugin.Name is not ("strands:memory-manager" or "strands:context-manager")).ToArray(),
                            MemoryManager = inheritMemory ? context.Agent.MemoryManager?.ToReadOnly() : null,
                            ContextManager = contextMode is { } mode ? new(new() { Mode = mode }) : null,
                            ToolFilter = tool => (inheritedFilter?.Invoke(tool) ?? true) && allowed.Contains(childProviderScope.Name(tool)),
                            ProviderSpecFilter = (tool, captured) => (inheritedSpecFilter?.Invoke(tool, captured) ?? true) && childProviderScope.Admit(tool, captured),
                        };
                        return childCore;
                    });
                    await child.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    return child;
                }
                catch (Exception failure)
                {
                    await Harness.CleanupFailedConstructionAsync(child, childCore, resources, failure).ConfigureAwait(false);
                    throw;
                }
            },
        }, modelOptions.WebSearch.Provider == WebSearchProvider.Exa ? new[] { "web_search" } : Array.Empty<string>());
        return delegation;
    }
}
