using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>A llama.cpp HTTP adapter that borrows the caller's transport.</summary>
public sealed class LlamaCppModel : IModel, IModelMetadataProvider, IModelTokenCounter, IConfigurableModel<LlamaCppModelOptions>
{
    private readonly HttpClient _client;
    private readonly LlamaCppModelOptions _options;
    private readonly JsonElement? _parameters;
    private readonly Uri _endpoint;
    private readonly Uri _tokenEndpoint;
    private readonly ModelMetadata _metadata;
    private LlamaCppModel? _configured;
    /// <summary>Creates a model without taking ownership of the HTTP client.</summary>
    public LlamaCppModel(HttpClient client, LlamaCppModelOptions? options = null)
    {
        Guard.NotNull(client, nameof(client));
        options ??= new();
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options.MaxResponseBytes));
        _client = client;
        _parameters = ProviderConfiguration.Snapshot(options.Params);
        LlamaCppFormatting.ValidateParameters(_parameters);
        _options = options with { Params = _parameters };
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, "/v1/chat/completions", "http://localhost:8080/v1/chat/completions");
        _tokenEndpoint = options.TokenCountEndpoint is { } tokenEndpoint
            ? ProviderHttp.ResolveEndpoint(client, tokenEndpoint, "", "") : new Uri(_endpoint, "/tokenize");
        _metadata = new(options.ModelId, MaxOutputTokens: _parameters is { } parameters && parameters.TryGetProperty("max_tokens", out var maximum) ? maximum.GetInt64() : null);
    }
    /// <inheritdoc/>
    public ModelMetadata Metadata => Volatile.Read(ref _configured)?._metadata ?? _metadata;
    /// <inheritdoc/>
    public LlamaCppModelOptions GetConfig() => Volatile.Read(ref _configured)?._options ?? _options;
    /// <inheritdoc/>
    public void UpdateConfig(Func<LlamaCppModelOptions, LlamaCppModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var options = update(GetConfig());
        Guard.NotNull(options, nameof(update));
        var replacement = new LlamaCppModel(_client, options);
        Interlocked.Exchange(ref _configured, replacement);
    }
    /// <inheritdoc/>
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => StreamCoreAsync(request, null, cancellationToken);
    /// <inheritdoc/>
    public async ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured) return await configured.CountTokensAsync(request, cancellationToken).ConfigureAwait(false);
        if (!_options.UseNativeTokenCount) return ModelExtensions.EstimateInputTokens(request, cancellationToken);
        try
        {
            var payload = new Dictionary<string, object?> { ["messages"] = LlamaCppFormatting.Messages(request) };
            if (request.Tools is { Count: > 0 }) payload["tools"] = LlamaCppFormatting.Tools(request.Tools);
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint) { Content = ProviderHttp.JsonContent(payload) };
            outgoing.Headers.Accept.Add(new("application/json"));
            var response = await ProviderHttp.SendAsync(_client, outgoing, "llama.cpp", cancellationToken, ["application/json"]).ConfigureAwait(false);
            Exception? primary = null;
            try
            {
                var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                using var document = await ProviderHttp.ReadJsonAsync(stream, "llama.cpp", _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array)
                    throw new ModelException("llama.cpp tokenization requires a tokens array.");
                foreach (var token in tokens.EnumerateArray())
                    if (token.ValueKind != JsonValueKind.Number || !token.TryGetInt64(out var id) || id < 0) throw new ModelException("llama.cpp tokenizer returned an invalid token identifier.");
                cancellationToken.ThrowIfCancellationRequested();
                return tokens.GetArrayLength();
            }
            catch (Exception error) { primary = error; throw; }
            finally { DisposeResponse(response, primary); }
        }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SdkLogging.Information("llama.cpp native token counting failed; using the local estimate. {Error}", error.Message);
            return ModelExtensions.EstimateInputTokens(request, cancellationToken);
        }
    }
    /// <summary>Uses llama.cpp's native json_schema constraint and validates the typed JSON response.</summary>
    public async Task<T> InvokeStructuredAsync<T>(ModelRequest request, StructuredOutputOptions<T> options, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured) return await configured.InvokeStructuredAsync(request, options, cancellationToken).ConfigureAwait(false);
        var validator = new StructuredOutputTool<T>(options);
        var accumulator = new ModelStreamAccumulator();
        await foreach (var item in StreamCoreAsync(request with { Tools = null, ToolChoice = null }, options.Schema, cancellationToken).ConfigureAwait(false)) accumulator.Process(item);
        var (message, _) = accumulator.Complete();
        if (message.Content.OfType<ToolUseBlock>().Any()) throw new StructuredOutputException("llama.cpp structured output cannot contain tool calls.");
        try
        {
            using var output = JsonDocument.Parse(message.Text.Trim());
            return validator.ParseOutput(output.RootElement, cancellationToken);
        }
        catch (JsonException error) { throw new StructuredOutputException("llama.cpp structured output contains invalid JSON.", error); }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamCoreAsync(ModelRequest request, JsonElement? schema, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var item in configured.StreamCoreAsync(request, schema, cancellationToken).ConfigureAwait(false)) yield return item;
            yield break;
        }
        var body = LlamaCppFormatting.Request(request, _options, _parameters, schema);
        using var outgoing = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = ProviderHttp.JsonContent(body) };
        outgoing.Headers.Accept.Add(new("text/event-stream"));
        var response = await ProviderHttp.SendAsync(_client, outgoing, "llama.cpp", cancellationToken).ConfigureAwait(false);
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
            var state = new ChatCompletionsStreamState("llama.cpp");
            var normalization = new ChatCompletionsResponseNormalizer("llama.cpp");
            var hasTools = false;
            var pendingTools = new List<ModelStreamEvent>();
            ModelMetadataEvent? metadata = null;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                bool complete;
                try
                {
                    complete = !await frames.MoveNextAsync().ConfigureAwait(false) || frames.Current.Data == "[DONE]";
                    if (complete) events = metadata is null ? [state.Complete()] : [state.Complete(), metadata];
                    else
                    {
                        using var document = ProviderHttp.Parse(frames.Current.Data, "llama.cpp");
                        var decoded = state.Process(NormalizeToolStop(normalization.Normalize(document.RootElement, completeResponse: false), ref hasTools));
                        var ordered = new List<ModelStreamEvent>();
                        foreach (var value in decoded)
                        {
                            if (value is ModelMetadataEvent usage) metadata = usage;
                            else if (value is ModelContentBlockDeltaEvent { Index: 0 } or ModelContentBlockStopEvent { Index: 0 }) ordered.Add(value);
                            else if (value is ModelContentBlockStartEvent { Index: > 0 } or ModelContentBlockDeltaEvent { Index: > 0 } or ModelContentBlockStopEvent { Index: > 0 }) pendingTools.Add(value);
                        }
                        if (document.RootElement.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(value => value.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String))
                        {
                            if (!ordered.OfType<ModelContentBlockStopEvent>().Any(value => value.Index == 0)) ordered.Add(new ModelContentBlockStopEvent(0));
                            foreach (var group in pendingTools.GroupBy(BlockIndex).OrderBy(group => group.Key)) ordered.AddRange(group);
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

    private static int BlockIndex(ModelStreamEvent value) => value switch
    {
        ModelContentBlockStartEvent start => start.Index,
        ModelContentBlockDeltaEvent delta => delta.Index,
        ModelContentBlockStopEvent stop => stop.Index,
        _ => throw new InvalidOperationException("Only content events can be buffered.")
    };
    private static JsonElement NormalizeToolStop(JsonElement root, ref bool hasTools)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return root;
        var normalized = new List<object>();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0) hasTools = true;
            var value = ProviderConfiguration.Parameters(choice);
            // The pinned llama.cpp adapter gives emitted calls precedence over the server's finish reason.
            if (hasTools && ProviderHttp.OptionalString(choice, "finish_reason") is not null) value["finish_reason"] = "tool_calls";
            normalized.Add(value);
        }
        var result = ProviderConfiguration.Parameters(root);
        result["choices"] = normalized;
        return JsonSerializer.SerializeToElement(result);
    }
    private static void DisposeResponse(HttpResponseMessage response, Exception? primary)
    {
        try { response.Dispose(); }
        catch (Exception cleanup) { if (primary is null) throw; AddCleanup(primary, cleanup); }
    }
    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        if (!ReferenceEquals(primary, cleanup)) errors.Add(cleanup);
    }
}
