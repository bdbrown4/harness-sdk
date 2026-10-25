using System.Text.Encodings.Web;
using System.Text.Json;

namespace Strands.Agents.Memory;

internal static class TestMemoryRecordCodec
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = int.MaxValue,
    };

    internal static IReadOnlyList<JsonElement> Read(byte[] bytes, string key, CancellationToken cancellationToken)
    {
        JsonDocument document;
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) json = json.Slice(3);
        try { document = JsonDocument.Parse(json, new() { MaxDepth = int.MaxValue }); }
        catch (JsonException error) { throw new InvalidDataException("TestMemoryStore: invalid JSON in " + key + ".", error); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("TestMemoryStore: invalid backing file " + key + ": expected a JSON array of records.");
            var records = new List<JsonElement>();
            foreach (var record in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.ValueKind != JsonValueKind.Object || !StringField(record, "id") || !StringField(record, "content") || !StringField(record, "createdAt"))
                    throw new InvalidDataException("TestMemoryStore: invalid backing file " + key + ": each record must have string 'id', 'content', and 'createdAt' fields.");
                if (record.TryGetProperty("metadata", out var metadata) && metadata.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                    throw new InvalidDataException("TestMemoryStore: invalid backing file " + key + ": a record's 'metadata', when present, must be a JSON object.");
                records.Add(record.Clone());
            }
            return records;
        }
    }

    internal static byte[] Append(IReadOnlyList<JsonElement> records, string id, string content, string timestamp, JsonElement? metadata)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var record in records) record.WriteTo(writer);
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("content", content);
            writer.WriteString("createdAt", timestamp);
            if (metadata is { } value) { writer.WritePropertyName("metadata"); value.WriteTo(writer); }
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        return stream.ToArray();
    }

    internal static JsonElement ScoredMetadata(JsonElement record, int score)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            if (record.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                foreach (var property in metadata.EnumerateObject())
                    if (property.Name != "_relevanceScore") property.WriteTo(writer);
            writer.WriteNumber("_relevanceScore", score);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = int.MaxValue });
        return document.RootElement.Clone();
    }

    private static bool StringField(JsonElement record, string property)
        => record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String;
}
