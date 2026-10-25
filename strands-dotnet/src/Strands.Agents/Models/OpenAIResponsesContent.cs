using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Models;

/// <summary>A completed Responses output-text or refusal part, including provider annotations and log probabilities.</summary>
public sealed record ResponsesContentBlock : ContentBlock
{
    /// <summary>Copies a completed assistant content part for exact Responses replay.</summary>
    [JsonConstructor]
    public ResponsesContentBlock(string itemId, int contentIndex, JsonElement part, string status = "completed", JsonElement? item = null)
    {
        Guard.NotNullOrWhiteSpace(itemId, nameof(itemId));
        if (contentIndex < 0) throw new ArgumentOutOfRangeException(nameof(contentIndex));
        var kind = ProviderHttp.RequiredString(part, "type");
        if (kind is not ("output_text" or "refusal")) throw new ArgumentException("A Responses content part must be output_text or refusal.", nameof(part));
        _ = ProviderHttp.OptionalString(part, kind == "refusal" ? "refusal" : "text") ?? throw new ArgumentException("A Responses part requires text.", nameof(part));
        if (status is not ("completed" or "incomplete")) throw new ArgumentException("A completed output item needs completed or incomplete status.", nameof(status));
        if (item is { } message && (ProviderHttp.RequiredString(message, "type") != "message" || ProviderHttp.RequiredString(message, "id") != itemId || ProviderHttp.RequiredString(message, "role") != "assistant" || ProviderHttp.RequiredString(message, "status") != status || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || contentIndex >= content.GetArrayLength() || !JsonElement.DeepEquals(content[contentIndex], part))) throw new ArgumentException("The completed Responses item must contain this exact part.", nameof(item));
        ItemId = itemId; ContentIndex = contentIndex; Part = part.Clone(); Status = status; Item = item?.Clone();
    }
    /// <summary>The provider message-item identifier.</summary>
    public string ItemId { get; }
    /// <summary>The part's index within its provider message.</summary>
    public int ContentIndex { get; }
    /// <summary>The detached native part, preserving refusal type and annotations.</summary>
    public JsonElement Part { get; }
    /// <summary>The completed provider item's status.</summary>
    public string Status { get; }
    /// <summary>The complete message item, when supplied on its first part, preserving additional provider metadata.</summary>
    public JsonElement? Item { get; }
    /// <summary>The text or refusal explanation, without interpreting its annotations.</summary>
    [JsonIgnore]
    public string Text => Part.GetProperty(Part.GetProperty("type").GetString() == "refusal" ? "refusal" : "text").GetString()!;
}

/// <summary>A completed Responses reasoning item, retaining its encrypted content for stateless replay.</summary>
public sealed record ResponsesReasoningBlock : ContentBlock
{
    /// <summary>Copies the complete reasoning item from output_item.done, rather than its incomplete added event.</summary>
    [JsonConstructor]
    public ResponsesReasoningBlock(JsonElement item)
    {
        if (ProviderHttp.RequiredString(item, "type") != "reasoning") throw new ArgumentException("Expected a Responses reasoning item.", nameof(item));
        _ = ProviderHttp.RequiredString(item, "id");
        Item = item.Clone();
    }
    /// <summary>The detached native item, including encrypted content, summary and provider metadata.</summary>
    public JsonElement Item { get; }
    /// <summary>The visible summary followed by any visible reasoning text; encrypted bytes are never exposed as text.</summary>
    [JsonIgnore]
    public string Text => TextFrom("summary") + TextFrom("content");
    private string TextFrom(string property) => Item.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? string.Concat(values.EnumerateArray().Select(value => ProviderHttp.OptionalString(value, "text") ?? "")) : "";
}

/// <summary>A completed provider-side Responses tool item or empty message, retained without local function dispatch.</summary>
public sealed record ResponsesOutputItemBlock : ContentBlock
{
    /// <summary>Copies a native output item for future Responses input.</summary>
    [JsonConstructor]
    public ResponsesOutputItemBlock(JsonElement item)
    {
        var kind = ProviderHttp.RequiredString(item, "type");
        if (kind is "function_call" or "reasoning" || kind == "message" && (ProviderHttp.RequiredString(item, "role") != "assistant" || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 0)) throw new ArgumentException("Use the corresponding typed Responses representation.", nameof(item));
        _ = ProviderHttp.RequiredString(item, "id");
        Item = item.Clone();
    }
    /// <summary>The detached native item, including provider-side tool output and metadata.</summary>
    public JsonElement Item { get; }
}
