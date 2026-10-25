using System.Collections.ObjectModel;
using Strands.Agents;
using Strands.Agents.Mcp;

namespace Strands.Harness.Mcp;

internal sealed class McpProviderBindings
{
    private readonly IReadOnlyList<IToolProvider> _declarations;
    private readonly IReadOnlyDictionary<string, McpToolProvider> _named;
    private McpProviderBindings(IReadOnlyList<IToolProvider> declarations, IReadOnlyDictionary<string, McpToolProvider> named,
        IReadOnlyDictionary<string, IReadOnlyList<ITool>> servers, IReadOnlyCollection<string> remoteNames)
    { _declarations = declarations; _named = named; Servers = servers; RemoteNames = remoteNames; }

    internal IReadOnlyDictionary<string, IReadOnlyList<ITool>> Servers { get; }
    internal IReadOnlyCollection<string> RemoteNames { get; }

    internal static McpProviderBindings Bind(Agent agent, IReadOnlyList<IToolProvider> declarations)
    {
        var named = new Dictionary<string, McpToolProvider>(StringComparer.Ordinal);
        foreach (var provider in declarations.OfType<McpToolProvider>())
        {
            if (provider.Name is not { } name)
                HarnessLogging.Warning("An McpToolProvider in ToolProviders has no Name; it cannot be offered to subagents on mcp_servers.");
            else if (named.ContainsKey(name))
                HarnessLogging.Warning("Two McpToolProviders share the name {0}; only the first is offered to subagents on mcp_servers.", name);
            else named.Add(name, provider);
        }
        var groups = named.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ITool>)Array.AsReadOnly(agent.Tools.Where(tool =>
            ReferenceEquals(agent.RegisteredToolProvider(tool), pair.Value)).ToArray()), StringComparer.Ordinal);
        var remoteNames = agent.Tools.Where(tool => agent.RegisteredToolProvider(tool) is McpToolProvider).Select(tool => agent.RegisteredToolSpec(tool).Name).ToArray();
        return new(Array.AsReadOnly(declarations.ToArray()), new ReadOnlyDictionary<string, McpToolProvider>(named),
            new ReadOnlyDictionary<string, IReadOnlyList<ITool>>(groups), Array.AsReadOnly(remoteNames));
    }

    internal static (Func<ITool, string> Name, Func<ITool, ToolSpec, bool> Admit) CaptureProviderScope(Agent agent, IReadOnlyCollection<string> allowed)
    {
        var entries = agent.Tools.Where(tool => agent.RegisteredToolProvider(tool) is not null)
            .Select(tool => new KeyValuePair<ITool, string>(tool, agent.RegisteredToolSpec(tool).Name)).ToArray();
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        return (tool =>
        {
            foreach (var entry in entries) if (ReferenceEquals(entry.Key, tool)) return entry.Value;
            return tool.Spec.Name;
        }, (tool, spec) =>
        {
            if (!names.Contains(spec.Name))
                throw new InvalidOperationException($"A child provider returned a tool outside its selected scope: {spec.Name}.");
            foreach (var entry in entries)
                if (ReferenceEquals(entry.Key, tool) && entry.Value != spec.Name)
                    throw new InvalidOperationException($"A child provider changed the admitted tool name from {entry.Value} to {spec.Name}.");
            return true;
        }
        );
    }

    internal IReadOnlyList<IToolProvider> SelectDeclarations(IReadOnlyList<string>? selected)
    {
        var names = selected ?? _named.Keys.ToArray();
        var admitted = names.Distinct(StringComparer.Ordinal).Where(_named.ContainsKey).Select(name => (IToolProvider)_named[name]);
        return Array.AsReadOnly(_declarations.Where(provider => provider is not McpToolProvider).Concat(admitted).ToArray());
    }
}
