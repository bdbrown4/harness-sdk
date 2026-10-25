namespace Strands.Agents.Models;

/// <summary>Configuration for the Meta Llama API protocol retained by the pinned Python compatibility adapter.</summary>
public sealed record LlamaApiModelOptions
{
    /// <summary>Creates options with an explicit model identifier and bearer credential.</summary>
    public LlamaApiModelOptions(string modelId, string apiKey) { ModelId = modelId; ApiKey = apiKey; }
    /// <summary>Creates options using a borrowed asynchronous credential provider for each request.</summary>
    public LlamaApiModelOptions(string modelId, Func<CancellationToken, ValueTask<string>> apiKeyProvider) : this(modelId, string.Empty)
    { Guard.NotNull(apiKeyProvider, nameof(apiKeyProvider)); ApiKeyProvider = apiKeyProvider; }
    /// <summary>The explicit model identifier sent to Meta.</summary>
    public string ModelId { get; init; }
    /// <summary>The fixed bearer credential, unused when ApiKeyProvider is supplied.</summary>
    public string ApiKey { get; init; }
    /// <summary>The borrowed per-request asynchronous credential callback.</summary>
    public Func<CancellationToken, ValueTask<string>>? ApiKeyProvider { get; init; }
    /// <summary>The complete chat endpoint; otherwise the client's base address or https://api.llama.com/v1/chat/completions is used.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>Optional native repetition penalty.</summary>
    public double? RepetitionPenalty { get; init; }
    /// <summary>Optional native sampling temperature.</summary>
    public double? Temperature { get; init; }
    /// <summary>Optional nucleus-sampling probability.</summary>
    public double? TopP { get; init; }
    /// <summary>Optional maximum completion tokens.</summary>
    public int? MaxCompletionTokens { get; init; }
    /// <summary>Optional top-k sampling setting.</summary>
    public int? TopK { get; init; }
    /// <summary>Opt-in native tool-choice support. False preserves the pinned Python adapter's warning and omission of nonnull tool choices.</summary>
    public bool HonorToolChoice { get; init; }
    /// <summary>The assistant-content projection; defaults to concatenated text. PythonFirstBlock explicitly enables the pinned source's lossy first-block behavior.</summary>
    public LlamaApiAssistantContentPolicy AssistantContentPolicy { get; init; }
    /// <inheritdoc/>
    public override string ToString() => $"LlamaApiModelOptions {{ ModelId = {ModelId}, Credentials = redacted }}";
}
