using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>Provider prompt caching. Placement sections default to enabled when a cache configuration is supplied.</summary>
public sealed record CacheConfig
{
    /// <summary>Default provider cache duration; Anthropic accepts 5m/1h and OpenAI accepts in_memory/24h.</summary>
    public string? Ttl { get; init; }
    /// <summary>Whether to cache tool definitions; defaults to true.</summary>
    public bool Tools { get; init; } = true;
    /// <summary>Optional tool-section duration overriding Ttl.</summary>
    public string? ToolsTtl { get; init; }
    /// <summary>Whether to cache the system prompt; defaults to true.</summary>
    public bool SystemPrompt { get; init; } = true;
    /// <summary>Optional system-section duration overriding Ttl.</summary>
    public string? SystemPromptTtl { get; init; }
    /// <summary>Whether to cache the latest user-message prefix; defaults to true.</summary>
    public bool Messages { get; init; } = true;
    /// <summary>Optional message-section duration overriding Ttl.</summary>
    public string? MessagesTtl { get; init; }
    /// <summary>An explicit OpenAI cache-routing key, transmitted to the provider.</summary>
    public string? CacheKey { get; init; }
    /// <summary>Whether an unset key derives from the agent session ID; defaults to true. False opts out.</summary>
    public bool UseSessionKey { get; init; } = true;
}

/// <summary>Configuration for the OpenAI Chat Completions streaming API.</summary>
public sealed record OpenAIModelOptions
{
    /// <summary>Creates options with an explicit model and fixed bearer credential.</summary>
    public OpenAIModelOptions(string modelId, string apiKey) { ModelId = modelId; ApiKey = apiKey; }
    /// <summary>Creates options that obtain a fresh bearer credential for every request.</summary>
    public OpenAIModelOptions(string modelId, Func<CancellationToken, ValueTask<string>> apiKeyProvider) : this(modelId, string.Empty)
    {
        Guard.NotNull(apiKeyProvider, nameof(apiKeyProvider));
        ApiKeyProvider = apiKeyProvider;
    }
    /// <summary>The caller-owned asynchronous bearer credential provider, if supplied instead of a fixed key.</summary>
    public Func<CancellationToken, ValueTask<string>>? ApiKeyProvider { get; init; }
    /// <summary>The explicit model identifier sent to the configured endpoint.</summary>
    public string ModelId { get; init; }
    /// <summary>The credential sent in this provider's Authorization header.</summary>
    public string ApiKey { get; init; }
    /// <summary>The complete request URI. If null, chat/completions resolves against the client's base address, or the official v1 endpoint is used.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>The per-request maximum completion tokens, including reasoning tokens.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>Optional sampling temperature. Model support is provider-dependent.</summary>
    public double? Temperature { get; init; }
    /// <summary>Optional nucleus-sampling probability between zero and one.</summary>
    public double? TopP { get; init; }
    /// <summary>Optional frequency penalty between minus two and two.</summary>
    public double? FrequencyPenalty { get; init; }
    /// <summary>Optional presence penalty between minus two and two.</summary>
    public double? PresencePenalty { get; init; }
    /// <summary>Optional provider-native JSON parameters. Managed request fields and explicit options take precedence.</summary>
    public JsonElement? Params { get; init; }
    /// <summary>Optional automatic provider caching; null adds no cache-routing fields.</summary>
    public CacheConfig? CacheConfig { get; init; }
    /// <inheritdoc/>
    public override string ToString() => $"OpenAIModelOptions {{ ModelId = {ModelId}, Credentials = redacted }}";
}

/// <summary>Configuration for Anthropic's Messages streaming API.</summary>
public sealed record AnthropicModelOptions
{
    /// <summary>Creates options with an explicit Claude model and fixed credential.</summary>
    public AnthropicModelOptions(string modelId, string apiKey) { ModelId = modelId; ApiKey = apiKey; }
    /// <summary>The explicit Claude model identifier sent to the configured endpoint.</summary>
    public string ModelId { get; init; }
    /// <summary>The credential sent in this provider's x-api-key header.</summary>
    public string ApiKey { get; init; }
    /// <summary>The complete request URI. If null, messages resolves against the client's base address, or the official v1 endpoint is used.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>The positive per-request output-token limit, defaulting to the TypeScript baseline's 64000.</summary>
    public int MaxTokens { get; init; } = 64000;
    /// <summary>Whether to use provider-native token counting. The default false preserves heuristic counting.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseNativeTokenCount { get; init; }
    /// <summary>Optional sampling temperature between zero and one. Model support is provider-dependent.</summary>
    public double? Temperature { get; init; }
    /// <summary>Optional extended-thinking budget. Must be at least 1024 and less than MaxTokens.</summary>
    public int? ThinkingBudgetTokens { get; init; }
    /// <summary>The Anthropic API version header.</summary>
    public string ApiVersion { get; init; } = "2023-06-01";
    /// <summary>Optional nucleus-sampling probability between zero and one.</summary>
    public double? TopP { get; init; }
    /// <summary>Optional sequences that terminate generation.</summary>
    public IReadOnlyList<string>? StopSequences { get; init; }
    /// <summary>Optional provider-native JSON parameters. Managed request fields and explicit options take precedence.</summary>
    public JsonElement? Params { get; init; }
    /// <summary>Provider-executed tool definitions, appended after ordinary function tools. Definitions with input_schema belong in the agent tool registry.</summary>
    public IReadOnlyList<JsonElement> AnthropicTools { get; init; } = [];
    /// <summary>Optional automatic caching of tools, system instructions, and the latest user message.</summary>
    public CacheConfig? CacheConfig { get; init; }
    /// <summary>Opt-in Anthropic beta identifiers; no beta header is sent by default.</summary>
    public IReadOnlyList<string> Betas { get; init; } = [];
    /// <inheritdoc/>
    public override string ToString() => $"AnthropicModelOptions {{ ModelId = {ModelId}, Credentials = redacted }}";
}

internal static class ProviderConfiguration
{
    internal static JsonElement? Snapshot(JsonElement? parameters)
    {
        if (parameters is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Provider Params must be a JSON object.", nameof(parameters));
        return parameters?.Clone();
    }
    internal static Dictionary<string, object?> Parameters(JsonElement? parameters) => parameters is { } value
        ? value.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal) : new(StringComparer.Ordinal);
    internal static void Range(double? value, double minimum, double maximum, string parameter)
    {
        if (value is { } number && (!Guard.IsFinite(number) || number < minimum || number > maximum)) throw new ArgumentOutOfRangeException(parameter);
    }
    internal static void ValidateToolChoice(ModelRequest request)
    {
        if (request.ToolChoice is not { } choice) return;
        if (!Enum.IsDefined(typeof(ToolChoiceMode), choice.Mode)) throw new ArgumentException("Unknown tool-choice mode.", nameof(request));
        if (choice.Mode == ToolChoiceMode.Tool)
        {
            if (string.IsNullOrWhiteSpace(choice.Name) || request.Tools?.Any(tool => tool.Name == choice.Name) != true)
                throw new ArgumentException("A named tool choice must refer to a supplied tool.", nameof(request));
        }
        else if (choice.Name is not null) throw new ArgumentException("Only named tool selection accepts a name.", nameof(request));
        if (choice.Mode == ToolChoiceMode.Any && request.Tools is not { Count: > 0 }) throw new ArgumentException("Required tool selection needs supplied tools.", nameof(request));
    }
    internal static string AudioFormat(AudioBlock audio) => audio.MediaType switch
    {
        "audio/wav" or "audio/x-wav" => "wav",
        "audio/mpeg" or "audio/mp3" => "mp3",
        _ => throw new NotSupportedException($"OpenAI input audio does not support {audio.MediaType}.")
    };
    internal static string DataUrl(ImageBlock image)
    {
        if (image.MediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) throw new NotSupportedException($"Unsupported image media type {image.MediaType}.");
        MediaContent.Snapshot(image);
        return image.Url?.AbsoluteUri ?? $"data:{image.MediaType};base64,{MediaContent.InlineData(image, "This provider")}";
    }
    internal static object CacheControl(string? ttl)
    {
        if (ttl is not null and not ("5m" or "1h")) throw new ArgumentException("Anthropic cache TTL must be 5m or 1h.", nameof(ttl));
        var cache = new Dictionary<string, object?> { ["type"] = "ephemeral" };
        if (ttl is not null) cache["ttl"] = ttl;
        return cache;
    }
}
