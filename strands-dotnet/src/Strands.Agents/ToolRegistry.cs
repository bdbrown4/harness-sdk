namespace Strands.Agents;

/// <summary>The direct-call name lookup policy; model-issued calls remain exact.</summary>
public enum ToolNameResolution
{
    /// <summary>Only an ordinal exact name matches.</summary>
    Exact,
    /// <summary>Exact names, followed by underscore aliases for hyphenated names, as in Python.</summary>
    UnderscoreAlias,
    /// <summary>Exact names, underscore aliases, then the first insertion-order case-insensitive match.</summary>
    TypeScriptDirect
}

/// <summary>Registration conflict policy for hyphen/underscore aliases.</summary>
public enum ToolNameConflictPolicy
{
    /// <summary>Reject only exact duplicates; permits ambiguous aliases for native compatibility.</summary>
    ExactOnly,
    /// <summary>Also reject case-sensitive names differing only by hyphens versus underscores.</summary>
    RejectNormalized
}

/// <summary>Immutable registration policy copied when a registry is constructed.</summary>
public sealed record ToolRegistryOptions
{
    /// <summary>Both pinned upstream registries reject normalized conflicts by default.</summary>
    public ToolNameConflictPolicy NameConflictPolicy { get; init; } = ToolNameConflictPolicy.RejectNormalized;
}

/// <summary>A standalone insertion-ordered registry of borrowed tools. Mutations never dispose tools.</summary>
public sealed class ToolRegistry
{
    private readonly object _gate = new();
    private readonly ToolNameConflictPolicy _conflicts;
    private List<Registration> _registrations = [];

    /// <summary>Creates a registry with optional initial tools and an immutable conflict policy.</summary>
    public ToolRegistry(IEnumerable<ITool>? tools = null, ToolRegistryOptions? options = null)
    {
        _conflicts = options?.NameConflictPolicy ?? ToolNameConflictPolicy.RejectNormalized;
        if (!Enum.IsDefined(typeof(ToolNameConflictPolicy), _conflicts)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown tool-name conflict policy.");
        if (tools is not null) AddRange(tools);
    }
    /// <summary>A detached, read-only membership snapshot in insertion order; tool instances are borrowed.</summary>
    public IReadOnlyList<ITool> Items { get { lock (_gate) return Array.AsReadOnly(_registrations.Select(value => value.Tool).ToArray()); } }
    /// <summary>The conflict policy copied at construction.</summary>
    public ToolNameConflictPolicy NameConflictPolicy => _conflicts;
    /// <summary>Adds a tool; duplicate names fail without mutation.</summary>
    public void Add(ITool tool) => AddRange([tool]);
    /// <summary>Adds an entire batch atomically, including enumeration and specification failures.</summary>
    public void AddRange(IEnumerable<ITool> tools) => Apply(tools, replace: false);
    /// <summary>Adds or replaces a tool, preserving the insertion position of an existing name.</summary>
    public void AddOrReplace(ITool tool) => AddOrReplace([tool]);
    /// <summary>Adds or replaces a complete batch atomically; repeated exact names use the last tool.</summary>
    public void AddOrReplace(IEnumerable<ITool> tools) => Apply(tools, replace: true);
    /// <summary>Replaces an existing exact name in place; a missing name throws <see cref="KeyNotFoundException"/>.</summary>
    public void Replace(ITool tool)
    {
        var replacement = Capture(tool);
        lock (_gate)
        {
            var index = Find(_registrations, replacement.Name);
            if (index < 0) throw Missing(replacement.Name);
            _registrations[index] = replacement;
        }
    }
    /// <summary>Retrieves an exact name, or null when missing.</summary>
    public ITool? Get(string name) => TryGet(name, out var tool) ? tool : null;
    /// <summary>Retrieves an exact name without alias or case fallback.</summary>
    public bool TryGet(string name, out ITool? tool)
    {
        Guard.NotNull(name, nameof(name));
        lock (_gate)
        {
            var index = Find(_registrations, name);
            tool = index < 0 ? null : _registrations[index].Tool;
            return index >= 0;
        }
    }
    /// <summary>Resolves a direct-call name under an explicit policy, or throws <see cref="KeyNotFoundException"/>.</summary>
    public ITool Resolve(string name, ToolNameResolution resolution = ToolNameResolution.Exact)
    {
        Guard.NotNull(name, nameof(name));
        if (!Enum.IsDefined(typeof(ToolNameResolution), resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        lock (_gate)
        {
            var exact = Find(_registrations, name);
            if (exact >= 0) return _registrations[exact].Tool;
            if (resolution != ToolNameResolution.Exact && name.IndexOf('_') >= 0)
                foreach (var entry in _registrations)
                    if (Normalize(entry.Name) == name) return entry.Tool;
            if (resolution == ToolNameResolution.TypeScriptDirect)
            {
                var lower = name.ToLowerInvariant();
                foreach (var entry in _registrations)
                    if (entry.Name.ToLowerInvariant() == lower) return entry.Tool;
            }
            throw Missing(name);
        }
    }
    /// <summary>Removes only an exact name; returns false when it was missing.</summary>
    public bool Remove(string name)
    {
        Guard.NotNull(name, nameof(name));
        lock (_gate)
        {
            var index = Find(_registrations, name);
            if (index < 0) return false;
            _registrations.RemoveAt(index);
            return true;
        }
    }
    /// <summary>Clears registrations without disposing borrowed tools.</summary>
    public void Clear() { lock (_gate) _registrations.Clear(); }

    private void Apply(IEnumerable<ITool> tools, bool replace)
    {
        Guard.NotNull(tools, nameof(tools));
        // Enumerators and custom Spec getters are application code: run them before taking the publication lock.
        var pending = tools.Select(Capture).ToArray();
        lock (_gate)
        {
            var next = new List<Registration>(_registrations);
            foreach (var entry in pending)
            {
                var index = Find(next, entry.Name);
                if (index >= 0)
                {
                    if (!replace) throw new ArgumentException($"Duplicate tool name: {entry.Name}.", nameof(tools));
                    next[index] = entry;
                    continue;
                }
                if (_conflicts == ToolNameConflictPolicy.RejectNormalized)
                {
                    var normalized = Normalize(entry.Name);
                    if (next.Any(value => Normalize(value.Name) == normalized))
                        throw new ArgumentException($"Tool name '{entry.Name}' conflicts with a hyphen/underscore alias already registered.", nameof(tools));
                }
                next.Add(entry);
            }
            _registrations = next;
        }
    }

    private static Registration Capture(ITool tool)
    {
        Guard.NotNull(tool, nameof(tool));
        var spec = tool.Spec ?? throw new ArgumentException("A tool specification cannot be null.", nameof(tool));
        return new(spec.Name, tool);
    }
    private static int Find(List<Registration> items, string name) => items.FindIndex(value => value.Name == name);
    private static string Normalize(string name) => name.Replace('-', '_');
    private static KeyNotFoundException Missing(string name) => new($"Tool '{name}' not found.");
    private sealed record Registration(string Name, ITool Tool);
}
