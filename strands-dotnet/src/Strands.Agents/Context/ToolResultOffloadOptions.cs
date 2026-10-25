namespace Strands.Agents.Context;

/// <summary>Configures opt-in model-request offloading while retaining complete durable history.</summary>
public sealed record ToolResultOffloadOptions
{
    /// <summary>The estimated token count a successful result must exceed; defaults to 1,500.</summary>
    public int ThresholdTokens { get; init; } = 1_500;
    /// <summary>The text preview budget, estimated at four characters per token; defaults to 750.</summary>
    public int PreviewTokens { get; init; } = 750;
    /// <summary>The maximum textual retrieval budget including headers and truncation markers, estimated at four characters per token; defaults to 10,000. Opaque typed content is preserved.</summary>
    public int MaxRetrievalTokens { get; init; } = 10_000;
    /// <summary>The native per-line regex deadline; defaults to 100 milliseconds. Search patterns are capped at 200 characters; invalid patterns and nested quantifiers use literal matching.</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromMilliseconds(100);
}
