using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>Native HTTP configuration for an Ollama chat server. Credentials and transport policy belong to the supplied HttpClient.</summary>
public sealed record OllamaModelOptions
{
    /// <summary>Creates options with an explicit installed or remotely served model.</summary>
    public OllamaModelOptions(string modelId) { ModelId = modelId; }
    /// <summary>The requested model identifier.</summary>
    public string ModelId { get; init; }
    /// <summary>The complete chat endpoint; otherwise api/chat resolves against the client base address, falling back to localhost:11434.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>Optional native generation options, copied at construction.</summary>
    public JsonElement? Options { get; init; }
    /// <summary>Optional native top-level request parameters. Managed conversation, model, tools and streaming fields take precedence.</summary>
    public JsonElement? AdditionalArgs { get; init; }
    /// <summary>An optional num_predict override. Positive limits and Ollama's special -1/-2 values are accepted.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>An optional nonnegative finite sampling temperature.</summary>
    public double? Temperature { get; init; }
    /// <summary>An optional nucleus sampling probability from zero to one.</summary>
    public double? TopP { get; init; }
    /// <summary>Optional stop sequences, copied at construction.</summary>
    public IReadOnlyList<string>? StopSequences { get; init; }
    /// <summary>An optional string duration or numeric keep-alive setting.</summary>
    public JsonElement? KeepAlive { get; init; }
    /// <summary>An optional native JSON schema object or the string json for constrained output.</summary>
    public JsonElement? Format { get; init; }
    /// <summary>The maximum complete structured-response bytes; defaults to 16 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <inheritdoc/>
    public override string ToString() => $"OllamaModelOptions {{ ModelId = {ModelId} }}";
}
