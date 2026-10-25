using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Harness;

/// <summary>Provider-neutral reasoning effort mapped by the harness before model construction.</summary>
public enum HarnessEffort
{
    /// <summary>Use the provider/model recommendation.</summary>
    Auto,
    /// <summary>Disable configured reasoning, using none where supported.</summary>
    Off,
    /// <summary>Request minimal reasoning where supported.</summary>
    Minimal,
    /// <summary>Request low reasoning.</summary>
    Low,
    /// <summary>Request medium reasoning.</summary>
    Medium,
    /// <summary>Request high reasoning.</summary>
    High,
    /// <summary>Request extra-high reasoning where supported.</summary>
    ExtraHigh,
    /// <summary>Request maximum reasoning where supported.</summary>
    Max,
}

/// <summary>Immutable mapped provider settings consumed by an application model factory.</summary>
public sealed record HarnessModelConfiguration
{
    internal HarnessModelConfiguration(string identifier, string provider, string modelId, string? effort, int? maxTokens, JsonElement? parameters, bool caching, CacheConfig? cacheConfig, bool nativeWebSearch)
    {
        Identifier = identifier; Provider = provider; ModelId = modelId; Effort = effort; MaxTokens = maxTokens;
        Parameters = parameters?.Clone(); Caching = caching; CacheConfig = cacheConfig; NativeWebSearch = nativeWebSearch;
    }
    /// <summary>The original provider/name identifier.</summary>
    public string Identifier { get; }
    /// <summary>The resolved provider name; bare identifiers select bedrock.</summary>
    public string Provider { get; }
    /// <summary>The model name without its provider prefix.</summary>
    public string ModelId { get; }
    /// <summary>The mapped reasoning level, including none where supported; null adds no reasoning field.</summary>
    public string? Effort { get; }
    /// <summary>The harness model-specific output-token limit, when applicable.</summary>
    public int? MaxTokens { get; }
    /// <summary>Detached provider request fields, including reasoning, generation configuration and native tools.</summary>
    public JsonElement? Parameters { get; }
    /// <summary>Whether caching remains enabled after provider/model support checks. Automatic server caching needs no request fields.</summary>
    public bool Caching { get; }
    /// <summary>Explicit cache placement settings for supported Bedrock Anthropic and Anthropic-direct models; otherwise null.</summary>
    public CacheConfig? CacheConfig { get; }
    /// <summary>Whether the provider's native web search is selected.</summary>
    public bool NativeWebSearch { get; }
}
