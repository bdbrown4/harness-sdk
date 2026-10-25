using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;

namespace Strands.Harness.Tools;

internal static class DirectWebFetch
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = Timeout.InfiniteTimeSpan };
    internal static async Task<FetchedPage> FetchAsync(string url, WebFetchConfig config, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(config.Timeout);
        var cleanupErrors = new ConcurrentQueue<Exception>();
        try
        {
            var target = new Uri(url);
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.TryAddWithoutValidation("User-Agent", WebFetch.UserAgent);
                var response = await (config.HttpClient ?? SharedClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                using var abort = deadline.Token.Register(() => DisposeResponse(response, cleanupErrors));
                FetchedPage? page = null;
                try
                {
                    var status = (int)response.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                    {
                        if (redirects >= 20) throw new HttpRequestException("web_fetch exceeded twenty redirects.");
                        target = new Uri(target, location);
                        WebFetchContent.ValidateUrl(target.AbsoluteUri);
                    }
                    else
                    {
                        if (!response.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + status.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase);
                        var resolved = response.RequestMessage?.RequestUri?.AbsoluteUri ?? target.AbsoluteUri;
                        WebFetchContent.ValidateUrl(resolved);
                        var content = response.Content;
                        if (content is null) page = new(resolved, "");
                        else
                        {
#if NET8_0_OR_GREATER
                            using var stream = await content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
#else
                            using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
                            using var bytes = new MemoryStream();
                            var buffer = new byte[16384];
                            while (bytes.Length < WebFetch.MaxBytes)
                            {
                                var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, WebFetch.MaxBytes - bytes.Length), deadline.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                bytes.Write(buffer, 0, read);
                            }
                            deadline.Token.ThrowIfCancellationRequested();
                            page = WebFetchContent.Convert(bytes.ToArray(), content.Headers.ContentType?.ToString() ?? "", resolved, url);
                        }
                    }
                }
                finally { DisposeResponse(response, cleanupErrors); }
                // Wait for a running cancellation callback before reading its recorded cleanup errors.
                abort.Dispose();
                deadline.Token.ThrowIfCancellationRequested();
                if (cleanupErrors.TryPeek(out var cleanup)) ExceptionDispatchInfo.Capture(cleanup).Throw();
                if (page is not null) return page;
            }
        }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested)
                throw AttachCleanup(new OperationCanceledException("web_fetch was canceled.", error, cancellationToken), cleanupErrors);
            if (deadline.IsCancellationRequested)
                throw AttachCleanup(new TimeoutException("web_fetch gave up after " + config.Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s.", error), cleanupErrors);
            AttachCleanup(error, cleanupErrors);
            throw;
        }
    }

    private static void DisposeResponse(HttpResponseMessage response, ConcurrentQueue<Exception> cleanupErrors)
    {
        try { response.Dispose(); }
        catch (Exception error) { cleanupErrors.Enqueue(error); }
    }

    private static T AttachCleanup<T>(T primary, ConcurrentQueue<Exception> cleanupErrors) where T : Exception
    {
        var errors = cleanupErrors.Where(error => !ReferenceEquals(error, primary)).ToList();
        if (errors.Count > 0) primary.Data["Strands.CleanupErrors"] = errors;
        return primary;
    }
}
