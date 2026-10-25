using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>A bounded, process-local configuration store with detached JSON values.</summary>
public sealed class InMemoryA2APushNotificationConfigStore : IA2APushNotificationConfigStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string TaskId, string ConfigurationId), (JsonElement Json, int Bytes)> _configurations = [];
    private readonly int _maximum;
    private readonly int _maximumBytes;
    private int _bytes;
    /// <summary>Sets total configuration and retained UTF-8 byte limits.</summary>
    public InMemoryA2APushNotificationConfigStore(int maxConfigurations = 1000, int maxBytes = 1024 * 1024)
    {
        if (maxConfigurations <= 0) throw new ArgumentOutOfRangeException(nameof(maxConfigurations));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maximum = maxConfigurations; _maximumBytes = maxBytes;
    }
    /// <inheritdoc />
    public ValueTask SetAsync(string taskId, JsonElement configuration, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId));
        A2AJson.RequireObject(configuration); A2AJson.ValidateUniqueProperties(configuration);
        var id = A2AJson.RequiredString(configuration, "id");
        var detached = configuration.Clone();
        var bytes = System.Text.Encoding.UTF8.GetByteCount(detached.GetRawText());
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (taskId, id);
            var replacing = _configurations.TryGetValue(key, out var prior);
            var nextBytes = (long)_bytes - prior.Bytes + bytes;
            if (!replacing && _configurations.Count >= _maximum || nextBytes > _maximumBytes)
                throw new InvalidOperationException("The push configuration store has reached its configured capacity.");
            _configurations[key] = (detached, bytes); _bytes = (int)nextBytes;
        }
        return default;
    }
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<JsonElement>> ListAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId));
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(_configurations.Where(pair => pair.Key.TaskId == taskId).OrderBy(pair => pair.Key.ConfigurationId, StringComparer.Ordinal).Select(pair => pair.Value.Json.Clone()).ToArray());
        }
    }
    /// <inheritdoc />
    public ValueTask DeleteAsync(string taskId, string configurationId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(taskId, nameof(taskId)); Guard.NotNullOrWhiteSpace(configurationId, nameof(configurationId));
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (taskId, configurationId);
            if (_configurations.TryGetValue(key, out var prior)) { _configurations.Remove(key); _bytes -= prior.Bytes; }
        }
        return default;
    }
}
