using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Strands.Agents.Models;

/// <summary>A native streaming adapter for Meta's Llama API, deprecated in the pinned Python SDK.</summary>
/// <remarks>The caller owns its HTTP transport. This adapter uses Meta's event protocol and does not proxy through an OpenAI adapter.</remarks>
public sealed class LlamaApiModel : IModel, IModelMetadataProvider, IConfigurableModel<LlamaApiModelOptions>
{
    internal static bool IsContextOverflow(string message) => new[]
    {
        "this model's maximum context length is", "exceed context limit", "model's maximum context limit",
        "is longer than the model's context length", "prompt is too long", "too many tokens"
    }.Any(pattern => message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
    private readonly LlamaApiModelOptions _options;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private LlamaApiModel? _configured;
    /// <summary>Creates an adapter using a borrowed HTTP client and explicit model settings.</summary>
    public LlamaApiModel(HttpClient client, LlamaApiModelOptions options)
    {
        Guard.NotNull(client, nameof(client)); Guard.NotNull(options, nameof(options)); Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (options.ApiKeyProvider is null) Guard.NotNullOrWhiteSpace(options.ApiKey, nameof(options.ApiKey));
        foreach (var value in new[] { options.RepetitionPenalty, options.Temperature, options.TopP })
            if (value is { } number && !Guard.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(options), "Generation settings must be finite.");
        if (options.MaxCompletionTokens is <= 0 || options.TopK is < 0) throw new ArgumentOutOfRangeException(nameof(options), "Completion tokens must be positive and top-k cannot be negative.");
        if (options.AssistantContentPolicy is not (LlamaApiAssistantContentPolicy.ConcatenatedText or LlamaApiAssistantContentPolicy.PythonFirstBlock))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown assistant-content projection policy.");
        _client = client; _options = options;
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "chat/completions", "https://api.llama.com/v1/chat/completions");
    }
    /// <inheritdoc/>
    public ModelMetadata Metadata { get { var options = GetConfig(); return new(options.ModelId, MaxOutputTokens: options.MaxCompletionTokens); } }
    /// <inheritdoc/>
    public LlamaApiModelOptions GetConfig() => (Volatile.Read(ref _configured) ?? this)._options;
    /// <inheritdoc/>
    public void UpdateConfig(Func<LlamaApiModelOptions, LlamaApiModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new LlamaApiModel(_client, update(GetConfig()));
        Interlocked.Exchange(ref _configured, replacement);
    }
    /// <summary>Streams native Meta text, tool calls and reported usage, ending only after a complete response.</summary>
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request)); cancellationToken.ThrowIfCancellationRequested();
        return (Volatile.Read(ref _configured) ?? this).ReadAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<ModelStreamEvent> ReadAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var outgoing = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(LlamaApiFormatting.Format(request, _options)) };
        outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ProviderHttp.ResolveApiKeyAsync(_options.ApiKey, _options.ApiKeyProvider, cancellationToken).ConfigureAwait(false));
        outgoing.Headers.Accept.Add(new("text/event-stream"));
        var response = await ProviderHttp.SendAsync(_client, outgoing, "Llama API", cancellationToken).ConfigureAwait(false);
        IAsyncEnumerator<SseFrame>? frames = null; Exception? primary = null;
        try
        {
            try
            {
                var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                frames = ServerSentEvents.ReadAsync(stream, acceptRawJsonErrors: true, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            var state = new LlamaApiStreamState();
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events; bool ended;
                try
                {
                    ended = !await frames.MoveNextAsync().ConfigureAwait(false);
                    if (ended) events = state.Complete();
                    else
                    {
                        var frame = frames.Current;
                        using var json = ProviderHttp.Parse(frame.Data, "Llama API");
                        if (frame.Event == "error" || json.RootElement.TryGetProperty("error", out _)) throw ProviderHttp.CreateError("Llama API", json.RootElement);
                        events = state.Process(json.RootElement);
                    }
                }
                catch (Exception error) { primary = error; throw; }
                foreach (var value in events)
                {
                    try { cancellationToken.ThrowIfCancellationRequested(); }
                    catch (Exception error) { primary = error; throw; }
                    yield return value;
                }
                if (ended) break;
            }
        }
        finally
        {
            Exception? cleanup = null;
            try { if (frames is not null) await frames.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanup = error; }
            try { response.Dispose(); }
            catch (Exception error) { if (cleanup is null) cleanup = error; else AttachCleanup(cleanup, error); }
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                else AttachCleanup(primary, cleanup);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!errors.Any(error => ReferenceEquals(error, cleanup))) errors.Add(cleanup);
    }
}
