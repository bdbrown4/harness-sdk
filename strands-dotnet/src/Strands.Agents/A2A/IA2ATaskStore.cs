using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>Persists A2A task JSON separately from native agent conversation and interruption state.</summary>
/// <remarks>Implementations detach inputs and returned values. A successful save replaces one task atomically according to the backend's guarantees. Supplied stores remain caller-owned.</remarks>
public interface IA2ATaskStore
{
    /// <summary>Persists a detached task, replacing an existing record with the same task identifier.</summary>
    ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default);
    /// <summary>Returns a detached task, or null when its identifier is missing.</summary>
    ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default);
}
