using System.Text.Json;

namespace Strands.Agents.Context;

/// <summary>Immutable message pinning and adjacent tool-pair protection for context reduction.</summary>
public static class MessagePins
{
    /// <summary>Returns a message with metadata.custom.pinned set to true, preserving all other metadata.</summary>
    /// <param name="message">The message to protect.</param>
    /// <returns>A detached immutable message retaining its tracking identifier.</returns>
    public static Message Pin(Message message)
    {
        Guard.NotNull(message, nameof(message));
        var metadata = ReadObject(message.Metadata);
        var custom = metadata.TryGetValue("custom", out var value) ? ReadObject(value) : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        custom["pinned"] = JsonSerializer.SerializeToElement(true);
        metadata["custom"] = JsonSerializer.SerializeToElement(custom);
        return new(message.Role, message.Content, message.TrackingId, JsonSerializer.SerializeToElement(metadata));
    }

    /// <summary>Tests a message's explicit pin and protects an adjacent tool-pair partner with the same tool-use identifier.</summary>
    /// <param name="messages">The ordered conversation.</param>
    /// <param name="index">The message index to test.</param>
    /// <returns>True when the message or a correlated adjacent partner is pinned.</returns>
    public static bool IsPinned(IReadOnlyList<Message> messages, int index)
    {
        Guard.NotNull(messages, nameof(messages));
        if (index < 0 || index >= messages.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var message = messages[index];
        if (HasPin(message)) return true;
        if (index > 0 && HasPin(messages[index - 1]) && message.Content.OfType<ToolResultBlock>().Any(result => messages[index - 1].Content.OfType<ToolUseBlock>().Any(call => call.ToolUseId == result.ToolUseId))) return true;
        return index + 1 < messages.Count && HasPin(messages[index + 1]) && message.Content.OfType<ToolUseBlock>().Any(call => messages[index + 1].Content.OfType<ToolResultBlock>().Any(result => result.ToolUseId == call.ToolUseId));
    }

    /// <summary>Returns a message without its explicit pinned flag, retaining all other metadata.</summary>
    /// <param name="message">The message whose explicit pin is removed.</param>
    /// <returns>A detached message; an adjacent pinned partner can still protect it.</returns>
    public static Message Unpin(Message message)
    {
        Guard.NotNull(message, nameof(message));
        var metadata = ReadObject(message.Metadata);
        if (!metadata.TryGetValue("custom", out var value) || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("pinned", out _)) return message;
        var custom = ReadObject(value);
        custom.Remove("pinned");
        if (custom.Count == 0) metadata.Remove("custom");
        else metadata["custom"] = JsonSerializer.SerializeToElement(custom);
        return new(message.Role, message.Content, message.TrackingId, metadata.Count == 0 ? null : JsonSerializer.SerializeToElement(metadata));
    }

    /// <summary>Replaces the first count entries with explicitly pinned immutable messages.</summary>
    /// <param name="messages">The mutable list whose entries are replaced.</param>
    /// <param name="count">A nonnegative count, capped at the list length.</param>
    public static void ApplyPinFirst(IList<Message> messages, int count)
    {
        Guard.NotNull(messages, nameof(messages));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        for (var index = 0; index < Math.Min(count, messages.Count); index++) messages[index] = Pin(messages[index]);
    }

    private static bool HasPin(Message message) => message.Metadata is { } metadata && metadata.TryGetProperty("custom", out var custom) && custom.ValueKind == JsonValueKind.Object && custom.TryGetProperty("pinned", out var pinned) && pinned.ValueKind == JsonValueKind.True;

    private static Dictionary<string, JsonElement> ReadObject(JsonElement? value)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return result;
        if (value.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Pin metadata must be an object.", nameof(value));
        foreach (var property in value.Value.EnumerateObject()) result[property.Name] = property.Value.Clone();
        return result;
    }
}
