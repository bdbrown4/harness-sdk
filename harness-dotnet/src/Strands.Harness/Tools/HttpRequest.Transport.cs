using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

public static partial class HttpRequest
{
    private static async ValueTask<string> SendAsync(ParsedRequest input, HttpRequestConfig config, CancellationToken executionToken, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        executionToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(callerToken, executionToken, deadline.Token);
        var cleanup = new ConcurrentQueue<Exception>();
        HttpRequestMessage? request = null;
        HttpClient? client = null;
        HttpClientHandler? ownedHandler = null;
        ResponseOwner? owner = null;
        Exception? primary = null;
        string? result = null;
        try
        {
            request = new HttpRequestMessage(new HttpMethod(input.Method), input.Uri);
            if (input.Body is { } body)
            {
                request.Content = new ByteArrayContent(body);
                if (config.Behavior == HttpRequestBehavior.TypeScript) request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain;charset=UTF-8");
            }
            foreach (var header in input.Headers)
            {
                if (request.Headers.TryAddWithoutValidation(header.Key, header.Value)) continue;
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.Remove(header.Key);
                if (!request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw new ArgumentException("Unavailable request header: " + header.Key);
            }
            client = config.HttpClient;
            if (client is null)
            {
                // Keep handler ownership explicit even if setting a BCL property or creating
                // the client fails before the connection is admitted.
                ownedHandler = new HttpClientHandler();
                ownedHandler.AllowAutoRedirect = config.Behavior == HttpRequestBehavior.TypeScript;
                ownedHandler.MaxAutomaticRedirections = 20;
                ownedHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                ownedHandler.UseCookies = false;
                ownedHandler.MaxResponseHeadersLength = Math.Max(1, (int)Math.Min(int.MaxValue, ((long)config.MaximumHeaderBytes + 1023) / 1024));
                client = new HttpClient(ownedHandler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            }
            if (input.Timeout is { } timeout) deadline.CancelAfter(timeout);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token).ConfigureAwait(false);
            owner = new(response, cleanup);
            var headers = ProjectHeaders(response, config);
            var status = (int)response.StatusCode;
            if (config.Behavior == HttpRequestBehavior.Python && status >= 400) throw StatusError(response, input);
            var bytes = response.Content is { } content ? await ReadBodyAsync(content, owner, config.MaximumResponseBodyBytes, lifetime.Token).ConfigureAwait(false) : Array.Empty<byte>();
            lifetime.Token.ThrowIfCancellationRequested();
            if (config.Behavior == HttpRequestBehavior.TypeScript && !response.IsSuccessStatusCode) throw StatusError(response, input);
            var text = Decode(bytes, response.Content?.Headers.ContentType?.CharSet, config.Behavior);
            result = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["status"] = status,
                [config.Behavior == HttpRequestBehavior.Python ? "status_text" : "statusText"] = response.ReasonPhrase ?? "",
                ["headers"] = headers,
                ["body"] = text
            });
        }
        catch (Exception error) { primary = Normalize(error); }
        finally
        {
            owner?.Dispose();
            try { request?.Dispose(); } catch (Exception error) { cleanup.Enqueue(error); }
            if (config.HttpClient is null) { try { client?.Dispose(); } catch (Exception error) { cleanup.Enqueue(error); } }
            try { ownedHandler?.Dispose(); } catch (Exception error) { cleanup.Enqueue(error); }
        }
        if (primary is null && lifetime.IsCancellationRequested) primary = Normalize(new OperationCanceledException(lifetime.Token));
        if (primary is null && cleanup.TryDequeue(out var first)) primary = first;
        if (primary is not null)
        {
            AttachCleanup(primary, cleanup);
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        return result!;

        Exception Normalize(Exception error)
        {
            if (callerToken.IsCancellationRequested)
                return error is OperationCanceledException canceled && canceled.CancellationToken == callerToken ? error : new OperationCanceledException("HTTP request canceled by its caller.", error, callerToken);
            if (executionToken.IsCancellationRequested)
                return error is OperationCanceledException canceled && canceled.CancellationToken == executionToken ? error : new OperationCanceledException("HTTP tool execution canceled.", error, executionToken);
            if (deadline.IsCancellationRequested) return new TimeoutException("HTTP request exceeded its complete-operation deadline.", error);
            return error;
        }
    }

    private static HttpRequestException StatusError(HttpResponseMessage response, ParsedRequest input) => new("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + ": " + input.Method + " " + input.Uri.AbsoluteUri);

    private static Dictionary<string, string> ProjectHeaders(HttpResponseMessage response, HttpRequestConfig config)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        long count = 0;
        Add(response.Headers);
        if (response.Content is { } content) Add(content.Headers);
        return headers;

        void Add(IEnumerable<KeyValuePair<string, IEnumerable<string>>> values)
        {
            foreach (var header in values)
            {
                var key = header.Key.ToLowerInvariant();
                var parts = header.Value.ToArray();
                foreach (var value in parts) count += Encoding.UTF8.GetByteCount(key) + (long)Encoding.UTF8.GetByteCount(value) + 4;
                if (count > config.MaximumHeaderBytes) throw new HttpRequestException("HTTP response headers exceed their projection byte limit.");
                headers[key] = config.Behavior == HttpRequestBehavior.Python ? string.Join("\n", parts)
                    : key == "set-cookie" ? parts.LastOrDefault() ?? "" : string.Join(", ", parts);
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, ResponseOwner owner, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Headers.ContentLength is { } length && length > limit) throw new HttpRequestException("HTTP response body exceeds its byte limit.");
        // Parameterless Open is deliberately physically joined on all assets. Closing content
        // while Open is pending loses late stream ownership on the characterized public BCLs.
        var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var abort = cancellationToken.Register(owner.Dispose);
        cancellationToken.ThrowIfCancellationRequested();
        using var bytes = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, limit + 1L - bytes.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            bytes.Write(buffer, 0, read);
            if (bytes.Length > limit) throw new HttpRequestException("HTTP response body exceeds its byte limit.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return bytes.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset, HttpRequestBehavior behavior)
    {
        if (behavior == HttpRequestBehavior.TypeScript)
        {
            var skip = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
        }
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset!.Trim('"'), EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("\uFFFD")); }
            catch (ArgumentException) { }
        }
        return encoding.GetString(bytes, 0, bytes.Length);
    }

    private sealed class ResponseOwner(HttpResponseMessage response, ConcurrentQueue<Exception> errors)
    {
        private int _disposed;
        internal void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { response.Dispose(); } catch (Exception error) { errors.Enqueue(error); }
        }
    }

    private static void AttachCleanup(Exception primary, IEnumerable<Exception> failures)
    {
        var values = primary.Data["Strands.CleanupErrors"] as List<Exception>;
        foreach (var failure in failures)
        {
            if (ReferenceEquals(primary, failure) || values?.Any(value => ReferenceEquals(value, failure)) == true) continue;
            values ??= new();
            values.Add(failure);
        }
        if (values is { Count: > 0 }) primary.Data["Strands.CleanupErrors"] = values;
    }
}
