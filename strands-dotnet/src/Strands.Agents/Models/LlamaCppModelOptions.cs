using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>Configuration for a llama.cpp OpenAI-compatible HTTP server.</summary>
public sealed record LlamaCppModelOptions
{
    /// <summary>Creates options for the server's default model or an explicit loaded identifier.</summary>
    public LlamaCppModelOptions(string modelId = "default") => ModelId = modelId;
    /// <summary>The loaded server model identifier, defaulting to default.</summary>
    public string ModelId { get; init; }
    /// <summary>The complete chat endpoint; otherwise the client's base address or localhost:8080 is used.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>The complete native tokenization endpoint; otherwise /tokenize on the chat origin is used.</summary>
    public Uri? TokenCountEndpoint { get; init; }
    /// <summary>Detached OpenAI and llama.cpp sampling, grammar, and JSON-schema parameters.</summary>
    public JsonElement? Params { get; init; }
    /// <summary>Enables native /tokenize requests. The default uses the local token estimate.</summary>
    public bool UseNativeTokenCount { get; init; }
    /// <summary>Pins cache_prompt to true unless Params explicitly supplies it. Section controls are unsupported.</summary>
    public CacheConfig? CacheConfig { get; init; }
    /// <summary>The maximum native token-count JSON response bytes, defaulting to 16 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <inheritdoc/>
    public override string ToString() => $"LlamaCppModelOptions {{ ModelId = {ModelId}, Parameters = omitted }}";
}
