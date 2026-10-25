using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>One web-search source.</summary>
/// <param name="Title">The source title.</param>
/// <param name="Url">The source URL.</param>
/// <param name="Snippet">A short source excerpt.</param>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>Creates model-facing web-search tools.</summary>
public static class WebSearch
{
    /// <summary>Creates a search tool using Exa's hosted MCP service. Queries leave the selected sandbox.</summary>
    public static ITool CreateExa(ExaWebSearchConfig? config = null) => Create(ExaWebSearchBackend.Create(config ?? new()));

    /// <summary>Creates a search tool with an application backend. Cancellation propagates to that backend.</summary>
    public static ITool Create(Func<string, int, CancellationToken, Task<IReadOnlyList<WebSearchResult>>> backend)
    {
        Guard.NotNull(backend, nameof(backend));
        return new FunctionTool(new("web_search", "Search the web and return the top results as numbered sources with a short excerpt each.", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string", description = "What to search for. A descriptive sentence works better than bare keywords." }, max_results = new { type = "integer", description = "How many results to return, 1-10." } },
            required = new[] { "query" },
        })), async (input, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String)
                throw new ArgumentException("query must be a string.", nameof(input));
            var maximum = 5;
            if (input.TryGetProperty("max_results", out var count))
            {
                if (count.ValueKind != JsonValueKind.Number || !count.TryGetDecimal(out var value) || decimal.Truncate(value) != value)
                    throw new ArgumentException("max_results must be an integer.", nameof(input));
                maximum = (int)Math.Max(1, Math.Min(10, value));
            }
            IReadOnlyList<WebSearchResult> results;
            try { results = await backend(query.GetString()!, maximum, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return "web_search failed: " + (string.IsNullOrEmpty(error.Message) ? error.GetType().Name : error.Message); }
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count == 0) return "No results.";
            var lines = new List<string> { "Sources:" };
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                lines.Add((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + OneLine(result.Title) + " — " + result.Url);
                if (result.Snippet.Length > 0) lines.Add("   " + OneLine(result.Snippet));
            }
            return string.Join("\n", lines);
        });
    }

    private static string OneLine(string value) => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Explicit Exa MCP transport settings. The optional HTTP client remains caller-owned.</summary>
public sealed record ExaWebSearchConfig
{
    /// <summary>Describes transport settings without rendering the API credential.</summary>
    public override string ToString() => "ExaWebSearchConfig { Endpoint = " + Endpoint + ", ApiKey = " +
        (ApiKey is null ? "environment" : ApiKey.Length == 0 ? "disabled" : "configured") + ", Timeout = " + Timeout + " }";

    /// <summary>The hosted MCP endpoint, overridable for an explicitly selected compatible service.</summary>
    public Uri Endpoint { get; init; } = new("https://mcp.exa.ai/mcp");
    /// <summary>An explicit API key; null reads EXA_API_KEY per call, while empty disables that lookup.</summary>
    public string? ApiKey { get; init; }
    /// <summary>An optional caller-owned HTTP client and transport policy.</summary>
    public HttpClient? HttpClient { get; init; }
    /// <summary>The tool-call deadline, defaulting to thirty seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
