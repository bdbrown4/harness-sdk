using Strands.Agents.SchemaImplementation;

namespace Strands.Agents;

public sealed partial class Agent
{
    /// <summary>Replaces an existing application registration without changing its insertion position. Plugin/provider tools cannot be replaced by this API.</summary>
    public void ReplaceApplicationTool(ITool tool) => MutateApplicationTools([tool], ApplicationToolMutation.Replace);
    /// <summary>Adds or replaces an application registration while this agent is inactive and has no pending interrupts.</summary>
    public void AddOrReplaceApplicationTool(ITool tool) => AddOrReplaceApplicationTools([tool]);
    /// <summary>Validates and publishes a complete application tool batch atomically; plugin/provider registrations cannot be overwritten.</summary>
    public void AddOrReplaceApplicationTools(IReadOnlyList<ITool> tools) => MutateApplicationTools(tools, ApplicationToolMutation.AddOrReplace);
    /// <summary>Removes one exact application registration. Returns false for a missing or plugin/provider registration.</summary>
    public bool RemoveApplicationTool(string name)
    {
        Guard.NotNull(name, nameof(name));
        lock (_initializationGate)
        {
            ValidateToolMutation();
            if (!_tools.TryGetValue(name, out var registered) || !IsApplicationTool(registered.Tool)) return false;
            var next = OrderedRegistrations(); next.RemoveAll(value => value.Key == name);
            var applications = _applicationTools.Where(value => !ReferenceEquals(value, registered.Tool)).ToList();
            CommitToolRegistrations(next, applications);
            return true;
        }
    }
    /// <summary>Clears application registrations while preserving plugin/provider tools and borrowed tool lifetimes.</summary>
    public void ClearApplicationTools()
    {
        lock (_initializationGate)
        {
            ValidateToolMutation();
            CommitToolRegistrations(OrderedRegistrations().Where(value => !IsApplicationTool(value.Value.Tool)).ToList(), []);
        }
    }

    private void ValidateToolMutation()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _invoking) != 0 || _directToolCalls != 0 || _abortingInterrupts || _classicReductions != 0 || _catalogOperation is not null || _initialization is { IsCompleted: false })
            throw new InvalidOperationException("Tools can only be changed while the agent is inactive and initialization is not running.");
        if (InterruptState is not null) throw new InvalidOperationException("Tool bindings cannot be changed while interrupts are pending.");
    }

    private void MutateApplicationTools(IReadOnlyList<ITool> tools, ApplicationToolMutation mutation)
    {
        Guard.NotNull(tools, nameof(tools));
        lock (_initializationGate) ValidateToolMutation();
        var pending = new List<KeyValuePair<string, (ITool Tool, JsonSchema Schema)>>();
        // Filters/getters are application code. Revalidate admission and the latest registrations after preparing them.
        foreach (var tool in tools.ToArray())
        {
            Guard.NotNull(tool, nameof(tools));
            if (_options.ToolFilter?.Invoke(tool) == false) continue;
            var spec = tool.Spec ?? throw new ArgumentException("A registered tool must have a specification.", nameof(tools));
            pending.Add(new(spec.Name, (tool, ToolSchema.Compile(spec.InputSchema))));
        }
        ValidateDelegationRegistration(pending.Select(value => value.Value.Tool));
        _backgroundTasks?.Policy.Validate(pending.Select(value => value.Value.Tool).ToArray());
        lock (_initializationGate)
        {
            ValidateToolMutation();
            var next = OrderedRegistrations();
            var applications = _applicationTools.ToList();
            foreach (var entry in pending)
            {
                var index = next.FindIndex(value => value.Key == entry.Key);
                if (index >= 0)
                {
                    if (mutation == ApplicationToolMutation.Add) throw new ArgumentException($"Duplicate tool name: {entry.Key}.", nameof(tools));
                    var applicationIndex = applications.FindIndex(value => ReferenceEquals(value, next[index].Value.Tool));
                    if (applicationIndex < 0) throw new InvalidOperationException($"Tool '{entry.Key}' belongs to a plugin or provider and cannot be replaced as an application tool.");
                    applications[applicationIndex] = entry.Value.Tool; next[index] = entry;
                }
                else
                {
                    if (mutation == ApplicationToolMutation.Replace) throw new KeyNotFoundException($"Application tool '{entry.Key}' not found.");
                    ValidateNormalizedToolName(entry.Key, next.Select(value => value.Key));
                    applications.Add(entry.Value.Tool); next.Add(entry);
                }
            }
            CommitToolRegistrations(next, applications);
            MarkSandboxToolsAsApplicationOwned(pending.Select(value => value.Value.Tool));
        }
    }

    private bool IsApplicationTool(ITool tool) => _applicationTools.Any(value => ReferenceEquals(value, tool));

    private List<KeyValuePair<string, (ITool Tool, JsonSchema Schema)>> OrderedRegistrations()
    {
        // The published snapshot is the portable insertion-order authority, including on Framework.
        return Tools.Select(tool => _tools.First(value => ReferenceEquals(value.Value.Tool, tool))).ToList();
    }

    private void CommitToolRegistrations(List<KeyValuePair<string, (ITool Tool, JsonSchema Schema)>> next, List<ITool> applications)
    {
        _tools.Clear();
        foreach (var entry in next) _tools.Add(entry.Key, entry.Value);
        _applicationTools.Clear(); _applicationTools.AddRange(applications);
        PruneSandboxToolOrigins(next.Select(value => value.Value.Tool));
        Volatile.Write(ref _toolSnapshot, Array.AsReadOnly(next.Select(value => value.Value.Tool).ToArray()));
    }

    private void ValidateNormalizedToolName(string name, IEnumerable<string> existing, bool initialization = false)
    {
        if (_options.ToolNameConflictPolicy != ToolNameConflictPolicy.RejectNormalized) return;
        var normalized = name.Replace('-', '_');
        if (!existing.Any(value => value != name && value.Replace('-', '_') == normalized)) return;
        var message = $"Tool name '{name}' conflicts with an existing hyphen/underscore alias.";
        if (initialization) throw new InvalidOperationException(message);
        throw new ArgumentException(message);
    }

    private string ResolvePublicDirectToolName(string name)
    {
        if (_options.DirectToolNameResolution == ToolNameResolution.Exact) return name;
        if (Volatile.Read(ref _providerRegistrations).Count > 0)
        {
            var names = Tools.Select(RegisteredToolSpec).Select(spec => spec.Name).ToArray();
            if (names.Contains(name, StringComparer.Ordinal)) return name;
            foreach (var registered in names) if (registered.Replace('-', '_') == name) return registered;
            if (_options.DirectToolNameResolution == ToolNameResolution.TypeScriptDirect)
                foreach (var registered in names) if (StringComparer.OrdinalIgnoreCase.Equals(registered, name)) return registered;
            return name;
        }
        var registry = new ToolRegistry(Tools, new() { NameConflictPolicy = ToolNameConflictPolicy.ExactOnly });
        try { return registry.Resolve(name, _options.DirectToolNameResolution).Spec.Name; }
        catch (KeyNotFoundException) { return name; }
    }

    private enum ApplicationToolMutation { Add, Replace, AddOrReplace }
}
