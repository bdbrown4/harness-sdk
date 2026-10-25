using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>A native streaming adapter for OpenAI's Chat Completions API.</summary>
/// <remarks>The caller owns the HttpClient. Requests are never retried automatically; replaying a partial stream can duplicate tool calls.</remarks>
public sealed class OpenAIModel : IModel, IModelMetadataProvider, IConfigurableModel<OpenAIModelOptions>
{
    /// <inheritdoc />
    public ModelMetadata Metadata { get { var options = GetConfig(); return new(options.ModelId, MaxOutputTokens: options.MaxTokens); } }
    private readonly HttpClient _client;
    private readonly OpenAIModelOptions _options;
    private readonly Uri _endpoint;
    private readonly JsonElement? _parameters;
    private OpenAIModel? _configured;
    /// <inheritdoc/>
    public OpenAIModelOptions GetConfig()
    {
        var current = Volatile.Read(ref _configured) ?? this;
        return current._options with { Params = current._parameters };
    }
    /// <inheritdoc/>
    public void UpdateConfig(Func<OpenAIModelOptions, OpenAIModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new OpenAIModel(_client, update(GetConfig()));
        Interlocked.Exchange(ref _configured, replacement);
    }

    /// <summary>Creates a Chat Completions adapter using a caller-owned HTTP client.</summary>
    /// <param name="client">The HTTP client used for every request; the adapter never disposes it.</param>
    /// <param name="options">The explicit credentials, model identifier, endpoint, and generation settings.</param>
    /// <exception cref="ArgumentNullException">The client or options is null.</exception>
    /// <exception cref="ArgumentException">Credentials, the model identifier, or the endpoint is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The token limit or temperature is outside its supported range.</exception>
    public OpenAIModel(HttpClient client, OpenAIModelOptions options)
    {
        Guard.NotNull(client, nameof(client));
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (options.ApiKeyProvider is null) Guard.NotNullOrWhiteSpace(options.ApiKey, nameof(options.ApiKey));
        if (options.MaxTokens is <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxTokens must be positive.");
        if (options.Temperature is { } temperature && (!Guard.IsFinite(temperature) || temperature is < 0 or > 2))
            throw new ArgumentOutOfRangeException(nameof(options), "Temperature must be between 0 and 2.");
        ProviderConfiguration.Range(options.TopP, 0, 1, nameof(options.TopP));
        ProviderConfiguration.Range(options.FrequencyPenalty, -2, 2, nameof(options.FrequencyPenalty));
        ProviderConfiguration.Range(options.PresencePenalty, -2, 2, nameof(options.PresencePenalty));
        _parameters = ProviderConfiguration.Snapshot(options.Params);
        _client = client;
        _options = options;
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "chat/completions", "https://api.openai.com/v1/chat/completions");
    }

    /// <summary>Streams text and function calls, including parallel tool-call argument deltas.</summary>
    /// <param name="request">The conversation, system instructions, and function specifications.</param>
    /// <param name="cancellationToken">Cancellation for the HTTP request and incremental response reads.</param>
    /// <returns>Indexed model events, with a stop event after the provider's stream terminator.</returns>
    /// <exception cref="ModelException">The provider fails or the stream is malformed or incomplete.</exception>
    /// <exception cref="NotSupportedException">A message contains a block the Chat Completions API cannot represent.</exception>
    /// <exception cref="OperationCanceledException">The request is cancelled.</exception>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var value in configured.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(FormatRequest(request)) };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await ProviderHttp.ResolveApiKeyAsync(_options.ApiKey, _options.ApiKeyProvider, cancellationToken).ConfigureAwait(false));
        httpRequest.Headers.Accept.Add(new("text/event-stream"));
        var response = await ProviderHttp.SendAsync(_client, httpRequest, "OpenAI", cancellationToken).ConfigureAwait(false);
        IAsyncEnumerator<SseFrame>? frames = null;
        Exception? primary = null;
        try
        {
            try
            {
                var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                frames = ServerSentEvents.ReadAsync(stream, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            var state = new ChatCompletionsStreamState("OpenAI");
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                bool complete;
                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false)) throw new ModelException("OpenAI stream ended without its [DONE] terminator.");
                    var frame = frames.Current;
                    complete = frame.Data == "[DONE]";
                    if (complete) events = [state.Complete()];
                    else
                    {
                        using var document = ProviderHttp.Parse(frame.Data, "OpenAI");
                        events = state.Process(document.RootElement);
                    }
                }
                catch (Exception error) { primary = error; throw; }
                foreach (var item in events) yield return item;
                if (complete) yield break;
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
    }

    private static void AttachCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!errors.Any(value => ReferenceEquals(value, cleanup))) errors.Add(cleanup);
    }

    private Dictionary<string, object?> FormatRequest(ModelRequest request)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        ProviderConfiguration.ValidateToolChoice(request);
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var message in request.Messages) FormatMessage(message, messages);
        var body = ProviderConfiguration.Parameters(_parameters);
        body["model"] = _options.ModelId;
        body["messages"] = messages;
        body["stream"] = true;
        body["stream_options"] = new { include_usage = true };
        if (_options.MaxTokens is { } maxTokens) body["max_completion_tokens"] = maxTokens;
        if (_options.Temperature is { } temperature) body["temperature"] = temperature;
        if (_options.TopP is { } topP) body["top_p"] = topP;
        if (_options.FrequencyPenalty is { } frequency) body["frequency_penalty"] = frequency;
        if (_options.PresencePenalty is { } presence) body["presence_penalty"] = presence;
        if (request.Tools is { Count: > 0 })
            body["tools"] = request.Tools.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema } }).ToArray();
        if (request.ToolChoice is { } choice) body["tool_choice"] = choice.Mode switch
        {
            ToolChoiceMode.Auto => "auto",
            ToolChoiceMode.Any => "required",
            ToolChoiceMode.None => "none",
            ToolChoiceMode.Tool => new { type = "function", function = new { name = choice.Name } },
            _ => throw new ArgumentException("Unknown tool-choice mode.", nameof(request))
        };
        if (body.TryGetValue("n", out var choices) && choices is JsonElement { ValueKind: JsonValueKind.Number } count && count.GetDouble() > 1)
            throw new ArgumentException("Streaming multiple choices is not supported.", nameof(request));
        if (_options.CacheConfig is { } cache)
        {
            var key = cache.CacheKey ?? (cache.UseSessionKey && request.SessionId is { } session ? "strands-" + session : null);
            if (!body.ContainsKey("prompt_cache_key") && !string.IsNullOrEmpty(key)) body["prompt_cache_key"] = key;
            if (!body.ContainsKey("prompt_cache_retention") && cache.Ttl is "in_memory" or "24h") body["prompt_cache_retention"] = cache.Ttl;
        }
        return body;
    }

    private static void FormatMessage(Message message, List<object> output)
    {
        if (message.Role == Role.User) { FormatUserMessage(message, output); return; }
        var text = new StringBuilder();
        var calls = new List<object>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock plain:
                    text.Append(plain.Text);
                    break;
                case CitationsBlock citations:
                    text.Append(citations.Text);
                    break;
                case ToolUseBlock call when message.Role == Role.Assistant && call.Input.ValueKind == JsonValueKind.Object:
                    calls.Add(new { id = call.ToolUseId, type = "function", function = new { name = call.Name, arguments = call.Input.GetRawText() } });
                    break;
                case ReasoningBlock reasoning:
                    text.Append(reasoning.Text);
                    break;
                case CachePointBlock:
                    break;
                default:
                    throw new NotSupportedException($"OpenAI Chat Completions cannot represent {block.GetType().Name} in a {message.Role} message.");
            }
        }
        if (message.Role == Role.Assistant)
        {
            var assistant = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = text.Length == 0 ? null : text.ToString() };
            if (calls.Count > 0) assistant["tool_calls"] = calls;
            output.Add(assistant);
        }
    }

    private static void FormatUserMessage(Message message, List<object> output)
    {
        var deferredMedia = new List<object>();
        foreach (var result in message.Content.OfType<ToolResultBlock>())
        {
            var text = new StringBuilder();
            var media = new List<object>();
            var blocks = result.ContentBlocks.Count > 0 ? result.ContentBlocks : new ContentBlock[] { new TextBlock(result.Content) };
            foreach (var block in MediaContent.ExpandDocuments(blocks, plainText: true))
            {
                if (block is TextBlock plain) text.Append(plain.Text);
                else if (block is JsonBlock json) text.Append(json.Json.GetRawText());
                else if (block is ImageBlock or AudioBlock or DocumentBlock) media.Add(FormatUserContent(block));
                else if (block is ResourceLinkBlock link) text.Append(JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, description = link.Description, mimeType = link.MediaType }));
                else throw new NotSupportedException($"OpenAI cannot represent {block.GetType().Name} in a tool result.");
            }
            if (result.StructuredContent is { } structured && !blocks.OfType<JsonBlock>().Any(block => block.Json.GetRawText() == structured.GetRawText())) text.Append(structured.GetRawText());
            var value = text.ToString();
            if (string.IsNullOrWhiteSpace(value) && media.Count > 0) value = "Tool successfully returned an image. The image is being provided in the following user message.";
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Tool result for {result.ToolUseId} has empty content.", nameof(message));
            output.Add(new { role = "tool", tool_call_id = result.ToolUseId, content = result.Status == ToolResultStatus.Error ? "[ERROR] " + value : value });
            if (media.Count > 0) deferredMedia.Add(new { role = "user", content = media });
        }
        output.AddRange(deferredMedia);
        var content = MediaContent.ExpandDocuments(message.Content.Where(block => block is not (ToolResultBlock or CachePointBlock)), plainText: true).ToArray();
        if (content.Length == 0) { if (message.Content.Count == 0) output.Add(new { role = "user", content = "" }); return; }
        if (content.All(block => block is TextBlock)) output.Add(new { role = "user", content = string.Concat(content.Cast<TextBlock>().Select(block => block.Text)) });
        else output.Add(new { role = "user", content = content.Select(FormatUserContent).ToArray() });
    }

    private static object FormatUserContent(ContentBlock block) => block switch
    {
        TextBlock text => new { type = "text", text = text.Text },
        JsonBlock json => new { type = "text", text = json.Json.GetRawText() },
        ImageBlock image => new { type = "image_url", image_url = new { url = ProviderConfiguration.DataUrl(image) } },
        AudioBlock audio => new { type = "input_audio", input_audio = new { data = MediaContent.InlineData(audio, "OpenAI"), format = ProviderConfiguration.AudioFormat(audio) } },
        DocumentBlock document => new { type = "file", file = new { file_data = $"data:{document.MediaType};base64,{MediaContent.InlineData(document, "OpenAI")}", filename = document.Name } },
        ResourceLinkBlock link => new { type = "text", text = JsonSerializer.Serialize(new { uri = link.Uri, name = link.Name, description = link.Description, mimeType = link.MediaType }) },
        _ => throw new NotSupportedException($"OpenAI cannot represent {block.GetType().Name} in user content.")
    };
}
