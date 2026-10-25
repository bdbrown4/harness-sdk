using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>Stores authorized A2A push configurations separately from task snapshots. Implementations detach inputs and results and remain caller-owned.</summary>
public interface IA2APushNotificationConfigStore
{
    /// <summary>Atomically inserts or replaces one configuration by its nonempty id within a task.</summary>
    ValueTask SetAsync(string taskId, JsonElement configuration, CancellationToken cancellationToken = default);
    /// <summary>Returns detached configurations for one task, or an empty list.</summary>
    ValueTask<IReadOnlyList<JsonElement>> ListAsync(string taskId, CancellationToken cancellationToken = default);
    /// <summary>Removes the identified configuration. A missing configuration is a no-op.</summary>
    ValueTask DeleteAsync(string taskId, string configurationId, CancellationToken cancellationToken = default);
}
