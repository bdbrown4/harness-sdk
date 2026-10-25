using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>Native configuration for Mistral's Chat Completions API.</summary>
public sealed record MistralModelOptions
{
    /// <summary>Creates native Mistral options with an explicit model and optional fixed credential.</summary>
    public MistralModelOptions(string modelId, string? apiKey = null) { ModelId = modelId; ApiKey = apiKey; }
    /// <summary>The Mistral model identifier.</summary>
    public string ModelId { get; init; }
    /// <summary>A fixed bearer credential; null resolves MISTRAL_API_KEY for each request.</summary>
    public string? ApiKey { get; init; }
    /// <summary>The complete endpoint override, otherwise the client's base address or Mistral's official endpoint.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>An optional positive output-token bound.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>Optional sampling temperature from zero through one.</summary>
    public double? Temperature { get; init; }
    /// <summary>Optional nucleus-sampling probability from zero through one.</summary>
    public double? TopP { get; init; }
    /// <summary>Whether to request streaming SSE; false adapts a complete JSON response into model events.</summary>
    public bool Stream { get; init; } = true;
    /// <summary>Optional prompt-cache routing. Mistral supports the routing key without section or retention controls.</summary>
    public CacheConfig? CacheConfig { get; init; }
    /// <summary>The maximum bytes buffered for a complete JSON response; defaults to 16 MiB and does not affect SSE frame limits.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <inheritdoc/>
    public override string ToString() => $"MistralModelOptions {{ ModelId = {ModelId}, Credentials = redacted }}";
}

/// <summary>A native Mistral HTTP adapter with caller-owned transport.</summary>
public sealed class MistralModel : IModel, IModelMetadataProvider, IConfigurableModel<MistralModelOptions>
{
    private readonly MistralModelOptions _options;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private MistralModel? _configured;
    /// <summary>Constructs a Mistral adapter without taking ownership of the client.</summary>
    public MistralModel(HttpClient client, MistralModelOptions options)
    {
        Guard.NotNull(client, nameof(client));
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (options.ApiKey is not null) Guard.NotNullOrWhiteSpace(options.ApiKey, nameof(options.ApiKey));
        if (options.MaxTokens is <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        ProviderConfiguration.Range(options.Temperature, 0, 1, nameof(options.Temperature));
        ProviderConfiguration.Range(options.TopP, 0, 1, nameof(options.TopP));
        _options = options;
        _client = client;
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "chat/completions", "https://api.mistral.ai/v1/chat/completions");
    }
    /// <inheritdoc/>
    public ModelMetadata Metadata { get { var options = GetConfig(); return new(options.ModelId, MaxOutputTokens: options.MaxTokens); } }
    /// <inheritdoc/>
    public MistralModelOptions GetConfig() => Volatile.Read(ref _configured)?._options ?? _options;
    /// <inheritdoc/>
    public void UpdateConfig(Func<MistralModelOptions, MistralModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new MistralModel(_client, update(GetConfig()));
        Interlocked.Exchange(ref _configured, replacement);
    }
    internal static bool IsContextOverflow(string detail) => detail.IndexOf("too large for model", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("maximum context length", StringComparison.OrdinalIgnoreCase) >= 0;
    /// <summary>Requests one schema-validated typed result through Mistral's forced, non-streaming tool protocol.</summary>
    public async Task<T> InvokeStructuredAsync<T>(ModelRequest request, StructuredOutputOptions<T> options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            return await configured.InvokeStructuredAsync(request, options, cancellationToken).ConfigureAwait(false);
        }
        var validator = new StructuredOutputTool<T>(options);
        var accumulator = new ModelStreamAccumulator();
        await foreach (var value in StreamCoreAsync(request, validator.Spec, cancellationToken).ConfigureAwait(false)) accumulator.Process(value);
        var (message, _) = accumulator.Complete();
        var calls = message.Content.OfType<ToolUseBlock>().ToArray();
        if (calls.Length != 1 || calls[0].Name != validator.Spec.Name)
            throw new StructuredOutputException("Mistral structured output requires exactly the requested extraction tool.");
        return validator.ParseOutput(calls[0].Input, cancellationToken);
    }
    /// <inheritdoc/>
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => StreamCoreAsync(request, null, cancellationToken);

    private async IAsyncEnumerable<ModelStreamEvent> StreamCoreAsync(ModelRequest request, ToolSpec? structuredTool, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var value in configured.StreamCoreAsync(request, structuredTool, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        var streaming = structuredTool is null && _options.Stream;
        using var outgoing = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(FormatRequest(request, structuredTool)) };
        var key = _options.ApiKey ?? Environment.GetEnvironmentVariable("MISTRAL_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new ModelException("Mistral requires an API key or MISTRAL_API_KEY.");
        outgoing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        outgoing.Headers.Accept.Add(new(streaming ? "text/event-stream" : "application/json"));
        var response = await ProviderHttp.SendAsync(_client, outgoing, "Mistral", cancellationToken, streaming ? null : ["application/json"]).ConfigureAwait(false);
        Exception? primary = null;
        IAsyncEnumerator<SseFrame>? frames = null;
        try
        {
            Stream stream;
            try
            {
                stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (streaming) frames = ServerSentEvents.ReadAsync(stream, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            var state = new ChatCompletionsStreamState("Mistral");
            var normalization = new ChatCompletionsResponseNormalizer("Mistral");
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                bool complete;
                try
                {
                    if (!streaming)
                    {
                        using var document = await ProviderHttp.ReadJsonAsync(stream, "Mistral", _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                        events = state.Process(normalization.Normalize(document.RootElement, completeResponse: true)).Concat(new[] { state.Complete() }).ToArray();
                        complete = true;
                    }
                    else if (!await frames!.MoveNextAsync().ConfigureAwait(false) || frames.Current.Data == "[DONE]")
                    {
                        events = [state.Complete()];
                        complete = true;
                    }
                    else
                    {
                        using var document = ProviderHttp.Parse(frames.Current.Data, "Mistral");
                        events = state.Process(normalization.Normalize(document.RootElement, completeResponse: false));
                        complete = false;
                    }
                }
                catch (Exception error) { primary = error; throw; }
                foreach (var value in events) yield return value;
                if (complete) yield break;
            }
        }
        finally
        {
            Exception? cleanup = null;
            try { if (frames is not null) await frames.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanup = error; }
            try { response.Dispose(); }
            catch (Exception error) { if (cleanup is null) cleanup = error; else AddCleanup(cleanup, error); }
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                else AddCleanup(primary, cleanup);
            }
        }
    }

    private Dictionary<string, object?> FormatRequest(ModelRequest request, ToolSpec? structuredTool)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var message in request.Messages) FormatMessage(message, messages);
        var body = new Dictionary<string, object?> { ["model"] = _options.ModelId, ["messages"] = messages, ["stream"] = structuredTool is null && _options.Stream };
        if (_options.MaxTokens is { } max) body["max_tokens"] = max;
        if (_options.Temperature is { } temperature) body["temperature"] = temperature;
        if (_options.TopP is { } topP) body["top_p"] = topP;
        var specifications = structuredTool is not null ? [structuredTool] : request.Tools;
        if (specifications is { Count: > 0 }) body["tools"] = specifications.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema } }).ToArray();
        // The pinned Python provider ignores ordinary per-call selection; its structured
        // output helper uses a separate forced call. Do not silently reinterpret that policy.
        if (structuredTool is not null)
        {
            body["tool_choice"] = "any";
            body["parallel_tool_calls"] = false;
        }
        else if (request.ToolChoice is not null) SdkLogging.Warning("Mistral's ordinary model stream ignores tool choice, matching the pinned provider.");
        if (_options.CacheConfig is { } cache)
        {
            var key = cache.CacheKey ?? (cache.UseSessionKey && request.SessionId is { } session ? "strands-" + session : null);
            if (!string.IsNullOrEmpty(key)) body["prompt_cache_key"] = key;
            if (cache.Ttl is not null || cache.ToolsTtl is not null || cache.SystemPromptTtl is not null || cache.MessagesTtl is not null)
                SdkLogging.Warning("Mistral supports prompt-cache routing without section or retention controls.");
        }
        return body;
    }

    private static void FormatMessage(Message message, List<object> messages)
    {
        Guard.NotNull(message, nameof(message));
        var content = new List<object>();
        var calls = new List<object>();
        var results = new List<object>();
        foreach (var block in message.Content)
        {
            MediaContent.RejectS3(block, "Mistral");
            MediaContent.RejectGuardSelection(block, "Mistral");
            switch (block)
            {
                case TextBlock text: content.Add(text.Text); break;
                case CitationsBlock citations: content.Add(citations.Text); break;
                case ImageBlock image: content.Add(new { type = "image_url", image_url = ProviderConfiguration.DataUrl(image) }); break;
                case ToolUseBlock call:
                    calls.Add(new { id = call.ToolUseId, type = "function", function = new { name = call.Name, arguments = call.Input.GetRawText() } });
                    break;
                case ToolResultBlock result:
                    results.Add(new
                    {
                        role = "tool",
                        name = result.ToolUseId.Split('_')[0],
                        tool_call_id = result.ToolUseId,
                        content = string.Join("\n", (result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)]).Select(part => part switch { TextBlock text => text.Text, JsonBlock json => json.Json.GetRawText(), _ => null }).Where(value => value is not null))
                    });
                    break;
                default: SdkLogging.Warning("Mistral skips unsupported content block {BlockType}, matching the pinned provider.", block.GetType().Name); break;
            }
        }
        if (content.Count > 0 || calls.Count > 0)
        {
            var ordinary = new Dictionary<string, object?> { ["role"] = message.Role == Role.User ? "user" : "assistant" };
            ordinary["content"] = content.Any(value => value is not string) ? content.Select(value => value is string text ? (object)new { type = "text", text } : value).ToArray() : string.Join(" ", content.Cast<string>());
            if (calls.Count > 0) ordinary["tool_calls"] = calls;
            messages.Add(ordinary);
        }
        messages.AddRange(results);
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!ReferenceEquals(primary, cleanup)) errors.Add(cleanup);
    }

}
