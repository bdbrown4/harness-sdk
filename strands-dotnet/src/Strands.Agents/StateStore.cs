using System.Collections.ObjectModel;
using System.Text.Json;

namespace Strands.Agents;

/// <summary>Thread-safe application state, copied on every read and write and excluded from model context.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);
    private long _version;
    internal long Version { get { lock (_gate) return _version; } }

    /// <summary>Creates an empty state store or copies JSON values from an initial snapshot.</summary>
    public StateStore(IReadOnlyDictionary<string, JsonElement>? initialState = null)
    {
        if (initialState is not null) Replace(initialState);
    }

    /// <summary>Returns a detached JSON value, or null when the key is absent.</summary>
    public JsonElement? Get(string key)
    {
        Guard.NotNull(key, nameof(key));
        lock (_gate) return _values.TryGetValue(key, out var value) ? value.Clone() : null;
    }

    /// <summary>Deserializes a detached application value; an absent key returns the type's default value.</summary>
    public T? Get<T>(string key) => Get(key) is { } value ? value.Deserialize<T>(SerializerOptions) : default;

    /// <summary>Serializes and copies a value before replacing the key. Failed serialization leaves existing state intact.</summary>
    public void Set<T>(string key, T value)
    {
        Guard.NotNull(key, nameof(key));
        var copy = JsonSerializer.SerializeToElement(value, SerializerOptions);
        lock (_gate) { _values[key] = copy; _version++; }
    }

    /// <summary>Removes the key and reports whether it existed.</summary>
    public bool Delete(string key)
    {
        Guard.NotNull(key, nameof(key));
        lock (_gate)
        {
            var removed = _values.Remove(key);
            if (removed) _version++;
            return removed;
        }
    }

    /// <summary>Removes all application state.</summary>
    public void Clear() { lock (_gate) { if (_values.Count == 0) return; _values.Clear(); _version++; } }

    /// <summary>Returns a read-only detached snapshot of the state keys. Ordering is unspecified.</summary>
    public IReadOnlyList<string> Keys()
    {
        lock (_gate) return new ReadOnlyCollection<string>(_values.Keys.ToArray());
    }

    /// <summary>Returns a read-only detached snapshot of all values.</summary>
    public IReadOnlyDictionary<string, JsonElement> GetAll()
    {
        return GetSnapshot(out _);
    }

    internal IReadOnlyDictionary<string, JsonElement> GetSnapshot(out long version)
    {
        lock (_gate)
        {
            version = _version;
            return new ReadOnlyDictionary<string, JsonElement>(_values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));
        }
    }

    internal void Replace(IReadOnlyDictionary<string, JsonElement> values)
    {
        var copy = values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        lock (_gate) { _values = copy; _version++; }
    }
}
