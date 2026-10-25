using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Models;

/// <summary>Explicit REST configuration for Gemini Developer API or caller-configured Vertex-compatible GenerateContent.</summary>
public sealed record GoogleModelOptions
{
    /// <summary>Creates options with an explicit Gemini model and fixed credential.</summary>
    [JsonConstructor]
    public GoogleModelOptions(string modelId, string apiKey) { ModelId = modelId; ApiKey = apiKey; }
    /// <summary>Creates keyless options for an explicitly selected Vertex-compatible transport.</summary>
    public GoogleModelOptions(string modelId) : this(modelId, string.Empty) { }
    /// <summary>Use an explicit Endpoint and caller-configured Vertex-compatible authentication transport. Omits the adapter API-key header and automatic mixed-tools flag; defaults to false.</summary>
    public bool UseVertexAi { get; init; }
    /// <summary>The model identifier used in the REST endpoint and model metadata.</summary>
    public string ModelId { get; init; }
    /// <summary>The API key, required in Developer mode and unused by the adapter in Vertex mode. Borrowed HttpClient default headers are never changed.</summary>
    public string ApiKey { get; init; }
    /// <summary>A complete streamGenerateContent endpoint, including alt=sse when required. Optional in Developer mode; explicitly required in Vertex mode even when HttpClient.BaseAddress is set.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>An optional complete countTokens endpoint; required for custom streaming URLs that cannot be derived.</summary>
    public Uri? TokenCountEndpoint { get; init; }
    /// <summary>An optional complete generateContent endpoint; required for structured output with a custom streaming URL that cannot be derived.</summary>
    public Uri? StructuredOutputEndpoint { get; init; }
    /// <summary>The maximum complete structured-response bytes; defaults to 16 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Native generationConfig fields, such as maxOutputTokens, thinkingConfig, responseMimeType and responseJsonSchema. Copied at construction; explicit values override Params.generationConfig.</summary>
    public JsonElement? GenerationConfig { get; init; }
    /// <summary>Additional native GenerateContentRequest fields, copied at construction. Params.toolConfig takes precedence over per-request tool choice; managed content, instructions and registered tools remain authoritative.</summary>
    public JsonElement? Params { get; init; }
    /// <summary>Native provider-side tool definitions, copied at construction and appended alongside registered function declarations.</summary>
    public IReadOnlyList<JsonElement> BuiltInTools { get; init; } = [];
    /// <summary>Whether counting calls the native countTokens endpoint; defaults to false. Native failures fall back to the upstream heuristic, while cancellation propagates.</summary>
    public bool UseNativeTokenCount { get; init; }
    /// <inheritdoc/>
    public override string ToString() => $"GoogleModelOptions {{ ModelId = {ModelId}, Credentials = redacted }}";
}
