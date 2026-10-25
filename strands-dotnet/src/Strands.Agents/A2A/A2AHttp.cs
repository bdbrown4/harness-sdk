using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.A2A;

internal static class A2AHttp
{
    internal static void ValidateUri(Uri? uri, string name)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) throw new ArgumentException("An absolute HTTP or HTTPS URI is required.", name);
    }

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string mediaType, CancellationToken cancellationToken, bool allowJsonError = false)
    {
        var response = await SendResponseAsync(client, request, cancellationToken).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            if (!response.IsSuccessStatusCode) throw new A2AHttpException($"The A2A server returned HTTP {(int)response.StatusCode}.", response.StatusCode);
            var actualMediaType = response.Content?.Headers.ContentType?.MediaType;
            if (!string.Equals(actualMediaType, mediaType, StringComparison.OrdinalIgnoreCase) && !(allowJsonError && string.Equals(actualMediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
                throw new A2AProtocolException($"The A2A server must return {mediaType}.");
            return response;
        }
        catch (Exception error) { primary = error; throw; }
        finally { if (primary is not null) Dispose(response, primary); }
    }

    internal static async Task<HttpResponseMessage> SendResponseAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            if (error.CancellationToken == cancellationToken) throw;
            var canceled = new OperationCanceledException("The A2A HTTP request was canceled.", error, cancellationToken);
            if (error.Data["Strands.CleanupErrors"] is List<Exception> cleanup) canceled.Data["Strands.CleanupErrors"] = new List<Exception>(cleanup);
            throw canceled;
        }
        catch (HttpRequestException error) { throw new A2AHttpException("The A2A HTTP request failed.", innerException: error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { throw new A2AHttpException("The A2A HTTP request timed out.", innerException: error); }
    }

    internal static async Task<JsonElement> ReadJsonAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        var stream = await ProviderHttp.OpenBodyAsync(content, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = await ProviderHttp.ReadJsonAsync(stream, "A2A", maximum, cancellationToken).ConfigureAwait(false);
            A2AJson.ValidateUniqueProperties(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (ModelException error) { throw new A2AProtocolException("The A2A JSON response is malformed or exceeds MaxResponseBytes.", error); }
    }

    internal static void Dispose(IDisposable value, Exception? primary)
    {
        try { value.Dispose(); }
        catch (Exception cleanup) { if (primary is null) throw; AddCleanup(primary, cleanup); }
    }
    internal static void AddCleanup(Exception primary, Exception cleanup)
    {
        const string key = "Strands.CleanupErrors";
        if (primary.Data[key] is not List<Exception> errors) primary.Data[key] = errors = [];
        errors.Add(cleanup);
    }
}

internal static class A2AJson
{
    internal static void RequireObject(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Object) throw new A2AProtocolException("An A2A object is required."); }
    internal static JsonElement RequiredObject(JsonElement value, string name)
    { RequireObject(value); if (!value.TryGetProperty(name, out var nested) || nested.ValueKind != JsonValueKind.Object) throw new A2AProtocolException($"A2A field {name} must be an object."); return nested; }
    internal static string? OptionalString(JsonElement value, string name)
    {
        RequireObject(value);
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new A2AProtocolException($"A2A field {name} must be a string.");
        return property.GetString();
    }
    internal static string RequiredString(JsonElement value, string name)
    { var text = OptionalString(value, name); return !string.IsNullOrWhiteSpace(text) ? text! : throw new A2AProtocolException($"A2A field {name} must be nonblank."); }
    internal static bool OptionalBoolean(JsonElement value, string name)
    {
        RequireObject(value);
        if (!value.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new A2AProtocolException($"A2A field {name} must be boolean.");
        return property.ValueKind == JsonValueKind.True;
    }
    internal static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new A2AProtocolException($"Duplicate A2A JSON field: {property.Name}.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) ValidateUniqueProperties(child);
    }
}
