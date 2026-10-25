using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Mcp;

/// <summary>Authenticates one MCP resource using the OAuth client_credentials grant and a redirect-disabled owned HTTP transport.</summary>
/// <remarks>The HttpClient that wraps this handler owns it by default. No caller-supplied HTTP client or transport is disposed.</remarks>
public sealed partial class McpClientCredentialsHandler : HttpMessageHandler
{
    private readonly HttpMessageInvoker _transport;
    private readonly Uri _resource;
    private readonly McpClientCredentials _credentials;
    private readonly McpClientCredentialsHandlerOptions _options;
    private readonly SemaphoreSlim _acquisition = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifetime = new();
    private bool _disposed;
    private AccessToken? _token;
    private Discovery? _discovery;
    private string? _requestedScopes;

    /// <summary>Creates an HTTP handler for one absolute HTTP(S) MCP resource.</summary>
    /// <param name="resource">The fixed MCP resource endpoint.</param>
    /// <param name="credentials">Pre-registered credentials, copied by the handler.</param>
    /// <param name="options">Caching and response limits.</param>
    public McpClientCredentialsHandler(Uri resource, McpClientCredentials credentials, McpClientCredentialsHandlerOptions? options = null)
    {
        Guard.NotNull(resource, nameof(resource));
        Guard.NotNull(credentials, nameof(credentials));
        ValidateHttpUri(resource, nameof(resource));
        Guard.NotNull(credentials.ClientId, nameof(credentials));
        Guard.NotNull(credentials.ClientSecret, nameof(credentials));
        Guard.NotNull(credentials.Scopes, nameof(credentials));
        if (credentials.Scopes.Any(scope => scope is null)) throw new ArgumentException("OAuth scopes must be strings.", nameof(credentials));
        options ??= new();
        if (options.MaximumRequestBodyBytes <= 0 || options.MaximumMetadataBytes <= 0 || options.MaximumTokenBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "OAuth byte limits must be positive.");
        _resource = resource;
        _credentials = credentials with { Scopes = Array.AsReadOnly(credentials.Scopes.ToArray()) };
        _options = options with { };
        _transport = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Guard.NotNull(request, nameof(request));
        if (request.RequestUri is not { IsAbsoluteUri: true } target || !SameOrigin(_resource, target) ||
            _resource.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped) != target.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped) ||
            !string.IsNullOrEmpty(target.Fragment) || !string.IsNullOrEmpty(target.UserInfo))
            throw new InvalidOperationException("A client-credentials handler is bound to its configured resource origin, path, and query.");
        CancellationTokenSource operation;
        lock (_lifetime)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(McpClientCredentialsHandler));
            cancellationToken.ThrowIfCancellationRequested();
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        }
        using (operation)
        {
            try
            {
                var snapshot = await CaptureAsync(request, operation.Token).ConfigureAwait(false);
                var used = Volatile.Read(ref _token);
                if (_options.RefreshOnExpiry && used?.ExpiresAt <= DateTimeOffset.UtcNow)
                    used = await AcquireAsync(null, used, operation.Token).ConfigureAwait(false);
                var response = await SendResourceAsync(snapshot, used, request, operation.Token).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return response;
                Challenge challenge;
                try
                {
                    challenge = ParseChallenge(response);
                    if (response.StatusCode == HttpStatusCode.Forbidden && challenge.Error != "insufficient_scope") return response;
                }
                catch { response.Dispose(); throw; }
                response.Dispose();
                var token = await AcquireAsync(challenge, used, operation.Token).ConfigureAwait(false);
                return await SendResourceAsync(snapshot, token, request, operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested && error.CancellationToken != cancellationToken)
            { throw new OperationCanceledException("OAuth resource operation was cancelled.", error, cancellationToken); }
        }
    }

    private async Task<HttpResponseMessage> SendResourceAsync(RequestSnapshot snapshot, AccessToken? token, HttpRequestMessage original, CancellationToken cancellationToken)
    {
        using var message = snapshot.Create();
        if (token is not null) message.Headers.Authorization = new("Bearer", token.Value);
        var response = await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.RequestMessage = original;
        return response;
    }

    private async Task<AccessToken> AcquireAsync(Challenge? challenge, AccessToken? used, CancellationToken cancellationToken)
    {
        await _acquisition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Volatile.Read(ref _token);
            if (current is not null && !ReferenceEquals(current, used) &&
                (!_options.RefreshOnExpiry || current.ExpiresAt is null || current.ExpiresAt > DateTimeOffset.UtcNow) &&
                (challenge?.Error != "insufficient_scope" || IncludesScopes(current.Scopes, challenge.Scope))) return current;
            var discovery = _discovery ?? await DiscoverAsync(challenge, cancellationToken).ConfigureAwait(false);
            var scope = challenge?.Error == "insufficient_scope"
                ? CombineScopes(_requestedScopes, current?.Scopes, challenge.Scope)
                : challenge?.Scope ?? _requestedScopes ?? discovery.Scopes;
            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["resource"] = discovery.Resource.AbsoluteUri,
            };
            if (!string.IsNullOrEmpty(scope)) fields["scope"] = scope!;
            using var message = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint) { Content = new FormUrlEncodedContent(fields) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(_credentials.ClientId) + ":" + Uri.EscapeDataString(_credentials.ClientSecret))));
            using var response = await _transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("OAuth token exchange failed with HTTP status " + (int)response.StatusCode + ".");
            var value = await ReadJsonAsync(response.Content, _options.MaximumTokenBytes, cancellationToken).ConfigureAwait(false);
            var token = RequiredString(value, "access_token");
            if (!string.Equals(RequiredString(value, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OAuth token endpoint returned an unsupported token type.");
            if (token.Any(character => character <= ' ' || character >= 127)) throw new InvalidDataException("OAuth access_token is not a valid HTTP bearer value.");
            DateTimeOffset? expires = null;
            if (value.TryGetProperty("expires_in", out var duration))
            {
                var now = DateTimeOffset.UtcNow;
                if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt64(out var seconds) || seconds < 0 || seconds > (DateTimeOffset.MaxValue - now).TotalSeconds)
                    throw new InvalidDataException("OAuth expires_in must be a nonnegative bounded integer.");
                expires = now.AddSeconds(seconds);
            }
            var granted = scope;
            if (value.TryGetProperty("scope", out var grantedScopes))
            {
                if (grantedScopes.ValueKind != JsonValueKind.String) throw new InvalidDataException("OAuth scope must be a string.");
                granted = grantedScopes.GetString();
            }
            var accepted = new AccessToken(token, expires, granted);
            cancellationToken.ThrowIfCancellationRequested();
            _discovery = discovery;
            _requestedScopes = scope;
            Volatile.Write(ref _token, accepted);
            return accepted;
        }
        finally { _acquisition.Release(); }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lifetime) { if (_disposed) return; _disposed = true; }
            Exception? failure = null;
            try { _shutdown.Cancel(); } catch (Exception error) { failure = error; }
            try { _transport.Dispose(); } catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
            try { _shutdown.Dispose(); } catch (Exception error) { if (failure is null) failure = error; else AttachCleanup(failure, error); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        base.Dispose(disposing);
    }

    private static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme && first.Port == second.Port && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase);
    private static void ValidateHttpUri(Uri uri, string name)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("OAuth endpoints must be absolute HTTP(S) URIs without user information or fragments.", name);
    }
    private static string RequiredString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString()))
            throw new InvalidDataException("OAuth response requires a nonempty string field: " + name + ".");
        return field.GetString()!;
    }
    private static void AttachCleanup(Exception primary, Exception secondary)
    {
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        errors.Add(secondary);
    }
    private static async Task<JsonElement> ReadJsonAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("OAuth response exceeded its byte limit.");
        var stream = await ProviderHttp.OpenBodyAsync(content, cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await ProviderHttp.ReadBytesAsync(stream, buffer, 0, (int)Math.Min(buffer.Length, maximumBytes - bytes.Length + 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (bytes.Length + read > maximumBytes) throw new InvalidDataException("OAuth response exceeded its byte limit.");
            bytes.Write(buffer, 0, read);
        }
        try { using var document = JsonDocument.Parse(bytes.ToArray()); return document.RootElement.Clone(); }
        catch (JsonException error) { throw new InvalidDataException("OAuth response contained malformed JSON.", error); }
    }
    private static IEnumerable<string> SplitScopes(string? value) => (value ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    private static string CombineScopes(params string?[] values) => string.Join(" ", values.SelectMany(SplitScopes).Distinct(StringComparer.Ordinal));
    private static bool IncludesScopes(string? granted, string? required)
    {
        var available = new HashSet<string>(SplitScopes(granted), StringComparer.Ordinal);
        return SplitScopes(required).All(available.Contains);
    }
    private sealed record AccessToken(string Value, DateTimeOffset? ExpiresAt, string? Scopes);
    private sealed record Discovery(Uri Resource, Uri Issuer, Uri TokenEndpoint, string? Scopes);
    private sealed record Challenge(Uri? Metadata, string? Scope, string? Error);
}
