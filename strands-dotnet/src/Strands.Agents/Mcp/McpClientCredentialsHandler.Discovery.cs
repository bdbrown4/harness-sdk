using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Strands.Agents.Mcp;

public sealed partial class McpClientCredentialsHandler
{
    private static Challenge ParseChallenge(HttpResponseMessage response)
    {
        var bearer = response.Headers.WwwAuthenticate.FirstOrDefault(value => string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
        if (bearer is null) return new(null, null, null);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(bearer.Parameter ?? string.Empty, "([A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*(?:\"((?:\\\\.|[^\"\\\\])*)\"|([^,\\s]+))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            var key = match.Groups[1].Value;
            if (fields.ContainsKey(key)) throw new InvalidDataException("OAuth challenge repeats a parameter.");
            fields[key] = match.Groups[2].Success ? Regex.Replace(match.Groups[2].Value, "\\\\(.)", "$1", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : match.Groups[3].Value;
        }
        Uri? metadata = null;
        if (fields.TryGetValue("resource_metadata", out var url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out metadata)) throw new InvalidDataException("OAuth resource_metadata must be an absolute URI.");
            ValidateHttpUri(metadata, "resource_metadata");
        }
        fields.TryGetValue("scope", out var scope);
        return new(metadata, string.IsNullOrEmpty(scope) ? null : scope, fields.TryGetValue("error", out var error) ? error : null);
    }

    private async Task<Discovery> DiscoverAsync(Challenge? challenge, CancellationToken cancellationToken)
    {
        var origin = new Uri(_resource.GetLeftPart(UriPartial.Authority) + "/");
        var resourcePath = _resource.AbsolutePath.Trim('/');
        var candidates = challenge?.Metadata is { } selected ? new[] { selected }
            : new[] { new Uri(origin, ".well-known/oauth-protected-resource" + (resourcePath.Length == 0 ? "" : "/" + resourcePath)), new Uri(origin, ".well-known/oauth-protected-resource") }.Distinct().ToArray();
        JsonElement? resourceMetadata = null;
        foreach (var candidate in candidates)
        {
            resourceMetadata = await GetMetadataAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (resourceMetadata.HasValue) break;
        }
        if (challenge?.Metadata is not null && !resourceMetadata.HasValue) throw new InvalidDataException("OAuth challenge referenced unavailable protected-resource metadata.");
        var resource = _resource;
        var issuer = origin;
        var scope = challenge?.Scope;
        if (resourceMetadata is { } metadata)
        {
            resource = ParseUri(metadata, "resource");
            if (!SameOrigin(resource, _resource) || (!string.IsNullOrEmpty(resource.Query) && resource.Query != _resource.Query) ||
                !(resource.AbsolutePath == _resource.AbsolutePath || _resource.AbsolutePath.StartsWith(resource.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("OAuth protected-resource metadata does not identify the configured resource.");
            if (metadata.TryGetProperty("authorization_servers", out var servers))
            {
                if (servers.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OAuth authorization_servers must be an array.");
                foreach (var server in servers.EnumerateArray())
                    if (server.ValueKind != JsonValueKind.String || !Uri.TryCreate(server.GetString(), UriKind.Absolute, out var value) || !IsIssuer(value))
                        throw new InvalidDataException("OAuth authorization_servers contains an invalid issuer.");
                if (servers.GetArrayLength() > 0) issuer = new Uri(servers[0].GetString()!, UriKind.Absolute);
            }
            var supportedScopes = ReadScopes(metadata);
            scope ??= supportedScopes;
        }
        var issuerOrigin = new Uri(issuer.GetLeftPart(UriPartial.Authority) + "/");
        var issuerPath = issuer.AbsolutePath.Trim('/');
        var suffix = issuerPath.Length == 0 ? string.Empty : "/" + issuerPath;
        var discoveryUris = new[]
        {
            new Uri(issuerOrigin, ".well-known/oauth-authorization-server" + suffix),
            new Uri(issuerOrigin, ".well-known/openid-configuration" + suffix),
            new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration")
        }.Distinct().ToArray();
        JsonElement? serverMetadata = null;
        foreach (var candidate in discoveryUris)
        {
            serverMetadata = await GetMetadataAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (serverMetadata.HasValue) break;
        }
        Uri tokenEndpoint;
        if (serverMetadata is { } authorization)
        {
            var declared = ParseUri(authorization, "issuer");
            if (!IsIssuer(declared) || NormalizeIssuer(declared) != NormalizeIssuer(issuer)) throw new InvalidDataException("OAuth authorization-server issuer does not match discovery.");
            tokenEndpoint = ParseUri(authorization, "token_endpoint");
            RequireSupported(authorization, "grant_types_supported", "client_credentials");
            RequireSupported(authorization, "token_endpoint_auth_methods_supported", "client_secret_basic");
        }
        else tokenEndpoint = new Uri(issuerOrigin, "token");
        ValidateTokenEndpoint(tokenEndpoint);
        scope ??= _credentials.Scopes.Count > 0 ? string.Join(" ", _credentials.Scopes) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new(resource, issuer, tokenEndpoint, scope);
    }

    private async Task<JsonElement?> GetMetadataAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("OAuth metadata request failed with HTTP status " + (int)response.StatusCode + ".");
        var metadata = await ReadJsonAsync(response.Content, _options.MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        if (metadata.ValueKind != JsonValueKind.Object) throw new InvalidDataException("OAuth metadata must be an object.");
        return metadata;
    }

    private static Uri ParseUri(JsonElement value, string name)
    {
        if (!Uri.TryCreate(RequiredString(value, name), UriKind.Absolute, out var uri)) throw new InvalidDataException("OAuth response URI is invalid: " + name + ".");
        ValidateHttpUri(uri, name);
        return uri;
    }
    private static bool IsIssuer(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.Query);
    internal static void ValidateTokenEndpoint(Uri uri)
    {
        ValidateHttpUri(uri, "token_endpoint");
        if (uri.Scheme != "https" && !uri.IsLoopback && !uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OAuth credentials require an HTTPS token endpoint outside loopback development endpoints.");
    }
    private static string NormalizeIssuer(Uri uri) => uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri.AbsoluteUri.Substring(0, uri.AbsoluteUri.Length - 1) : uri.AbsoluteUri;
    private static void RequireSupported(JsonElement metadata, string field, string required)
    {
        if (!metadata.TryGetProperty(field, out var values)) return;
        if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String) || !values.EnumerateArray().Any(value => value.GetString() == required))
            throw new InvalidDataException("OAuth metadata does not support the required " + field + ".");
    }
    private static string? ReadScopes(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("scopes_supported", out var scopes)) return null;
        if (scopes.ValueKind != JsonValueKind.Array || scopes.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) throw new InvalidDataException("OAuth scopes_supported must contain strings.");
        var scope = string.Join(" ", scopes.EnumerateArray().Select(value => value.GetString()));
        return scope.Length == 0 ? null : scope;
    }
}
