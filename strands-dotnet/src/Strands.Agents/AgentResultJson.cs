using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strands.Agents.Telemetry;

namespace Strands.Agents;

/// <summary>Projects results into a versioned native message envelope without local execution telemetry or caller state.</summary>
public static class AgentResultJson
{
    private static readonly JsonSerializerOptions MessageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new NullableJsonValueConverter() }
    };

    /// <summary>Creates detached safe JSON. This native envelope is not a foreign SDK message interchange format.</summary>
    /// <param name="result">The result to project.</param>
    /// <param name="profile">The source-derived safe-field policy.</param>
    /// <returns>A detached native result envelope.</returns>
    public static JsonElement ToJson(AgentResult result, AgentResultOutputProfile profile = AgentResultOutputProfile.TypeScript)
    {
        Guard.NotNull(result, nameof(result));
        ValidateProfile(profile);
        if (result.StopReason is null) throw new ArgumentException("A result stop reason must be a string.", nameof(result));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "strands.agent_result");
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("stopReason", result.StopReason);
            writer.WritePropertyName("message");
            JsonSerializer.Serialize(writer, result.Message, MessageJson);
            if (profile == AgentResultOutputProfile.TypeScript && result.StructuredOutput is { } structured)
            {
                writer.WritePropertyName("structuredOutput");
                structured.WriteTo(writer);
            }
            if (result.Checkpoint is { } checkpoint)
            {
                writer.WritePropertyName("checkpoint");
                JsonSerializer.Serialize(writer, checkpoint);
            }
            else if (profile == AgentResultOutputProfile.Python) writer.WriteNull("checkpoint");
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>Rehydrates a native safe envelope with fresh empty local metrics and caller state.</summary>
    /// <param name="payload">The native envelope.</param>
    /// <returns>A detached result without restored execution telemetry.</returns>
    public static AgentResult FromJson(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new JsonException("A native result envelope must be an object.");
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
            if (property.Name is "type" or "schemaVersion" or "stopReason" or "message" or "structuredOutput" or "checkpoint")
                if (!reserved.Add(property.Name)) throw new JsonException("A native result envelope repeats a reserved field.");
        var type = Required(payload, "type");
        var version = Required(payload, "schemaVersion");
        var stop = Required(payload, "stopReason");
        if (type.ValueKind != JsonValueKind.String || type.GetString() != "strands.agent_result" ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            stop.ValueKind != JsonValueKind.String)
            throw new JsonException("The native result type, schema version or stop reason is invalid.");
        var reason = stop.GetString()!;
        AgentCheckpoint? checkpoint = null;
        if (payload.TryGetProperty("checkpoint", out var checkpointData) && checkpointData.ValueKind != JsonValueKind.Null)
            checkpoint = checkpointData.Deserialize<AgentCheckpoint>() ?? throw new JsonException("A non-null native checkpoint marker is required.");
        var messageData = Required(payload, "message");
        Message? message;
        try
        {
            message = messageData.ValueKind == JsonValueKind.Null ? null : messageData.Deserialize<Message>(MessageJson)
                ?? throw new JsonException("A non-null native message is required.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new JsonException("The native result message is malformed.", error);
        }
        return new(reason, message, new(), 0, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal)))
        {
            Checkpoint = checkpoint,
            StructuredOutput = payload.TryGetProperty("structuredOutput", out var structured) ? structured.Clone() : null,
            Metrics = new AgentMetrics(0, new(), new(TimeSpan.Zero), [], new Dictionary<string, ToolMetrics>(StringComparer.Ordinal), null, null, TimeSpan.Zero)
        };
    }

    // Only this safe-envelope serializer distinguishes an absent nullable value from a present JSON null.
    private sealed class NullableJsonValueConverter : JsonConverter<JsonElement?>
    {
        public override bool HandleNull => true;

        public override JsonElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, JsonElement? value, JsonSerializerOptions options)
        {
            if (value is { } present) present.WriteTo(writer);
            else writer.WriteNullValue();
        }
    }

    private static JsonElement Required(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) ? value : throw new JsonException("The native result envelope is missing '" + name + "'.");

    internal static void ValidateProfile(AgentResultOutputProfile profile)
    {
        if (profile is not (AgentResultOutputProfile.TypeScript or AgentResultOutputProfile.Python))
            throw new ArgumentOutOfRangeException(nameof(profile));
    }
}
