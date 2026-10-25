using Strands.Harness.Tools;

namespace Strands.Harness;

/// <summary>The authority serving the harness web-search feature.</summary>
public enum WebSearchProvider
{
    /// <summary>Use the model provider's native search where available.</summary>
    Native,
    /// <summary>Explicitly use Exa's third-party MCP service, which receives search queries.</summary>
    Exa,
}

/// <summary>Settings for native search selection or explicitly authorized Exa search.</summary>
public sealed record HarnessWebSearchOptions
{
    /// <summary>The selected search provider; defaults to the model provider's native capability.</summary>
    public WebSearchProvider Provider { get; init; } = WebSearchProvider.Native;
    /// <summary>Exa MCP configuration used only when Exa is selected and the web_search built-in is enabled.</summary>
    public ExaWebSearchConfig Exa { get; init; } = new();
}
