using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Models;

internal static class ProviderHttp
{
    internal static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });

    internal static HttpContent JsonContent(object value) => new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    internal static async Task<JsonDocument> ReadJsonAsync(Stream stream, string provider, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await ReadBytesAsync(stream, bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > maxBytes) throw new ModelException($"{provider}'s complete JSON response exceeds MaxResponseBytes.");
            buffer.Write(bytes, 0, count);
        }
        try { return JsonDocument.Parse(buffer.ToArray()); }
        catch (JsonException error) { throw new ModelException($"{provider} returned invalid JSON.", error); }
    }

    internal static async ValueTask<string> ResolveApiKeyAsync(string fixedKey, Func<CancellationToken, ValueTask<string>>? provider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = fixedKey;
        if (provider is not null)
        {
            var resolving = provider(cancellationToken).AsTask();
            try
            {
                await TaskCancellation.WaitAsync(resolving, cancellationToken).ConfigureAwait(false);
                key = await resolving.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = ObserveLateCredentialAsync(resolving);
                throw;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(key)) throw new ModelException("The bearer credential provider returned an empty credential.");
        return key;
    }

    private static async Task ObserveLateCredentialAsync(Task<string> resolving)
    {
        try { await resolving.ConfigureAwait(false); }
        catch { /* Cancellation already won the caller's wait; observe any eventual provider failure. */ }
    }

    internal static async Task<Stream> OpenBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if NETSTANDARD2_0
        var opening = content.ReadAsStreamAsync();
#else
        var opening = content.ReadAsStreamAsync(cancellationToken);
#endif
        try
        {
            // Custom content can ignore even the modern overload's token. Cancel the wait separately.
            await TaskCancellation.WaitAsync(opening, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await opening.ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            var canceled = new CanceledProviderOperationException("The provider response body opening was cancelled.", error, cancellationToken);
            // The response owner still disposes content. If opening is unfinished, its future stream
            // needs a second owner: observe every late fault and close any late result without delay.
            // This no-fault task returns cleanup diagnostics; it never mutates Data after return.
            var cleanup = CleanupCanceledBodyOpenAsync(opening);
            canceled.Data["Strands.CleanupTask"] = cleanup;
            if (cleanup.IsCompleted)
            {
                var errors = await cleanup.ConfigureAwait(false);
                if (errors.Count > 0) canceled.Data["Strands.CleanupErrors"] = errors.ToList();
            }
            throw canceled;
        }
    }

    private sealed class CanceledProviderOperationException(string message, OperationCanceledException cause, CancellationToken cancellationToken)
        : OperationCanceledException(message, cause, cancellationToken)
    {
        // Framework's default Exception.Data rejects Task values as nonserializable. This runtime-only
        // diagnostic needs an ordinary dictionary; exception binary serialization is not supported.
        public override System.Collections.IDictionary Data { get; } = new System.Collections.Hashtable();
    }

    private static async Task<IReadOnlyList<Exception>> CleanupCanceledBodyOpenAsync(Task<Stream> opening)
    {
        Stream stream;
        try
        {
            stream = await opening.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Array.Empty<Exception>(); }
        catch (Exception error) { return Array.AsReadOnly(new[] { error }); }
        try { stream.Dispose(); return Array.Empty<Exception>(); }
        catch (Exception error) { return Array.AsReadOnly(new[] { error }); }
    }

    internal static async Task<int> ReadBytesAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // HTTP streams can ignore read cancellation; closing this owned body also interrupts the pending read.
        var cancellation = new CancelledRead(stream);
        using var registration = cancellationToken.Register(value => ((CancelledRead)value!).Close(), cancellation);
        Exception? primary = null;
        var read = 0;
        try
        {
#if NETSTANDARD2_0
            read = await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
#else
            read = await stream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (Exception error) { primary = error; }
        // Dispose waits for any running callback before its diagnostic state is inspected.
        registration.Dispose();
        if (cancellationToken.IsCancellationRequested && primary is null or OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException)
            primary = primary is OperationCanceledException cancelled && cancelled.CancellationToken == cancellationToken ? cancelled : new OperationCanceledException("The provider response read was cancelled.", primary, cancellationToken);
        if (cancellation.Failure is { } cleanup)
        {
            if (primary is null) primary = cleanup;
            else
            {
                const string key = "Strands.CleanupErrors";
                if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
                errors.Add(cleanup);
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        return read;
    }

    private sealed class CancelledRead(Stream stream)
    {
        internal Exception? Failure { get; private set; }
        internal void Close()
        {
            try { stream.Dispose(); }
            catch (Exception error) { Failure = error; }
        }
    }

    internal static Uri ResolveEndpoint(HttpClient client, Uri? endpoint, string relativePath, string defaultEndpoint)
    {
        if (endpoint is not null)
        {
            if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
                throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
            return endpoint;
        }
        return client.BaseAddress is null ? new(defaultEndpoint) : new(client.BaseAddress, relativePath);
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string provider, CancellationToken cancellationToken, IReadOnlyList<string>? acceptedContentTypes = null)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new ModelException($"{provider} request failed.", error);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            if (error.CancellationToken == cancellationToken) throw;
            var cancelled = new CanceledProviderOperationException($"{provider} request was cancelled.", error, cancellationToken);
            foreach (System.Collections.DictionaryEntry diagnostic in error.Data) cancelled.Data[diagnostic.Key] = diagnostic.Value;
            throw cancelled;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelException($"{provider} request timed out.", error);
        }

        Exception? primary = null;
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                var (body, readFailure) = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
#if NETSTANDARD2_0
                var cause = new HttpRequestException($"{provider} returned HTTP {(int)response.StatusCode}.", readFailure);
#else
                var cause = new HttpRequestException($"{provider} returned HTTP {(int)response.StatusCode}.", readFailure, response.StatusCode);
#endif
                throw CreateError(provider, body, response.StatusCode, cause);
            }
            if (!(acceptedContentTypes ?? new[] { "text/event-stream" }).Contains(response.Content?.Headers.ContentType?.MediaType, StringComparer.OrdinalIgnoreCase))
                throw new ModelException($"{provider} returned a response that is not {(acceptedContentTypes is null ? "text/event-stream" : string.Join(" or ", acceptedContentTypes))}.");
            return response;
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            if (primary is not null)
            {
                try { response.Dispose(); }
                catch (Exception cleanup)
                {
                    const string key = "Strands.CleanupErrors";
                    if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
                    errors.Add(cleanup);
                }
            }
        }
    }

    private static async Task<(JsonElement Body, Exception? Failure)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is not { } content) return (default, null);
        try
        {
            // The response owns this stream; closing it once preserves both classification and cleanup diagnostics.
            var stream = await OpenBodyAsync(content, cancellationToken).ConfigureAwait(false);
            var bytes = new byte[16 * 1024];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = await ReadBytesAsync(stream, bytes, read, bytes.Length - read, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, read));
            return (document.RootElement.Clone(), null);
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException)
        {
            return (default, error);
        }
    }

    internal static ModelException CreateError(string provider, JsonElement root, HttpStatusCode? status = null, Exception? cause = null)
    {
        var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested) ? nested : root;
        var code = OptionalString(error, "code") ?? OptionalString(error, "type") ?? string.Empty;
        var detail = error.ValueKind == JsonValueKind.String ? error.GetString()! : OptionalString(error, "message") ?? "The provider rejected the request.";
        var message = $"{provider}: {detail}";
        if (provider == "Llama API" && status == HttpStatusCode.BadRequest && LlamaApiModel.IsContextOverflow(detail)) return new ContextWindowOverflowException(message, cause);
        if (provider == "llama.cpp" && status == HttpStatusCode.ServiceUnavailable) return new ModelThrottledException(message, cause);
        if (provider == "llama.cpp" && status == HttpStatusCode.BadRequest && new[] { "context", "kv cache", "slot" }.Any(term => detail.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)) return new ContextWindowOverflowException(message, cause);
        if ((int?)status is 429 or 529 || code is "rate_limit_error" or "rate_limit_exceeded" or "overloaded_error")
            return new ModelThrottledException(message, cause);
        if (provider == "OpenAI Responses" && OpenAIResponsesModel.IsApiThrottling(code, detail)) return new ModelThrottledException(message, cause);
        if (provider == "OpenAI Responses" && OpenAIResponsesModel.IsApiContextOverflow(code, detail)) return new ContextWindowOverflowException(message, cause);
        if (provider == "Ollama" && OllamaModel.IsContextOverflow(detail) || provider == "Mistral" && MistralModel.IsContextOverflow(detail) || provider == "Writer" && WriterModel.IsContextOverflow(detail) || code is "context_length_exceeded" or "prompt_too_long" ||
            (code == "invalid_request_error" && (detail.IndexOf("prompt is too long", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("context window", StringComparison.OrdinalIgnoreCase) >= 0)))
            return new ContextWindowOverflowException(message, cause);
        return new ModelException(message, cause);
    }

    internal static JsonDocument Parse(string json, string provider)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw new ModelException($"{provider} returned a non-object stream event.");
        }
        catch (JsonException error)
        {
            throw new ModelException($"{provider} returned malformed JSON in a stream event.", error);
        }
    }

    internal static string? OptionalString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new ModelException($"Provider field '{name}' must be a string.");
        return property.GetString();
    }

    internal static string RequiredString(JsonElement value, string name)
    {
        var text = OptionalString(value, name);
        return !string.IsNullOrEmpty(text) ? text! : throw new ModelException($"Provider event is missing '{name}'.");
    }

    internal static JsonElement RequiredObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
            throw new ModelException($"Provider field '{name}' must be an object.");
        return property;
    }

    internal static int RequiredIndex(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("index", out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var index) || index < 0)
            throw new ModelException("Provider event is missing a nonnegative index.");
        return index;
    }

    internal static long TokenCount(JsonElement value, string name, long fallback = 0)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return fallback;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var count) || count < 0)
            throw new ModelException($"Provider token count '{name}' must be a nonnegative integer.");
        return count;
    }

    internal static void ValidateToolInput(string json, string provider)
    {
        using var document = Parse(json, provider);
    }
}

internal sealed record SseFrame(string? Event, string Data);

internal static class ServerSentEvents
{
    private const int MaximumFrameCharacters = 4 * 1024 * 1024;

    internal static IAsyncEnumerable<SseFrame> ReadAsync(Stream stream, CancellationToken cancellationToken) => ReadAsync(stream, acceptRawJsonErrors: false, cancellationToken);

    internal static async IAsyncEnumerable<SseFrame> ReadAsync(Stream stream, bool acceptRawJsonErrors, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var encoding = new UTF8Encoding(false, true);
        var decoder = encoding.GetDecoder();
        var bytes = new byte[1024];
        var buffer = new char[encoding.GetMaxCharCount(bytes.Length)];
        var line = new StringBuilder();
        var state = new FrameState(acceptRawJsonErrors);
        var previousCarriageReturn = false;
        var firstCharacter = true;
        while (true)
        {
            int count;
            int bytesRead;
            try
            {
                bytesRead = await ProviderHttp.ReadBytesAsync(stream, bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                count = decoder.GetChars(bytes, 0, bytesRead, buffer, 0, flush: bytesRead == 0);
            }
            catch (Exception error) when (error is IOException or DecoderFallbackException or HttpRequestException)
            {
                throw new ModelException("The provider stream could not be read.", error);
            }
            if (bytesRead == 0)
            {
                if (state.HasData || line.Length > 0) throw new ModelException("The provider stream ended inside a server-sent event.");
                yield break;
            }
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (firstCharacter)
                {
                    firstCharacter = false;
                    if (character == '\uFEFF') continue;
                }
                if (previousCarriageReturn && character == '\n') { previousCarriageReturn = false; continue; }
                previousCarriageReturn = character == '\r';
                if (character is '\r' or '\n')
                {
                    var frame = state.PushLine(line.ToString());
                    line.Clear();
                    if (frame is not null) yield return frame;
                    continue;
                }
                if (line.Length >= MaximumFrameCharacters) throw new ModelException("A provider stream event exceeded the size limit.");
                line.Append(character);
            }
        }
    }

    private sealed class FrameState(bool acceptRawJsonErrors)
    {
        private readonly StringBuilder _data = new();
        private string? _eventName;
        internal bool HasData { get; private set; }

        internal SseFrame? PushLine(string line)
        {
            if (line.Length == 0)
            {
                var frame = HasData ? new SseFrame(_eventName, _data.ToString()) : null;
                _data.Clear();
                _eventName = null;
                HasData = false;
                return frame;
            }
            if (line[0] == ':') return null;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line.Substring(0, separator);
            var value = separator < 0 ? string.Empty : line.Substring(separator + 1);
            if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
            if (field == "event") _eventName = value;
            if (field != "data")
            {
                if (acceptRawJsonErrors && line[0] == '{')
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out _))
                        {
                            _data.Clear(); _eventName = null; HasData = false;
                            return new("error", line);
                        }
                    }
                    catch (JsonException) { }
                return null;
            }
            if (HasData) _data.Append('\n');
            _data.Append(value);
            HasData = true;
            if (_data.Length > MaximumFrameCharacters) throw new ModelException("A provider stream event exceeded the size limit.");
            return null;
        }
    }
}
