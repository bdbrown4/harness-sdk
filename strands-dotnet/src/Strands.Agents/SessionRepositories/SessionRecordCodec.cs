using System.Text.Json;

namespace Strands.Agents.SessionRepositories;

internal static class SessionRecordCodec
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    internal static void ValidateJson(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicates(document.RootElement);
    }
    internal static JsonElement ReadEnvelope(byte[] bytes, string kind, string sessionId, string? ownerId)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("An indexed record envelope must be an object.");
            RejectDuplicates(root);
            var version = root.GetProperty("schemaVersion");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                root.GetProperty("kind").GetString() != kind || root.GetProperty("sessionId").GetString() != sessionId ||
                (ownerId is null ? root.GetProperty("ownerId").ValueKind != JsonValueKind.Null : root.GetProperty("ownerId").GetString() != ownerId))
                throw new InvalidDataException("The indexed record version, kind, or identity does not match its requested key.");
            var data = root.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Indexed record data must be an object.");
            return data.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw new InvalidDataException("The indexed record is malformed.", error); }
    }
    internal static SessionRecord ReadSession(JsonElement data) => Read(() => new SessionRecord(
        data.GetProperty("sessionId").GetString()!, (SessionRecordType)data.GetProperty("sessionType").GetInt32(), Date(data, "createdAt"), Date(data, "updatedAt")));
    internal static SessionAgentRecord ReadAgent(JsonElement data) => Read(() => new SessionAgentRecord(
        data.GetProperty("agentId").GetString()!, data.GetProperty("state"), data.GetProperty("conversationManagerState"), data.GetProperty("internalState"), Date(data, "createdAt"), Date(data, "updatedAt")));
    internal static SessionMessageRecord ReadMessage(JsonElement data) => Read(() => new SessionMessageRecord(
        data.GetProperty("message").Deserialize<Message>(Options) ?? throw new InvalidDataException("An indexed message cannot be null."),
        data.GetProperty("messageId").GetInt64(), data.GetProperty("redactMessage").Deserialize<Message>(Options), Date(data, "createdAt"), Date(data, "updatedAt")));
    private static DateTimeOffset Date(JsonElement data, string name) => data.GetProperty(name).GetDateTimeOffset();
    private static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        { throw new InvalidDataException("The indexed record payload is malformed.", error); }
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Indexed JSON contains duplicate property names.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
