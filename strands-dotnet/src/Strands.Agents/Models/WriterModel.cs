using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>Native configuration for Writer's chat API.</summary>
public sealed record WriterModelOptions
{
    /// <summary>Creates native Writer options with an explicit model and optional fixed credential.</summary>
    public WriterModelOptions(string modelId, string? apiKey = null) { ModelId = modelId; ApiKey = apiKey; }
    /// <summary>The explicit Palmyra model identifier.</summary>
    public string ModelId { get; init; }
    /// <summary>A fixed bearer credential; null resolves WRITER_API_KEY for each request.</summary>
    public string? ApiKey { get; init; }
    /// <summary>The complete endpoint override.</summary>
    public Uri? Endpoint { get; init; }
    /// <summary>An optional positive output-token bound.</summary>
    public int? MaxTokens { get; init; }
    /// <summary>An optional sampling temperature between zero and two.</summary>
    public double? Temperature { get; init; }
    /// <summary>An optional nucleus-sampling probability between zero and one.</summary>
    public double? TopP { get; init; }
    /// <summary>Optional stop sequences, copied when constructing the adapter.</summary>
    public IReadOnlyList<string>? Stop { get; init; }
    /// <summary>Requests a final provider usage chunk; null omits stream_options.</summary>
    public bool? IncludeUsage { get; init; }
    /// <summary>Writer ignores cache controls, matching the pinned provider.</summary>
    public CacheConfig? CacheConfig { get; init; }
    /// <summary>The maximum buffered structured-response bytes, defaulting to 16 MiB.</summary>
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <inheritdoc/>
    public override string ToString() => $"WriterModelOptions {{ ModelId = {ModelId}, Credentials = redacted }}";
}

/// <summary>A native Writer HTTP adapter with caller-owned transport.</summary>
public sealed class WriterModel : IModel, IModelMetadataProvider, IConfigurableModel<WriterModelOptions>
{
    private readonly HttpClient _client;
    private readonly WriterModelOptions _options;
    private readonly Uri _endpoint;
    private readonly IReadOnlyList<string>? _stop;
    private WriterModel? _configured;
    /// <summary>Constructs an adapter without taking ownership of the HTTP client.</summary>
    public WriterModel(HttpClient client, WriterModelOptions options)
    {
        Guard.NotNull(client, nameof(client));
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (options.ApiKey is not null) Guard.NotNullOrWhiteSpace(options.ApiKey, nameof(options.ApiKey));
        if (options.MaxTokens is <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        ProviderConfiguration.Range(options.Temperature, 0, 2, nameof(options.Temperature));
        ProviderConfiguration.Range(options.TopP, 0, 1, nameof(options.TopP));
        if (options.Stop is not null)
        {
            if (options.Stop.Any(value => value is null)) throw new ArgumentException("Stop sequences cannot contain null.", nameof(options));
            _stop = Array.AsReadOnly(options.Stop.ToArray());
        }
        _client = client;
        _options = options;
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "chat", "https://api.writer.com/v1/chat");
    }
    /// <inheritdoc/>
    public ModelMetadata Metadata { get { var options = GetConfig(); return new(options.ModelId, MaxOutputTokens: options.MaxTokens); } }
    /// <inheritdoc/>
    public WriterModelOptions GetConfig()
    {
        var current = Volatile.Read(ref _configured) ?? this;
        return current._options with { Stop = current._stop };
    }
    /// <inheritdoc/>
    public void UpdateConfig(Func<WriterModelOptions, WriterModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new WriterModel(_client, update(GetConfig()));
        Interlocked.Exchange(ref _configured, replacement);
    }

    /// <summary>Requests a schema-validated typed JSON response through Writer's non-streaming response_format protocol.</summary>
    public async Task<T> InvokeStructuredAsync<T>(ModelRequest request, StructuredOutputOptions<T> options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            return await configured.InvokeStructuredAsync(request, options, cancellationToken).ConfigureAwait(false);
        }
        var validator = new StructuredOutputTool<T>(options);
        var body = FormatRequest(request with { Tools = null, ToolChoice = null });
        body["stream"] = false;
        body.Remove("stream_options");
        body["response_format"] = new { type = "json_schema", json_schema = new { schema = options.Schema } };
        using var outgoing = CreateRequest(body);
        outgoing.Headers.Accept.Add(new("application/json"));
        var response = await ProviderHttp.SendAsync(_client, outgoing, "Writer", cancellationToken, ["application/json"]).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = await ProviderHttp.ReadJsonAsync(stream, "Writer", _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                throw new StructuredOutputException("Writer structured output requires exactly one response choice.");
            var message = ProviderHttp.RequiredObject(choices[0], "message");
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null && (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() > 0))
                throw new StructuredOutputException("Writer structured output returned unexpected tool calls.");
            var text = ProviderHttp.OptionalString(message, "content");
            if (string.IsNullOrWhiteSpace(text)) throw new StructuredOutputException("Writer structured output requires JSON text content.");
            try
            {
                using var output = JsonDocument.Parse(text!.Trim());
                return validator.ParseOutput(output.RootElement, cancellationToken);
            }
            catch (JsonException error) { throw new StructuredOutputException("Writer structured output contains invalid JSON.", error); }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try { response.Dispose(); }
            catch (Exception cleanup) { if (primary is null) throw; AddCleanup(primary, cleanup); }
        }
    }

    internal static bool IsContextOverflow(string detail) => new[]
    {
        "this model's maximum context length is", "exceed context limit", "model's maximum context limit",
        "is longer than the model's context length", "prompt is too long", "too many tokens"
    }.Any(value => detail.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <inheritdoc/>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var value in configured.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        using var outgoing = CreateRequest(FormatRequest(request));
        outgoing.Headers.Accept.Add(new("text/event-stream"));
        var response = await ProviderHttp.SendAsync(_client, outgoing, "Writer", cancellationToken).ConfigureAwait(false);
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
            var state = new ChatCompletionsStreamState("Writer");
            var normalization = new ChatCompletionsResponseNormalizer("Writer");
            var pendingTools = new List<ModelStreamEvent>();
            var metadata = new ModelMetadataEvent(new()) { UsageReported = false };
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                bool complete;
                try
                {
                    complete = !await frames.MoveNextAsync().ConfigureAwait(false) || frames.Current.Data == "[DONE]";
                    if (complete) events = [state.Complete(), metadata];
                    else
                    {
                        using var document = ProviderHttp.Parse(frames.Current.Data, "Writer");
                        var decoded = state.Process(normalization.Normalize(document.RootElement, completeResponse: false));
                        var ordered = new List<ModelStreamEvent>();
                        foreach (var value in decoded)
                        {
                            if (value is ModelMetadataEvent usage) metadata = usage;
                            else if (value is ModelContentBlockDeltaEvent { Index: 0 }) ordered.Add(value);
                            else if (value is ModelContentBlockStopEvent { Index: 0 }) ordered.Add(value);
                            else if (value is ModelContentBlockStartEvent { Index: > 0 } || value is ModelContentBlockDeltaEvent { Index: > 0 } || value is ModelContentBlockStopEvent { Index: > 0 }) pendingTools.Add(value);
                        }
                        if (document.RootElement.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(value => value.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String))
                        {
                            if (!ordered.OfType<ModelContentBlockStopEvent>().Any(value => value.Index == 0)) ordered.Add(new ModelContentBlockStopEvent(0));
                            foreach (var group in pendingTools.GroupBy(value => value switch { ModelContentBlockStartEvent start => start.Index, ModelContentBlockDeltaEvent delta => delta.Index, ModelContentBlockStopEvent stop => stop.Index, _ => throw new InvalidOperationException() }).OrderBy(group => group.Key)) ordered.AddRange(group);
                            pendingTools.Clear();
                        }
                        events = ordered;
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

    private HttpRequestMessage CreateRequest(object body)
    {
        var key = _options.ApiKey ?? Environment.GetEnvironmentVariable("WRITER_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new ModelException("Writer requires an API key or WRITER_API_KEY.");
        return new(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(body), Headers = { Authorization = new AuthenticationHeaderValue("Bearer", key) } };
    }

    private Dictionary<string, object?> FormatRequest(ModelRequest request)
    {
        Guard.NotNull(request.Messages, nameof(request.Messages));
        var messages = new List<object>();
        if (!string.IsNullOrEmpty(request.SystemPrompt)) messages.Add(new { role = "system", content = request.SystemPrompt });
        foreach (var message in request.Messages)
        {
            var content = FormatContent(message.Content);
            var calls = message.Content.OfType<ToolUseBlock>().Select(call => new { id = call.ToolUseId, type = "function", function = new { name = call.Name, arguments = call.Input.GetRawText() } }).ToArray();
            if (content is not null || calls.Length > 0)
            {
                var ordinary = new Dictionary<string, object?> { ["role"] = message.Role == Role.User ? "user" : "assistant", ["content"] = content ?? "" };
                if (calls.Length > 0) ordinary["tool_calls"] = calls;
                messages.Add(ordinary);
            }
            foreach (var result in message.Content.OfType<ToolResultBlock>())
            {
                var parts = (result.HasExplicitContent || result.ContentBlocks.Count > 0 ? result.ContentBlocks : [new TextBlock(result.Content)])
                    .Select(part => part is JsonBlock json ? new TextBlock(json.Json.GetRawText()) : part).ToArray();
                var resultContent = FormatContent(parts);
                if (resultContent is not null || result.HasExplicitContent && parts.Length == 0)
                    messages.Add(new { role = "tool", tool_call_id = result.ToolUseId, content = resultContent ?? (_options.ModelId == "palmyra-x5" ? (object)Array.Empty<object>() : "") });
            }
        }
        var body = new Dictionary<string, object?> { ["model"] = _options.ModelId, ["messages"] = messages, ["stream"] = true };
        if (_options.MaxTokens is { } max) body["max_tokens"] = max;
        if (_options.Temperature is { } temperature) body["temperature"] = temperature;
        if (_options.TopP is { } topP) body["top_p"] = topP;
        if (_stop is not null) body["stop"] = _stop;
        if (_options.IncludeUsage is { } include) body["stream_options"] = new { include_usage = include };
        if (request.Tools is { Count: > 0 }) body["tools"] = request.Tools.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema } }).ToArray();
        if (request.ToolChoice is not null) SdkLogging.Warning("Writer ignores per-call tool choice, matching the pinned provider.");
        if (_options.CacheConfig is not null) SdkLogging.Warning("Writer ignores cache controls, matching the pinned provider.");
        return body;
    }

    private object? FormatContent(IReadOnlyList<ContentBlock> content)
    {
        foreach (var block in content)
        {
            MediaContent.RejectS3(block, "Writer");
            MediaContent.RejectGuardSelection(block, "Writer");
        }
        if (_options.ModelId != "palmyra-x5")
        {
            var text = content.Select(part => part switch { TextBlock value => value.Text, CitationsBlock value => value.Text, _ => null }).Where(value => !string.IsNullOrEmpty(value)).ToArray();
            if (text.Length > 1) throw new ArgumentException($"Writer model '{_options.ModelId}' does not support multiple text blocks.", nameof(content));
            return text.SingleOrDefault();
        }
        var blocks = content.Where(part => part is not (ToolUseBlock or ToolResultBlock)).Select(part => part switch
        {
            TextBlock text => (object)new { type = "text", text = text.Text },
            CitationsBlock citations => new { type = "text", text = citations.Text },
            ImageBlock image => new { type = "image_url", image_url = new { url = ProviderConfiguration.DataUrl(image) } },
            _ => throw new NotSupportedException($"Writer vision cannot represent {part.GetType().Name}.")
        }).ToArray();
        return blocks.Length > 0 ? blocks : null;
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!ReferenceEquals(primary, cleanup)) errors.Add(cleanup);
    }
}
