using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.SageMaker;

/// <summary>How malformed container events are handled.</summary>
public enum SageMakerMalformedEventPolicy
{
    /// <summary>Warns and skips malformed data lines, matching the pinned Python parser.</summary>
    SkipWithWarning,
    /// <summary>Stops immediately with a model protocol failure.</summary>
    Throw
}

/// <summary>The model-container payload, independent of AWS routing and credentials.</summary>
public sealed record SageMakerPayloadOptions
{
    /// <summary>Uses InvokeEndpointWithResponseStream; defaults to true.</summary>
    public bool Stream { get; init; } = true;
    /// <summary>An optional positive output-token allowance.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>An optional nonnegative sampling temperature.</summary>
    public double? Temperature { get; init; }
    /// <summary>An optional nucleus-sampling probability between zero and one.</summary>
    public double? TopP { get; init; }
    /// <summary>An optional positive top-k value.</summary>
    public int? TopK { get; init; }
    /// <summary>Optional stop sequences, copied before publication.</summary>
    public IReadOnlyList<string>? Stop { get; init; }
    /// <summary>Formats tool responses as user messages; defaults to false.</summary>
    public bool ToolResultsAsUserMessages { get; init; }
    /// <summary>Additional container JSON fields overriding typed payload fields.</summary>
    public JsonElement? AdditionalArguments { get; init; }
}

/// <summary>Immutable SageMaker endpoint and model-container configuration.</summary>
public sealed record SageMakerModelOptions
{
    /// <summary>The explicit deployed endpoint name.</summary>
    public required string EndpointName { get; init; }
    /// <summary>The owned client's AWS region; null resolves AWS_REGION then us-west-2 at construction.</summary>
    public string? Region { get; init; }
    /// <summary>An optional inference component.</summary>
    public string? InferenceComponentName { get; init; }
    /// <summary>An optional multi-model target; supported by non-streaming InvokeEndpoint.</summary>
    public string? TargetModel { get; init; }
    /// <summary>An optional production variant.</summary>
    public string? TargetVariant { get; init; }
    /// <summary>Additional AWS request fields using their exact API names.</summary>
    public JsonElement? AdditionalArguments { get; init; }
    /// <summary>The model-container payload settings.</summary>
    public SageMakerPayloadOptions Payload { get; init; } = new();
    /// <summary>Unsupported cache settings retained in configuration but omitted from requests with a warning.</summary>
    public CacheConfig? CacheConfig { get; init; }
    /// <summary>An optional positive context-window size for metadata.</summary>
    public long? ContextWindowLimit { get; init; }
    /// <summary>Malformed SSE event handling; defaults to the Python warning-and-skip behavior.</summary>
    public SageMakerMalformedEventPolicy MalformedEventPolicy { get; init; }
    /// <summary>The maximum container event, buffered tool data, or complete JSON response in bytes; defaults to 16 MiB.</summary>
    public int MaximumResponseBytes { get; init; } = 16 * 1024 * 1024;
}
