using System.Collections.ObjectModel;
using System.Text.Json;

namespace Strands.Agents.MultiAgent;

/// <summary>Detached JSON knowledge contributed by named swarm agents.</summary>
public sealed class SharedContext
{
    private readonly object _gate = new();
    private Dictionary<string, StateStore> _nodes = new(StringComparer.Ordinal);
    /// <summary>Stores one JSON value under its contributing node and a nonblank key.</summary>
    public void AddContext(string nodeId, string key, JsonElement value)
    {
        Guard.NotNullOrWhiteSpace(nodeId, nameof(nodeId)); Guard.NotNullOrWhiteSpace(key, nameof(key));
        if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Shared context requires a JSON value.", nameof(value));
        lock (_gate)
        {
            if (!_nodes.TryGetValue(nodeId, out var state)) _nodes.Add(nodeId, state = new());
            state.Set(key, value);
        }
    }
    /// <summary>Returns an independent read-only view of every contributing node's JSON values.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> GetAll()
    { lock (_gate) return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>(_nodes.ToDictionary(pair => pair.Key, pair => pair.Value.GetAll(), StringComparer.Ordinal)); }
    internal void Replace(IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> values)
    {
        var copy = new Dictionary<string, StateStore>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            Guard.NotNullOrWhiteSpace(pair.Key, nameof(values));
            Guard.NotNull(pair.Value, nameof(values));
            var state = new StateStore();
            foreach (var item in pair.Value)
            {
                Guard.NotNullOrWhiteSpace(item.Key, nameof(values));
                state.Set(item.Key, item.Value);
            }
            copy.Add(pair.Key, state);
        }
        lock (_gate) _nodes = copy;
    }
}
