using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents;

/// <summary>Optional model identity and context/output limits. Supplied limits must be positive.</summary>
/// <param name="ModelId">The provider model identifier used for the pinned context-window lookup.</param>
/// <param name="ContextWindowLimit">The total context capacity shared by input and output tokens.</param>
/// <param name="MaxOutputTokens">The configured output allowance, when known.</param>
public sealed record ModelMetadata(string? ModelId = null, long? ContextWindowLimit = null, long? MaxOutputTokens = null)
{
    /// <summary>The upstream fallback for a model without known metadata: a 200,000-token context window.</summary>
    public static ModelMetadata Unknown { get; } = new(ContextWindowLimit: 200_000);
}

/// <summary>Optional provider capability for model identity and configured context/output limits.</summary>
public interface IModelMetadataProvider
{
    /// <summary>The provider's current metadata. Explicit call-site overrides take precedence.</summary>
    ModelMetadata Metadata { get; }
}

/// <summary>Optional provider-specific token counting. Accuracy depends on the provider's documented implementation.</summary>
public interface IModelTokenCounter
{
    /// <summary>Counts or estimates the input represented by the messages, system prompt, and tools.</summary>
    /// <param name="request">The complete model-visible request.</param>
    /// <param name="cancellationToken">Cancellation for tokenization or native provider requests.</param>
    /// <returns>A nonnegative input-token count.</returns>
    ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Model metadata and token-counting capabilities with a dependency-free upstream heuristic fallback.</summary>
public static class ModelExtensions
{
    private static readonly JsonSerializerOptions CountingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Counts input through the provider capability or estimates text at four characters per token and JSON at two. Binary media, opaque reasoning, and protocol overhead are not counted; this is not a billing or strict quota bound.</summary>
    /// <param name="model">The model, with optional provider-specific counting.</param>
    /// <param name="request">Messages, system prompt, and tool specifications to include.</param>
    /// <param name="cancellationToken">Cancellation for counting and provider requests.</param>
    /// <returns>A nonnegative count; treat it as an estimate unless the provider documents tokenizer accuracy.</returns>
    public static async ValueTask<long> CountTokensAsync(this IModel model, ModelRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(model, nameof(model));
        Guard.NotNull(request, nameof(request));
        Guard.NotNull(request.Messages, nameof(request.Messages));
        cancellationToken.ThrowIfCancellationRequested();
        if (model is IModelTokenCounter counter)
        {
            var count = await counter.CountTokensAsync(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count < 0) throw new InvalidDataException("A model token counter returned a negative input-token count.");
            return count;
        }
        return EstimateInputTokens(request, cancellationToken);
    }

    internal static long EstimateInputTokens(ModelRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        Guard.NotNull(request.Messages, nameof(request.Messages));
        cancellationToken.ThrowIfCancellationRequested();
        long total;
        if (request.SystemPromptContent is { } system)
        {
            total = 0;
            foreach (var block in system)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + CountBlock(block, cancellationToken));
            }
        }
        else total = CountText(request.SystemPrompt ?? "");
        foreach (var message in request.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guard.NotNull(message, nameof(request.Messages));
            foreach (var block in message.Content)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + CountBlock(block, cancellationToken));
            }
        }
        if (request.Tools is not null)
            foreach (var tool in request.Tools)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guard.NotNull(tool, nameof(request.Tools));
                total = checked(total + (JsonSerializer.Serialize(tool, CountingJsonOptions).Length + 1L) / 2);
            }
        cancellationToken.ThrowIfCancellationRequested();
        return total;
    }

    /// <summary>Counts or estimates messages with optional system instructions and tool specifications.</summary>
    /// <param name="model">The model whose optional counting capability is used.</param>
    /// <param name="messages">The ordered model-visible conversation.</param>
    /// <param name="systemPrompt">Additional system instructions.</param>
    /// <param name="tools">Available tool specifications.</param>
    /// <param name="cancellationToken">Cancellation for counting.</param>
    /// <returns>A nonnegative estimated input-token count.</returns>
    public static ValueTask<long> CountTokensAsync(this IModel model, IReadOnlyList<Message> messages, string? systemPrompt = null, IReadOnlyList<ToolSpec>? tools = null, CancellationToken cancellationToken = default) => model.CountTokensAsync(new ModelRequest(messages, systemPrompt, tools), cancellationToken);

    /// <summary>Resolves positive limits from explicit overrides, provider metadata, the pinned model table, and finally Unknown.</summary>
    /// <param name="model">The model whose optional metadata is used.</param>
    /// <param name="overrides">Explicit per-field values, taking precedence over provider metadata.</param>
    /// <returns>Metadata with a positive resolved context-window limit.</returns>
    public static ModelMetadata GetLimits(this IModel model, ModelMetadata? overrides = null)
    {
        Guard.NotNull(model, nameof(model));
        var metadata = model is IModelMetadataProvider provider
            ? provider.Metadata ?? throw new InvalidOperationException("A model metadata provider returned null metadata.")
            : new ModelMetadata();
        ValidateMetadata(metadata, nameof(model));
        if (overrides is not null) ValidateMetadata(overrides, nameof(overrides));
        var modelId = overrides?.ModelId ?? metadata.ModelId;
        var contextWindow = overrides?.ContextWindowLimit ?? metadata.ContextWindowLimit ?? LookupContextWindow(modelId);
        if (contextWindow is null)
        {
            contextWindow = ModelMetadata.Unknown.ContextWindowLimit;
            SdkLogging.WarnOnce("Model context window is unknown; using the upstream fallback of 200000 tokens. Supply explicit metadata for accurate utilization estimates.");
        }
        return new(modelId, contextWindow, overrides?.MaxOutputTokens ?? metadata.MaxOutputTokens);
    }

    /// <summary>Returns input tokens divided by the resolved context window. Overflow remains above one.</summary>
    /// <param name="model">The model whose context window is resolved.</param>
    /// <param name="inputTokens">A nonnegative input-token count.</param>
    /// <param name="overrides">Optional explicit metadata overrides.</param>
    /// <returns>A finite, nonnegative utilization ratio without overflow clamping.</returns>
    public static double EstimateUtilization(this IModel model, long inputTokens, ModelMetadata? overrides = null)
    {
        Guard.NotNull(model, nameof(model));
        if (inputTokens < 0) throw new ArgumentOutOfRangeException(nameof(inputTokens), "Input-token counts must be nonnegative.");
        return inputTokens / (double)model.GetLimits(overrides).ContextWindowLimit!.Value;
    }

    private static void ValidateMetadata(ModelMetadata metadata, string parameterName)
    {
        if (metadata.ModelId is not null) Guard.NotNullOrWhiteSpace(metadata.ModelId, parameterName);
        if (metadata.ContextWindowLimit is <= 0 || metadata.MaxOutputTokens is <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Model context and output limits must be positive when supplied.");
    }

    private static long? LookupContextWindow(string? modelId)
    {
        if (modelId is null) return null;
        if (ContextWindows.TryGetValue(modelId, out var direct)) return direct;
        var separator = modelId.IndexOf('.');
        if (separator > 0 && BedrockRegionPrefixes.Contains(modelId.Substring(0, separator)) && ContextWindows.TryGetValue(modelId.Substring(separator + 1), out var regional)) return regional;
        return null;
    }

    // Pinned baseline 3bd98e618320f0f286acbfb2252f72388030421b:
    // strands-ts/src/models/defaults.ts and strands-py/src/strands/models/_defaults.py.
    // This is not a live model catalog.
    private static readonly IReadOnlyDictionary<string, long> ContextWindows = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["claude-sonnet-4-6"] = 1_000_000,
        ["claude-sonnet-4-20250514"] = 1_000_000,
        ["claude-sonnet-4-5"] = 200_000,
        ["claude-sonnet-4-5-20250929"] = 200_000,
        ["claude-opus-4-6"] = 1_000_000,
        ["claude-opus-4-6-20260205"] = 1_000_000,
        ["claude-opus-4-7"] = 1_000_000,
        ["claude-opus-4-7-20260416"] = 1_000_000,
        ["claude-opus-4-8"] = 1_000_000,
        ["claude-opus-5"] = 1_000_000,
        ["claude-opus-5-5"] = 1_000_000,
        ["claude-fable-5"] = 1_000_000,
        ["claude-fable-5-1"] = 1_000_000,
        ["claude-sonnet-5"] = 1_000_000,
        ["claude-sonnet-5-5"] = 1_000_000,
        ["claude-haiku-5-5"] = 1_000_000,
        ["claude-opus-4-5"] = 200_000,
        ["claude-opus-4-5-20251101"] = 200_000,
        ["claude-opus-4-20250514"] = 200_000,
        ["claude-opus-4-1"] = 200_000,
        ["claude-opus-4-1-20250805"] = 200_000,
        ["claude-haiku-4-5"] = 200_000,
        ["claude-haiku-4-5-20251001"] = 200_000,
        ["claude-3-7-sonnet-20250219"] = 200_000,
        ["claude-3-5-sonnet-20241022"] = 200_000,
        ["claude-3-5-sonnet-20240620"] = 200_000,
        ["claude-3-5-haiku-20241022"] = 200_000,
        ["claude-3-opus-20240229"] = 200_000,
        ["claude-3-haiku-20240307"] = 200_000,
        ["anthropic.claude-sonnet-4-6"] = 1_000_000,
        ["anthropic.claude-sonnet-4-20250514-v1:0"] = 1_000_000,
        ["anthropic.claude-sonnet-4-5-20250929-v1:0"] = 200_000,
        ["anthropic.claude-opus-4-6-v1"] = 1_000_000,
        ["anthropic.claude-opus-4-7"] = 1_000_000,
        ["anthropic.claude-opus-4-8"] = 1_000_000,
        ["anthropic.claude-opus-5"] = 1_000_000,
        ["anthropic.claude-opus-5-5"] = 1_000_000,
        ["anthropic.claude-fable-5"] = 1_000_000,
        ["anthropic.claude-fable-5-1"] = 1_000_000,
        ["anthropic.claude-sonnet-5"] = 1_000_000,
        ["anthropic.claude-sonnet-5-5"] = 1_000_000,
        ["anthropic.claude-haiku-5-5"] = 1_000_000,
        ["anthropic.claude-opus-4-5-20251101-v1:0"] = 200_000,
        ["anthropic.claude-opus-4-20250514-v1:0"] = 200_000,
        ["anthropic.claude-opus-4-1-20250805-v1:0"] = 200_000,
        ["anthropic.claude-haiku-4-5-20251001-v1:0"] = 200_000,
        ["anthropic.claude-haiku-4-5@20251001"] = 200_000,
        ["anthropic.claude-3-7-sonnet-20250219-v1:0"] = 200_000,
        ["anthropic.claude-3-7-sonnet-20240620-v1:0"] = 200_000,
        ["anthropic.claude-3-5-sonnet-20241022-v2:0"] = 200_000,
        ["anthropic.claude-3-5-sonnet-20240620-v1:0"] = 200_000,
        ["anthropic.claude-3-5-haiku-20241022-v1:0"] = 200_000,
        ["anthropic.claude-3-opus-20240229-v1:0"] = 200_000,
        ["anthropic.claude-3-haiku-20240307-v1:0"] = 200_000,
        ["anthropic.claude-3-sonnet-20240229-v1:0"] = 200_000,
        ["anthropic.claude-mythos-preview"] = 1_000_000,
        ["amazon.nova-pro-v1:0"] = 300_000,
        ["amazon.nova-lite-v1:0"] = 300_000,
        ["amazon.nova-micro-v1:0"] = 128_000,
        ["amazon.nova-premier-v1:0"] = 1_000_000,
        ["amazon.nova-2-lite-v1:0"] = 1_000_000,
        ["amazon.nova-2-pro-preview-20251202-v1:0"] = 1_000_000,
        ["zai.glm-4.7"] = 203_000,
        ["gpt-6-astra"] = 1_050_000,
        ["gpt-6.1-sol"] = 1_050_000,
        ["gpt-6-sol"] = 1_050_000,
        ["gpt-6-luna"] = 1_050_000,
        ["gpt-5.6"] = 1_050_000,
        ["gpt-5.6-sol"] = 1_050_000,
        ["gpt-5.6-terra"] = 1_050_000,
        ["gpt-5.6-luna"] = 1_050_000,
        ["gpt-5.5"] = 1_050_000,
        ["gpt-5.5-pro"] = 1_050_000,
        ["gpt-5.4"] = 1_050_000,
        ["gpt-5.4-pro"] = 1_050_000,
        ["gpt-5.4-mini"] = 272_000,
        ["gpt-5.4-nano"] = 272_000,
        ["gpt-5.2"] = 272_000,
        ["gpt-5.2-pro"] = 272_000,
        ["gpt-5.1"] = 272_000,
        ["gpt-5"] = 272_000,
        ["gpt-5-mini"] = 272_000,
        ["gpt-5-nano"] = 272_000,
        ["gpt-5-pro"] = 128_000,
        ["gpt-4.1"] = 1_047_576,
        ["gpt-4.1-mini"] = 1_047_576,
        ["gpt-4.1-nano"] = 1_047_576,
        ["gpt-4o"] = 128_000,
        ["gpt-4o-mini"] = 128_000,
        ["gpt-4-turbo"] = 128_000,
        ["o3"] = 200_000,
        ["o3-mini"] = 200_000,
        ["o3-pro"] = 200_000,
        ["o4-mini"] = 200_000,
        ["o1"] = 200_000,
        ["gemini-2.5-flash"] = 1_048_576,
        ["gemini-2.5-flash-lite"] = 1_048_576,
        ["gemini-2.5-pro"] = 1_048_576,
        ["gemini-2.0-flash"] = 1_048_576,
        ["gemini-2.0-flash-lite"] = 1_048_576,
        ["gemini-3-pro-preview"] = 1_048_576,
        ["gemini-3-flash-preview"] = 1_048_576,
        ["gemini-3.6-flash"] = 1_048_576,
        ["gemini-3.5-flash"] = 1_048_576,
        ["gemini-3.5-flash-lite"] = 1_048_576,
        ["gemini-3.1-flash-lite"] = 1_048_576,
        ["gemini-3.1-pro-preview"] = 1_048_576,
        ["gemini-3.1-flash-lite-preview"] = 1_048_576,
        ["mistral-large-latest"] = 262_144,
        ["mistral-large-2512"] = 262_144,
        ["mistral-large-3"] = 262_144,
        ["mistral-medium-latest"] = 131_072,
        ["mistral-medium-2505"] = 131_072,
        ["mistral-small-latest"] = 131_072,
        ["mistral-small-3-2-2506"] = 131_072,
    };

    private static readonly HashSet<string> BedrockRegionPrefixes = new(StringComparer.Ordinal)
    {
        "us", "eu", "ap", "global", "apac", "au", "jp", "us-gov"
    };

    private static long CountText(string text) => (text.Length + 3L) / 4;
    private static long CountJson(JsonElement value) => (JsonSerializer.Serialize(value, CountingJsonOptions).Length + 1L) / 2;

    private static long CountBlock(ContentBlock block, CancellationToken cancellationToken)
    {
        switch (block)
        {
            case TextBlock text: return CountText(text.Text);
            case Models.ResponsesContentBlock text: return CountText(text.Text);
            case Models.ResponsesReasoningBlock reasoning: return CountText(reasoning.Text);
            case Models.GoogleContentBlock google: return checked(CountText(google.Text) + CountText(google.Reasoning));
            case Models.AnthropicContentBlock anthropic: return CountText(anthropic.Text);
            case CitationsBlock citations: return CountText(citations.Text);
            case JsonBlock json: return CountJson(json.Json);
            case ReasoningBlock reasoning: return CountText(reasoning.Text);
            case GuardContentBlock { Text: { } text }: return CountText(text);
            case ToolUseBlock call: return checked(CountText(call.Name) + CountJson(call.Input));
            case ToolResultBlock result:
                long total = result.ContentBlocks.Count == 0 ? CountText(result.Content) : 0;
                foreach (var content in result.ContentBlocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (content is TextBlock item) total = checked(total + CountText(item.Text));
                    else if (content is JsonBlock value) total = checked(total + CountJson(value.Json));
                }
                if (result.StructuredContent is { } structured) total = checked(total + CountJson(structured));
                return total;
            default: return 0;
        }
    }
}
