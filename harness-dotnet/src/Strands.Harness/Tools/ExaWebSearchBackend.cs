using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Harness.Tools;

internal static class ExaWebSearchBackend
{
    private const string ToolName = "web_search_exa";
    internal static Func<string, int, CancellationToken, Task<IReadOnlyList<WebSearchResult>>> Create(ExaWebSearchConfig config)
    {
        Guard.NotNull(config.Endpoint, nameof(config.Endpoint));
        if (!config.Endpoint.IsAbsoluteUri || config.Endpoint.Scheme is not ("http" or "https")) throw new ArgumentException("Exa MCP Endpoint must be an absolute HTTP or HTTPS URI.", nameof(config));
        if (config.Timeout <= TimeSpan.Zero || config.Timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(config), "The Exa call timeout must be positive and at most Int32.MaxValue milliseconds.");
        return async (query, maximum, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = config.ApiKey ?? Environment.GetEnvironmentVariable("EXA_API_KEY");
            try { return await CallAsync(config, key, query, maximum, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("Exa search was canceled.", error, cancellationToken); }
            catch (Exception error) when (string.IsNullOrEmpty(key))
            { throw new InvalidOperationException(error.Message + " (Exa's keyless tier is rate limited; set EXA_API_KEY to lift it.)", error); }
        };
    }

    private static async Task<IReadOnlyList<WebSearchResult>> CallAsync(ExaWebSearchConfig config, string? key, string query, int maximum, CancellationToken cancellationToken)
    {
        using var ownedHttp = config.HttpClient is null ? new HttpClient() : null;
        var http = config.HttpClient ?? ownedHttp!;
        Client? client = null;
        try
        {
            IReadOnlyList<Strands.Agents.Mcp.McpTool> tools;
            try
            {
                client = await Client.CreateHttpAsync(http, new()
                {
                    Endpoint = config.Endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = string.IsNullOrEmpty(key) ? null : new Dictionary<string, string> { ["x-api-key"] = key! },
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
                tools = await client.ListToolsAsync(new() { ToolFilter = name => name == ToolName }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { throw new InvalidOperationException("could not reach Exa's MCP server at " + config.Endpoint + " (" + error.Message + ")", error); }
            if (!tools.Any(tool => tool.Spec.Name == ToolName)) throw new InvalidOperationException("Exa's MCP server does not offer " + ToolName);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(config.Timeout);
            CallToolResult result;
            try { result = await client.CallToolAsync(ToolName, JsonSerializer.SerializeToElement(new { query, numResults = maximum }), cancellationToken: deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new TimeoutException("Exa search timed out.", error); }
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            if (result.IsError == true) throw new InvalidOperationException(string.IsNullOrEmpty(text) ? "Exa reported an error" : text);
            return Parse(text);
        }
        finally
        {
            // The upstream tool keeps useful results and primary failures even when session teardown fails.
            if (client is not null) try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }
    }

    private static IReadOnlyList<WebSearchResult> Parse(string text)
    {
        var results = new List<WebSearchResult>();
        foreach (var block in Regex.Split(text, @"^---\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var head = Regex.Match(block, @"\A\s*Title: (.*)\r?\nURL: (\S+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!head.Success) continue;
            var offset = block.IndexOf("Highlights:", StringComparison.Ordinal);
            var snippet = offset < 0 ? "" : Regex.Replace(block.Substring(offset + "Highlights:".Length), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
            if (snippet.Length > 500)
            {
                var count = char.IsHighSurrogate(snippet[499]) && char.IsLowSurrogate(snippet[500]) ? 499 : 500;
                snippet = snippet.Substring(0, count);
            }
            results.Add(new(head.Groups[1].Value.Trim(), head.Groups[2].Value, snippet));
        }
        return results.AsReadOnly();
    }
}
