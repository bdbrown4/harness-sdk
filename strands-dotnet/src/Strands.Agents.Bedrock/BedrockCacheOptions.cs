namespace Strands.Agents.Bedrock;

/// <summary>Selects how an explicit Bedrock cache profile recognizes model support.</summary>
public enum BedrockCacheStrategy
{
    /// <summary>Matches the case-sensitive ASCII family substrings used by the pinned TypeScript provider.</summary>
    Auto = 0,
    /// <summary>Enables managed caching without inspecting the model identifier.</summary>
    Anthropic = 1,
    /// <summary>Matches the same family substrings while folding only ASCII letter case.</summary>
    AutoIgnoreCase = 2
}

/// <summary>Configures managed Bedrock cache sections independently of the legacy cache flags.</summary>
public sealed record BedrockCacheOptions
{
    /// <summary>The model-support selection policy; exact case-sensitive automatic matching is the default.</summary>
    public BedrockCacheStrategy Strategy { get; init; }
    /// <summary>An optional shared duration. Nonempty strings are validated by the service.</summary>
    public string? Ttl { get; init; }
    /// <summary>Whether actual tool definitions receive a managed checkpoint.</summary>
    public bool Tools { get; init; } = true;
    /// <summary>An optional tools duration, falling back to the shared duration when empty or absent.</summary>
    public string? ToolsTtl { get; init; }
    /// <summary>Whether a nonempty system prompt receives a managed checkpoint.</summary>
    public bool SystemPrompt { get; init; } = true;
    /// <summary>An optional explicit system duration, falling back to the shared duration when empty or absent.</summary>
    public string? SystemPromptTtl { get; init; }
    /// <summary>Whether the final user message uses the existing managed checkpoint placement rules.</summary>
    public bool Messages { get; init; } = true;
    /// <summary>An optional message duration, falling back to the shared duration when empty or absent.</summary>
    public string? MessagesTtl { get; init; }
}
