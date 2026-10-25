using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>The network execution authority used by web_fetch.</summary>
public enum WebFetchTransport
{
    /// <summary>Run curl through the explicitly selected command sandbox.</summary>
    Curl,
    /// <summary>Send HTTP requests from the harness process, explicitly bypassing the sandbox.</summary>
    Direct
}

/// <summary>The command language used by the selected sandbox.</summary>
public enum WebFetchShell
{
    /// <summary>A POSIX-compatible shell with curl and standard text utilities.</summary>
    Posix,
    /// <summary>Windows PowerShell with the native curl.exe executable.</summary>
    WindowsPowerShell
}

/// <summary>Settings for bounded web-page retrieval and optional summarization.</summary>
public sealed record WebFetchConfig
{
    internal string? ModelIdentifier { get; init; }
    /// <summary>An explicit summarizer model, overriding the Create method's model argument. Supplied model ownership remains with the caller.</summary>
    public IModel? Model { get; init; }
    /// <summary>The transport, defaulting to curl through the selected sandbox.</summary>
    public WebFetchTransport Transport { get; init; } = WebFetchTransport.Curl;
    /// <summary>An explicit sandbox command language; otherwise the selected environment capability must identify its platform.</summary>
    public WebFetchShell? Shell { get; init; }
    /// <summary>The complete fetch deadline, defaulting to thirty seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>An optional caller-owned direct HTTP client. Its redirect and transport policies remain the caller's responsibility.</summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>Creates the bounded web_fetch tool.</summary>
public static class WebFetch
{
    internal const int MaxBytes = 5 * 1024 * 1024;
    internal const int MaxCharacters = 50_000;
    internal const string UserAgent = "strands-harness/1.0";
    private const string SummarizerPrompt = "You answer a request about a single fetched web page. Use only the provided content; if it does not contain the answer, say so plainly. Be concise and factual, and preserve concrete details (names, numbers, quotes, links) relevant to the request.";
    /// <summary>Creates a page-fetch tool using the selected sandbox or explicit direct transport. The caller owns the model and any supplied HTTP client.</summary>
    public static ITool Create(ISandbox sandbox, IModel model, WebFetchConfig? config = null) => Create(sandbox, model, config ?? new(), Stopwatch.GetTimestamp);

    internal static ITool Create(ISandbox sandbox, IModel model, WebFetchConfig config, Func<long> timestamp) => new FetchTool(sandbox, config.Model ?? model, config, timestamp);
    internal static ITool CreateForAgent(ITool tool) => tool is FetchTool fetch ? fetch.CreateForAgent() : tool;

    private sealed record CachedPage(long Expires, FetchedPage Page);
    private sealed class FetchTool : ITool
    {
        private readonly ISandbox _sandbox;
        private readonly IModel _model;
        private readonly WebFetchConfig _config;
        private readonly Func<long> _timestamp;
        private readonly ConcurrentDictionary<string, CachedPage> _cache = new(StringComparer.Ordinal);
        internal FetchTool(ISandbox sandbox, IModel model, WebFetchConfig config, Func<long> timestamp)
        {
            Guard.NotNull(sandbox, nameof(sandbox)); Guard.NotNull(model, nameof(model)); Guard.NotNull(timestamp, nameof(timestamp));
            if (!Enum.IsDefined(typeof(WebFetchTransport), config.Transport)) throw new ArgumentOutOfRangeException(nameof(config), "Unknown web_fetch transport.");
            if (config.Shell is { } shell && !Enum.IsDefined(typeof(WebFetchShell), shell)) throw new ArgumentOutOfRangeException(nameof(config), "Unknown web_fetch shell.");
            if (config.Timeout <= TimeSpan.Zero || config.Timeout.TotalMilliseconds > int.MaxValue - 5000) throw new ArgumentOutOfRangeException(nameof(config), "Timeout must be positive and below Int32.MaxValue minus five seconds.");
            if (config.Transport == WebFetchTransport.Curl && sandbox is not ICommandSandbox) throw new ArgumentException("The curl web_fetch transport requires an ICommandSandbox. Select Direct explicitly to use host HTTP.", nameof(sandbox));
            _sandbox = sandbox; _model = model; _config = config; _timestamp = timestamp;
            Spec = new("web_fetch", "Fetch a URL and answer a prompt about its content. Performs an HTTP GET, reduces the response to text, and asks a small fast model to answer the prompt using only that content, returning the answer rather than the raw page so large pages do not flood the conversation. Fetched content is cached for 15 minutes, so repeated fetches of the same URL are fast while each prompt is answered fresh.", JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    url = new { type = "string", description = "The http(s) URL to fetch." },
                    prompt = new { type = "string", description = "What to extract from or answer about the fetched content. Leave empty to get the whole page content back verbatim instead of a model-generated answer." },
                },
                required = new[] { "url" },
            }));
        }
        public ToolSpec Spec { get; }
        internal ITool CreateForAgent() => new FetchTool(_sandbox, _model, _config, _timestamp);
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String) throw new ArgumentException("url must be a string.", nameof(input));
            var url = urlValue.GetString()!;
            string? prompt = null;
            if (input.TryGetProperty("prompt", out var requested) && requested.ValueKind != JsonValueKind.Null)
                prompt = requested.ValueKind == JsonValueKind.String ? requested.GetString() : throw new ArgumentException("prompt must be a string.", nameof(input));
            FetchedPage page;
            if (_cache.TryGetValue(url, out var cached) && cached.Expires > _timestamp()) page = cached.Page;
            else
            {
                try
                {
                    var validated = WebFetchContent.ValidateUrl(url);
                    page = _config.Transport == WebFetchTransport.Direct
                        ? await DirectWebFetch.FetchAsync(validated, _config, cancellationToken).ConfigureAwait(false)
                        : await CurlWebFetch.FetchAsync(_sandbox, validated, _config, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) { return "Failed to fetch " + url + ": " + error.Message; }
                _cache[url] = new(checked(_timestamp() + Stopwatch.Frequency * 900L), page);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(prompt)) return page.Text;
            var summarizer = new Agent(new() { Model = _model, SystemPrompt = SummarizerPrompt });
            await using var scope = summarizer.ConfigureAwait(false);
            var result = await summarizer.InvokeAsync("Fetched URL: " + page.ResolvedUrl + "\n\nRequest: " + prompt + "\n\n--- Content ---\n" + page.Text, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ToString();
        }
    }
}
