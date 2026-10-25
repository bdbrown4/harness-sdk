using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Models;

/// <summary>A detached native Gemini content part, including thought signatures and opaque media or provider-tool output.</summary>
public sealed record GoogleContentBlock : ContentBlock
{
    /// <summary>Copies a native part. Function calls and responses use correlated ToolUseBlock and ToolResultBlock instead.</summary>
    /// <param name="part">A Gemini Part object, retained without merging signed and unsigned parts.</param>
    [JsonConstructor]
    public GoogleContentBlock(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object) throw new ArgumentException("A Google part must be an object.", nameof(part));
        if (part.TryGetProperty("functionCall", out _) || part.TryGetProperty("functionResponse", out _)) throw new ArgumentException("Use correlated tool content for Google function calls and responses.", nameof(part));
        if (part.TryGetProperty("text", out var text) && text.ValueKind != JsonValueKind.String) throw new ArgumentException("Google text must be a string.", nameof(part));
        if (part.TryGetProperty("thought", out var thought) && thought.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Google thought must be a boolean.", nameof(part));
        if (part.TryGetProperty("thoughtSignature", out var signature) && signature.ValueKind != JsonValueKind.String) throw new ArgumentException("Google thoughtSignature must be a string.", nameof(part));
        Part = part.Clone();
    }

    /// <summary>The complete detached Gemini Part, replayed without decoding its thought signature.</summary>
    public JsonElement Part { get; }
    /// <summary>Visible response text, excluding thought summaries.</summary>
    [JsonIgnore]
    public string Text => IsThought ? "" : ProviderHttp.OptionalString(Part, "text") ?? "";
    /// <summary>A visible thought summary, excluding encrypted signature bytes.</summary>
    [JsonIgnore]
    public string Reasoning => IsThought ? ProviderHttp.OptionalString(Part, "text") ?? "" : "";
    private bool IsThought => Part.TryGetProperty("thought", out var value) && value.ValueKind == JsonValueKind.True;
}
