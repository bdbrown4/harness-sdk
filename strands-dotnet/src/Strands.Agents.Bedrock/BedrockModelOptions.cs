using System.Text.Json;

namespace Strands.Agents.Bedrock;

/// <summary>Explicit Bedrock Converse configuration, independent of AWS credential and transport configuration.</summary>
public sealed record BedrockModelOptions
{
    /// <summary>The model or inference-profile identifier. The core default matches the Python SDK.</summary>
    public string ModelId { get; init; } = "global.anthropic.claude-sonnet-4-6";
    /// <summary>The region used by the convenience constructor. Caller-supplied clients retain their own region.</summary>
    public string Region { get; init; } = "us-west-2";
    /// <summary>An optional maximum output-token count.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>An optional sampling temperature between zero and one.</summary>
    public double? Temperature { get; init; }
    /// <summary>An optional nucleus-sampling probability between zero and one.</summary>
    public double? TopP { get; init; }
    /// <summary>Optional stop sequences, copied when the model is constructed.</summary>
    public IReadOnlyList<string>? StopSequences { get; init; }
    /// <summary>Additional model-specific fields, sent through the AWS Document representation.</summary>
    public JsonElement? AdditionalModelRequestFields { get; init; }
    /// <summary>Whether to append a cache checkpoint to nonempty system instructions.</summary>
    public bool CacheSystemPrompt { get; init; }
    /// <summary>Whether to append a cache checkpoint after tool definitions.</summary>
    public bool CacheTools { get; init; }
    /// <summary>Whether to manage one checkpoint on the last user message, preserving its first legal boundary before dynamic content.</summary>
    public bool CacheLastMessage { get; init; }
    /// <summary>An optional Bedrock cache duration, such as 5m or 1h; nonempty values are validated by the service.</summary>
    public string? CacheTtl { get; init; }
    /// <summary>An explicit section-aware cache profile. Null retains the legacy cache flags.</summary>
    public BedrockCacheOptions? Cache { get; init; }
    /// <summary>An optional deadline covering the request and streamed response; defaults to two minutes.</summary>
    public TimeSpan? RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>The maximum simultaneously buffered image or opaque reasoning bytes; defaults to 32 MiB.</summary>
    public long MaximumBufferedContentBytes { get; init; } = 32L * 1024 * 1024;
    /// <summary>The maximum indexed content blocks in one response; defaults to 1024.</summary>
    public int MaximumContentBlocks { get; init; } = 1024;
    /// <summary>Uses the native Bedrock CountTokens endpoint when true; defaults to the shared local heuristic.</summary>
    public bool UseNativeTokenCount { get; init; }
    /// <summary>Behavior when native counting fails. Fallback matches Python; cancellation and model disposal always propagate.</summary>
    public NativeTokenCountFailurePolicy TokenCountFailurePolicy { get; init; }
    /// <summary>Optional native guardrail request settings and content selection.</summary>
    public BedrockGuardrailOptions? Guardrail { get; init; }
    /// <summary>Optional JSON Pointer paths requested in additional model response fields.</summary>
    public IReadOnlyList<string>? AdditionalModelResponseFieldPaths { get; init; }
    /// <summary>Invocation-log metadata, copied when options are published.</summary>
    public IReadOnlyDictionary<string, string>? RequestMetadata { get; init; }
    /// <summary>Text values for a Bedrock managed prompt's variables.</summary>
    public IReadOnlyDictionary<string, string>? PromptVariables { get; init; }
    /// <summary>Optional performance latency setting, for example standard or optimized. Bedrock validates model availability.</summary>
    public string? PerformanceLatency { get; init; }
    /// <summary>Optional service tier, for example default, priority or flex. Bedrock validates model availability.</summary>
    public string? ServiceTier { get; init; }
    /// <summary>Include native tool-result status always (true), never (false), or automatically for supported model IDs (null).</summary>
    public bool? IncludeToolResultStatus { get; init; }
    /// <summary>Use ConverseStream when true (default), or project one native Converse response into the common event contract when false.</summary>
    public bool Streaming { get; init; } = true;
    /// <summary>Enable native strict tool definitions and recursively close object schemas without changing caller-owned schemas.</summary>
    public bool StrictTools { get; init; }

    /// <summary>Opt in to one request-acquisition retry for stale thinking blocks; defaults to false.</summary>
    public bool RecoverStaleThinkingBlocks { get; init; }
    /// <summary>Opt in to Python-style tool-result turn recovery; defaults to false.</summary>
    public bool RecoverToolResultTurns { get; init; }
    /// <summary>Opt in to pinned Python model-family reasoning replay filtering; defaults to false.</summary>
    public bool FilterRejectedReasoningContent { get; init; }
    /// <summary>Opt in to Nova tool-result JSON-to-text projection using native JSON spelling; defaults to false.</summary>
    public bool ConvertNovaToolResultJsonToText { get; init; }

}

/// <summary>Native Converse guardrail request configuration.</summary>
public sealed record BedrockGuardrailOptions
{
    /// <summary>The guardrail identifier or ARN.</summary>
    public string Identifier { get; init; } = "";
    /// <summary>The published version or DRAFT.</summary>
    public string Version { get; init; } = "";
    /// <summary>Trace mode: enabled (default), disabled or enabled_full.</summary>
    public string Trace { get; init; } = "enabled";
    /// <summary>Optional streaming processing mode: sync or async.</summary>
    public string? StreamProcessingMode { get; init; }
    /// <summary>Wrap text and supported inline images only on the last user message containing either, including across tool-result turns.</summary>
    public bool GuardLatestUserMessage { get; init; }
    /// <summary>Replace the latest user content when a guardrail blocks content; enabled by default.</summary>
    public bool RedactInput { get; init; } = true;
    /// <summary>Replace the complete assistant content when a guardrail blocks content; disabled by default.</summary>
    public bool RedactOutput { get; init; }
    /// <summary>Input replacement text; an empty string deliberately erases content.</summary>
    public string InputReplacement { get; init; } = "[User input redacted.]";
    /// <summary>Output replacement text; an empty string deliberately erases content.</summary>
    public string OutputReplacement { get; init; } = "[Assistant output redacted.]";
}

/// <summary>Controls fallback when a provider cannot count tokens.</summary>
public enum NativeTokenCountFailurePolicy
{
    /// <summary>Estimate locally on native failures and remember unsupported/permission-denied models for this provider instance.</summary>
    Fallback,
    /// <summary>Propagate a typed provider error instead of returning an estimate.</summary>
    Throw
}
