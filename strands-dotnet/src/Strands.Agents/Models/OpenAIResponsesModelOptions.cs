using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>Explicit configuration for OpenAI's Responses streaming API.</summary>
public sealed record OpenAIResponsesModelOptions
{
    /// <summary>Creates options with an explicit model and fixed bearer credential.</summary>
    public OpenAIResponsesModelOptions(string modelId, string apiKey) { ModelId = modelId; ApiKey = apiKey; }
    /// <summary>Creates options that obtain a fresh bearer credential for every request.</summary>
    public OpenAIResponsesModelOptions(string modelId, Func<CancellationToken, ValueTask<string>> apiKeyProvider) : this(modelId, string.Empty)
    {
        Guard.NotNull(apiKeyProvider, nameof(apiKeyProvider));
        ApiKeyProvider = apiKeyProvider;
    }
    /// <summary>The caller-owned asynchronous bearer credential provider, if supplied instead of a fixed key.</summary>
    public Func<CancellationToken, ValueTask<string>>? ApiKeyProvider { get; init; }
    /// <summary>The model identifier transmitted to the provider.</summary>
    public string ModelId { get; init; }
    /// <summary>The bearer credential; the adapter never changes HttpClient default headers.</summary>
    public string ApiKey { get; init; }
    /// <summary>The complete endpoint override, otherwise responses resolves against the borrowed client's base address.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>An optional positive output-token bound.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>Optional sampling temperature from zero to two.</summary>
    public double? Temperature { get; init; }
    /// <summary>Optional nucleus sampling probability from zero to one.</summary>
    public double? TopP { get; init; }
    /// <summary>Additional provider-native JSON-object parameters, copied at construction. Managed request fields take precedence.</summary>
    public JsonElement? Params { get; init; }
    /// <summary>An optional native text format object, including json_schema; copied at construction.</summary>
    public JsonElement? TextFormat { get; init; }
    /// <summary>An optional native reasoning configuration object; copied at construction.</summary>
    public JsonElement? Reasoning { get; init; }
    /// <summary>Server response storage; defaults to false. This does not enable automatic agent-history management.</summary>
    public bool Store { get; init; }
    /// <summary>Enables server-managed history through previous_response_id. Defaults to false; true forces server response storage and requires agent-managed provider state.</summary>
    public bool Stateful { get; init; }
    /// <summary>Optional prompt-cache routing through CacheKey, UseSessionKey and Ttl. Section flags apply only to providers with explicit cache boundaries.</summary>
    public CacheConfig? CacheConfig { get; init; }
    /// <summary>The positive byte bound for a complete direct structured response; defaults to 16 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <inheritdoc/>
    public override string ToString() => $"OpenAIResponsesModelOptions {{ ModelId = {ModelId}, Credentials = redacted }}";
}
