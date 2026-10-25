using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents;

/// <summary>The implemented fields available for explicit in-memory checkpoints.</summary>
[Flags]
public enum SnapshotFields
{
    /// <summary>No fields selected.</summary>
    None = 0,
    /// <summary>Conversation messages and native server-history acknowledgement metadata. Active input receipts also require provider state.</summary>
    Messages = 1,
    /// <summary>Application JSON state.</summary>
    State = 2,
    /// <summary>The optional system prompt, including an explicit absent prompt.</summary>
    SystemPrompt = 4,
    /// <summary>Provider conversation state.</summary>
    ModelState = 8,
    /// <summary>Pending human input, accepted responses and completed tool outputs retained for replay.</summary>
    InterruptState = 16,
    /// <summary>Classic conversation-manager discriminator, removal count, cadence and optional previous summary.</summary>
    ConversationManagerState = 32,
    /// <summary>All currently supported checkpoint fields.</summary>
    All = Messages | State | SystemPrompt | ModelState | InterruptState | ConversationManagerState
}

/// <summary>A named starting selection for checkpoint fields.</summary>
public enum SnapshotPreset
{
    /// <summary>All currently supported checkpoint fields.</summary>
    Session
}

/// <summary>Selects fields by applying preset, include and exclude in that order.</summary>
public sealed record SnapshotOptions
{
    /// <summary>An optional named starting selection.</summary>
    public SnapshotPreset? Preset { get; init; }
    /// <summary>Additional fields to capture.</summary>
    public SnapshotFields Include { get; init; }
    /// <summary>Fields removed after preset and include are applied.</summary>
    public SnapshotFields Exclude { get; init; }
    /// <summary>Application-owned JSON copied into the checkpoint and never applied to the agent.</summary>
    public IReadOnlyDictionary<string, JsonElement> AppData { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>An immutable, JSON-serializable in-memory checkpoint. The native message representation is independent of foreign SDK snapshot schemas.</summary>
public sealed class AgentSnapshot
{
    /// <summary>Copies a checkpoint envelope and all JSON values.</summary>
    /// <param name="data">The selected framework-owned fields.</param>
    /// <param name="appData">Application metadata, ignored during restoration.</param>
    /// <param name="scope">The checkpoint scope; restoration requires agent.</param>
    /// <param name="schemaVersion">The checkpoint schema; restoration supports 1.0 or the complete input-receipt pair in 2.0.</param>
    /// <param name="createdAt">The capture timestamp, or default to record the current UTC time.</param>
    [JsonConstructor]
    public AgentSnapshot(IReadOnlyDictionary<string, JsonElement> data, IReadOnlyDictionary<string, JsonElement>? appData = null,
        string scope = "agent", string schemaVersion = "1.0", DateTimeOffset createdAt = default)
    {
        Guard.NotNull(data, nameof(data));
        Guard.NotNull(scope, nameof(scope));
        Guard.NotNull(schemaVersion, nameof(schemaVersion));
        Data = CloneValues(data, nameof(data));
        AppData = CloneValues(appData ?? new Dictionary<string, JsonElement>(), nameof(appData));
        Scope = scope;
        SchemaVersion = schemaVersion;
        CreatedAt = createdAt == default ? DateTimeOffset.UtcNow : createdAt;
    }

    /// <summary>The checkpoint scope.</summary>
    public string Scope { get; }
    /// <summary>The checkpoint format version.</summary>
    public string SchemaVersion { get; }
    /// <summary>The capture timestamp.</summary>
    public DateTimeOffset CreatedAt { get; }
    /// <summary>Detached framework-owned fields. Unknown fields are retained and ignored by this version's loader.</summary>
    public IReadOnlyDictionary<string, JsonElement> Data { get; }
    /// <summary>Detached application metadata, never interpreted or applied by the SDK.</summary>
    public IReadOnlyDictionary<string, JsonElement> AppData { get; }

    internal static IReadOnlyDictionary<string, JsonElement> CloneValues(IReadOnlyDictionary<string, JsonElement> values, string parameterName)
    {
        try
        {
            var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var pair in values)
            {
                if (pair.Key is null || pair.Value.ValueKind == JsonValueKind.Undefined)
                    throw new ArgumentException("Checkpoint values require nonnull keys and defined JSON values.", parameterName);
                copy.Add(pair.Key, pair.Value.Clone());
            }
            return new ReadOnlyDictionary<string, JsonElement>(copy);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            throw new ArgumentException("Checkpoint values must contain valid JSON that can be detached from its producer.", parameterName, error);
        }
    }
}
