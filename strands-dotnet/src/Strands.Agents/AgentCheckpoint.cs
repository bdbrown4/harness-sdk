using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents;

/// <summary>An experimental boundary at which an agent can pause.</summary>
public enum CheckpointPosition
{
    /// <summary>The model requested tools, which have not run.</summary>
    AfterModel,
    /// <summary>The admitted tool batch has completed.</summary>
    AfterTools
}

/// <summary>An experimental pause marker. Conversation state is stored separately.</summary>
[JsonConverter(typeof(AgentCheckpointJsonConverter))]
public sealed record AgentCheckpoint
{
    /// <summary>The supported marker schema version.</summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>Creates a marker for a cycle boundary.</summary>
    /// <param name="position">The boundary at which execution paused.</param>
    /// <param name="cycleIndex">The zero-based cycle index.</param>
    public AgentCheckpoint(CheckpointPosition position, int cycleIndex = 0)
    {
        if (position is not (CheckpointPosition.AfterModel or CheckpointPosition.AfterTools))
            throw new ArgumentOutOfRangeException(nameof(position));
        if (cycleIndex < 0) throw new ArgumentOutOfRangeException(nameof(cycleIndex));
        Position = position;
        CycleIndex = cycleIndex;
    }

    /// <summary>The boundary at which execution paused.</summary>
    public CheckpointPosition Position { get; }

    /// <summary>The zero-based cycle index.</summary>
    public int CycleIndex { get; }

    /// <summary>The marker schema version.</summary>
    public string SchemaVersion => CurrentSchemaVersion;
}

internal sealed class AgentCheckpointJsonConverter : JsonConverter<AgentCheckpoint>
{
    public AgentCheckpointJsonConverter() { }

    public override AgentCheckpoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A checkpoint marker must be an object.");
        if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != AgentCheckpoint.CurrentSchemaVersion)
            throw new JsonException("Checkpoint schema version 1.0 is required.");
        if (!root.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.String)
            throw new JsonException("A checkpoint position is required.");
        var boundary = position.GetString() switch
        {
            "afterModel" => CheckpointPosition.AfterModel,
            "afterTools" => CheckpointPosition.AfterTools,
            _ => throw new JsonException("The checkpoint position is unsupported.")
        };
        var cycleIndex = 0;
        if (root.TryGetProperty("cycleIndex", out var cycle) &&
            (cycle.ValueKind != JsonValueKind.Number || !cycle.TryGetInt32(out cycleIndex) || cycleIndex < 0))
            throw new JsonException("A checkpoint cycle index must be a nonnegative Int32.");
        return new(boundary, cycleIndex);
    }

    public override void Write(Utf8JsonWriter writer, AgentCheckpoint value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("position", value.Position == CheckpointPosition.AfterModel ? "afterModel" : "afterTools");
        writer.WriteNumber("cycleIndex", value.CycleIndex);
        writer.WriteString("schemaVersion", AgentCheckpoint.CurrentSchemaVersion);
        writer.WriteEndObject();
    }
}
