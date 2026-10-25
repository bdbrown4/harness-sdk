using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Strands.Agents.Models;

/// <summary>A native Gemini GenerateContent adapter using a caller-owned HttpClient and explicit Developer or Vertex-compatible configuration.</summary>
public sealed partial class GoogleModel : IModel, IModelMetadataProvider, IModelTokenCounter, IConfigurableModel<GoogleModelOptions>
{
    private readonly HttpClient _client;
    private readonly GoogleModelOptions _options;
    private readonly JsonElement? _parameters;
    private readonly JsonElement? _generation;
    private readonly IReadOnlyList<JsonElement> _builtIns;
    private readonly Uri _endpoint;
    private readonly Uri? _countEndpoint;
    private readonly ModelMetadata _metadata;
    private GoogleModel? _configured;
    /// <inheritdoc/>
    public GoogleModelOptions GetConfig()
    {
        var current = Volatile.Read(ref _configured) ?? this;
        return current._options with { Params = current._parameters, GenerationConfig = current._generation, BuiltInTools = current._builtIns };
    }
    /// <inheritdoc/>
    public void UpdateConfig(Func<GoogleModelOptions, GoogleModelOptions> update)
    {
        Guard.NotNull(update, nameof(update));
        var replacement = new GoogleModel(_client, update(GetConfig()));
        Interlocked.Exchange(ref _configured, replacement);
    }
    /// <inheritdoc/>
    public ModelMetadata Metadata => (Volatile.Read(ref _configured) ?? this)._metadata;

    internal static ModelException CreateError(JsonElement root, HttpStatusCode? status = null, Exception? cause = null)
    {
        var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested) ? nested : root;
        var code = ProviderHttp.OptionalString(error, "status");
        var detail = ProviderHttp.OptionalString(error, "message") ?? "The Google provider rejected the request.";
        if ((int?)status is 429 or 503 || code is "RESOURCE_EXHAUSTED" or "UNAVAILABLE") return new ModelThrottledException("Google: " + detail, cause);
        if (code == "INVALID_ARGUMENT" && detail.IndexOf("exceeds the maximum number of tokens", StringComparison.OrdinalIgnoreCase) >= 0) return new ContextWindowOverflowException("Google: " + detail, cause);
        return new ModelException("Google: " + detail, cause);
    }
    /// <summary>Creates an adapter without taking ownership of the transport.</summary>
    public GoogleModel(HttpClient client, GoogleModelOptions options)
    {
        Guard.NotNull(client, nameof(client));
        Guard.NotNull(options, nameof(options));
        Guard.NotNullOrWhiteSpace(options.ModelId, nameof(options.ModelId));
        if (!options.UseVertexAi) Guard.NotNullOrWhiteSpace(options.ApiKey, nameof(options.ApiKey));
        if (options.UseVertexAi && options.Endpoint is null) throw new ArgumentException("Vertex mode requires an explicit streaming Endpoint and a caller-configured authentication transport.", nameof(options));
        Guard.NotNull(options.BuiltInTools, nameof(options.BuiltInTools));
        if (options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "MaxResponseBytes must be positive.");
        _client = client; _options = options;
        _parameters = ProviderConfiguration.Snapshot(options.Params);
        _generation = ProviderConfiguration.Snapshot(options.GenerationConfig);
        _builtIns = Array.AsReadOnly(options.BuiltInTools.Select(tool => tool.ValueKind == JsonValueKind.Object && !tool.TryGetProperty("functionDeclarations", out _) ? tool.Clone() : throw new ArgumentException("BuiltInTools cannot contain functionDeclarations or non-object values.", nameof(options))).ToArray());
        var model = options.ModelId.StartsWith("models/", StringComparison.Ordinal) ? options.ModelId.Substring(7) : options.ModelId;
        var path = "models/" + Uri.EscapeDataString(model);
        _endpoint = ProviderHttp.ResolveEndpoint(client, options.Endpoint, path + ":streamGenerateContent?alt=sse", "https://generativelanguage.googleapis.com/v1beta/" + path + ":streamGenerateContent?alt=sse");
        if (options.StructuredOutputEndpoint is { } structuredEndpoint) _structuredEndpoint = ProviderHttp.ResolveEndpoint(client, structuredEndpoint, "", "");
        if (options.TokenCountEndpoint is { } explicitCount) _countEndpoint = ProviderHttp.ResolveEndpoint(client, explicitCount, "", "");
        else if (options.Endpoint is null) _countEndpoint = ProviderHttp.ResolveEndpoint(client, null, path + ":countTokens", "https://generativelanguage.googleapis.com/v1beta/" + path + ":countTokens");
        else if (_endpoint.AbsolutePath.EndsWith(":streamGenerateContent", StringComparison.Ordinal)) _countEndpoint = new UriBuilder(_endpoint) { Path = _endpoint.AbsolutePath.Substring(0, _endpoint.AbsolutePath.Length - ":streamGenerateContent".Length) + ":countTokens", Query = "" }.Uri;
        else if (options.UseNativeTokenCount) throw new ArgumentException("A custom streaming URL requires TokenCountEndpoint when native counting is enabled.", nameof(options));
        var merged = GoogleFormatting.Generation(_parameters, _generation);
        var maximum = merged.TryGetValue("maxOutputTokens", out var value) && value is JsonElement token ? token.GetInt64() : (long?)null;
        _metadata = new(options.ModelId, MaxOutputTokens: maximum);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _configured) is { } configured)
        {
            await foreach (var value in configured.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return value;
            yield break;
        }
        using var message = CreateRequest(_endpoint, GoogleFormatting.Request(request, _parameters, _generation, _builtIns, _options.UseVertexAi), "text/event-stream");
        var response = await SendAsync(message, "text/event-stream", cancellationToken).ConfigureAwait(false);
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
            var state = new GoogleStreaming();
            while (true)
            {
                IReadOnlyList<ModelStreamEvent> events;
                bool hasFrame;
                try
                {
                    hasFrame = await frames.MoveNextAsync().ConfigureAwait(false);
                    if (hasFrame)
                    {
                        using var document = ProviderHttp.Parse(frames.Current.Data, "Google");
                        events = state.Process(document.RootElement);
                    }
                    else events = state.Complete();
                }
                catch (Exception error) { primary = error; throw; }
                foreach (var item in events) yield return item;
                if (!hasFrame) yield break;
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

    /// <inheritdoc/>
    public async ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _configured) is { } configured) return await configured.CountTokensAsync(request, cancellationToken).ConfigureAwait(false);
        Guard.NotNull(request, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.UseNativeTokenCount) return ModelExtensions.EstimateInputTokens(request, cancellationToken);
        var contents = GoogleFormatting.Messages(request.Messages);
        try
        {
            var count = await CountNativeAsync(contents, cancellationToken).ConfigureAwait(false);
            var overhead = ModelExtensions.EstimateInputTokens(new([], request.SystemPrompt, request.Tools), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return checked(count + overhead);
        }
        catch (Exception error) when (error is ModelException or IOException or HttpRequestException or OverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SdkLogging.Information("Google native token counting failed; using the upstream estimate. {Error}", error.Message);
            return ModelExtensions.EstimateInputTokens(request, cancellationToken);
        }
    }

    private async Task<long> CountNativeAsync(List<object> contents, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(_countEndpoint!, new { contents }, "application/json");
        var response = await SendAsync(request, "application/json", cancellationToken).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            var data = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("totalTokens", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0) throw new ModelException("Google countTokens must return a nonnegative integer totalTokens.");
            return count;
        }
        catch (Exception error) { primary = error; throw; }
        finally { DisposeResponse(response, primary); }
    }

    private HttpRequestMessage CreateRequest(Uri endpoint, object body, string mediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = ProviderHttp.JsonContent(body) };
        if (!_options.UseVertexAi) request.Headers.Add("x-goog-api-key", _options.ApiKey);
        request.Headers.Accept.Add(new(mediaType));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string mediaType, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException error) { throw new ModelException("Google request failed.", error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw new ModelException("Google request timed out.", error); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("The Google request was cancelled.", error, cancellationToken); }
        if (response.IsSuccessStatusCode && string.Equals(response.Content?.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)) return response;
        Exception? primary = null;
        try
        {
            if (response.IsSuccessStatusCode) throw new ModelException("Google returned a response that is not " + mediaType + ".");
            JsonElement detail;
            try { detail = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is ModelException or IOException) { detail = default; }
#if NETSTANDARD2_0
            var cause = new HttpRequestException("Google returned HTTP " + (int)response.StatusCode + ".");
#else
            var cause = new HttpRequestException("Google returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
#endif
            throw CreateError(detail, response.StatusCode, cause);
        }
        catch (Exception error) { primary = error; throw; }
        finally { DisposeResponse(response, primary); }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await ProviderHttp.ReadBytesAsync(stream, buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (body.Length + read > 16 * 1024) throw new ModelException("Google JSON response exceeded the bounded token-count/error response size.");
            body.Write(buffer, 0, read);
        }
        try { using var document = JsonDocument.Parse(body.ToArray()); return document.RootElement.Clone(); }
        catch (JsonException error) { throw new ModelException("Google returned malformed JSON.", error); }
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
        errors.Add(cleanup);
    }
}
