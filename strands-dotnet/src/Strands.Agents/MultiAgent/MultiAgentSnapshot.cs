using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.MultiAgent;

/// <summary>Application metadata supplied when taking an orchestration snapshot.</summary>
public sealed record MultiAgentSnapshotOptions
{
    /// <summary>Application-owned JSON copied into the envelope and ignored during restoration.</summary>
    public IReadOnlyDictionary<string, JsonElement> AppData { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>An immutable native orchestration checkpoint. Its schema is independent of foreign SDK snapshot formats.</summary>
public sealed class MultiAgentSnapshot
{
    /// <summary>Copies an orchestration envelope and detaches all JSON from its producer.</summary>
    [JsonConstructor]
    public MultiAgentSnapshot(IReadOnlyDictionary<string, JsonElement> data, IReadOnlyDictionary<string, JsonElement>? appData = null,
        string scope = "multiAgent", string schemaVersion = "1.0", DateTimeOffset createdAt = default)
    {
        Guard.NotNull(data, nameof(data)); Guard.NotNull(scope, nameof(scope)); Guard.NotNull(schemaVersion, nameof(schemaVersion));
        Data = AgentSnapshot.CloneValues(data, nameof(data)); AppData = AgentSnapshot.CloneValues(appData ?? new Dictionary<string, JsonElement>(), nameof(appData));
        Scope = scope; SchemaVersion = schemaVersion; CreatedAt = createdAt == default ? DateTimeOffset.UtcNow : createdAt;
    }
    /// <summary>The checkpoint scope, multiAgent for native orchestration.</summary>
    public string Scope { get; }
    /// <summary>The native format version.</summary>
    public string SchemaVersion { get; }
    /// <summary>The UTC checkpoint capture time.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>Detached framework-owned state. Unknown fields are preserved and ignored by this version.</summary>
    public IReadOnlyDictionary<string, JsonElement> Data { get; }
    /// <summary>Detached application metadata, ignored by the framework.</summary>
    public IReadOnlyDictionary<string, JsonElement> AppData { get; }
}

/// <summary>An optional native checkpoint capability for a nested orchestrator.</summary>
public interface ICheckpointableMultiAgent : IMultiAgent
{
    /// <summary>Validates detached checkpoint structure and identity without I/O or state mutation.</summary>
    void ValidateSnapshot(MultiAgentSnapshot snapshot);
    /// <summary>Captures an inactive orchestrator without transferring dependency ownership.</summary>
    ValueTask<MultiAgentSnapshot> TakeSnapshotAsync(MultiAgentSnapshotOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Validates a checkpoint and replaces inactive orchestration state without executing nodes.</summary>
    ValueTask LoadSnapshotAsync(MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default);
}
