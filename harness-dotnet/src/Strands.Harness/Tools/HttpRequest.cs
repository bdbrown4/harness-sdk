using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tools;

/// <summary>Creates an explicitly selected raw HTTP tool using host networking.</summary>
public static partial class HttpRequest
{
    /// <summary>Creates an HTTP tool. Supplied client and transport resources remain borrowed.</summary>
    public static ITool Create(HttpRequestConfig? config = null)
    {
        var snapshot = (config ?? new()) with { };
        if (!Enum.IsDefined(typeof(HttpRequestBehavior), snapshot.Behavior)) throw new ArgumentOutOfRangeException(nameof(config), "Unknown HTTP request behavior.");
        if (snapshot.MaximumRequestBodyBytes <= 0 || snapshot.MaximumResponseBodyBytes <= 0 || snapshot.MaximumResponseBodyBytes == int.MaxValue || snapshot.MaximumHeaderBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "HTTP byte bounds must be positive; response size must leave room for an overflow byte.");
        var optionalString = snapshot.Behavior == HttpRequestBehavior.Python ? new[] { "string", "null" } : new[] { "string" };
        var optionalObject = snapshot.Behavior == HttpRequestBehavior.Python ? new[] { "object", "null" } : new[] { "object" };
        var optionalNumber = snapshot.Behavior == HttpRequestBehavior.Python ? new[] { "number", "null" } : new[] { "number" };
        var spec = new ToolSpec(snapshot.Name, snapshot.Description, JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                method = new { type = "string", @enum = new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" } },
                url = new { type = "string", description = "An absolute http(s) URL without user information." },
                headers = new { type = optionalObject, additionalProperties = new { type = "string" } },
                body = new { type = optionalString },
                timeout = new { type = optionalNumber, exclusiveMinimum = 0, description = "Complete native request deadline in seconds; not HTTPX phase timers." }
            },
            required = new[] { "method", "url" }
        }));
        return new FunctionTool(spec, (input, context, cancellationToken) => SendAsync(Parse(input, snapshot), snapshot, context.CancelSignal, cancellationToken));
    }

    private sealed record ParsedRequest(string Method, Uri Uri, byte[]? Body, IReadOnlyList<KeyValuePair<string, string>> Headers, TimeSpan? Timeout);

    private static ParsedRequest Parse(JsonElement input, HttpRequestConfig config)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("HTTP input must be an object.", nameof(input));
        var method = RequiredString(input, "method");
        if (method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS")) throw new ArgumentException("Unsupported HTTP method.", nameof(input));
        var url = RequiredString(input, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("url must be an absolute http(s) URI without user information.", nameof(input));
        byte[]? body = null;
        if (input.TryGetProperty("body", out var bodyValue) && !(config.Behavior == HttpRequestBehavior.Python && bodyValue.ValueKind == JsonValueKind.Null))
        {
            if (bodyValue.ValueKind != JsonValueKind.String) throw new ArgumentException("body must be a string.", nameof(input));
            if (config.Behavior == HttpRequestBehavior.TypeScript && method is "GET" or "HEAD") throw new ArgumentException("GET and HEAD cannot have a body under TypeScript behavior.", nameof(input));
            var text = bodyValue.GetString()!;
            if (Encoding.UTF8.GetByteCount(text) > config.MaximumRequestBodyBytes) throw new ArgumentException("HTTP request body exceeds its byte limit.", nameof(input));
            body = Encoding.UTF8.GetBytes(text);
        }
        var headers = new List<KeyValuePair<string, string>>();
        long headerBytes = 0;
        if (input.TryGetProperty("headers", out var headerValue) && !(config.Behavior == HttpRequestBehavior.Python && headerValue.ValueKind == JsonValueKind.Null))
        {
            if (headerValue.ValueKind != JsonValueKind.Object) throw new ArgumentException("headers must be an object of strings.", nameof(input));
            foreach (var header in headerValue.EnumerateObject())
            {
                if (header.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("Header values must be strings.", nameof(input));
                var value = header.Value.GetString()!;
                if (header.Name.Length == 0 || header.Name.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') && "!#$%&'*+-.^_`|~".IndexOf(character) < 0) || value.Any(character => character != '\t' && (character < 32 || character > 126)))
                    throw new ArgumentException("HTTP headers must have token names and portable ASCII values without line breaks.", nameof(input));
                if (header.Name.ToLowerInvariant() is "content-length" or "transfer-encoding" or "connection" or "host" or "upgrade" or "trailer" or "te" or "expect" or "proxy-connection")
                    throw new ArgumentException("Transport-controlled header is unavailable: " + header.Name, nameof(input));
                headerBytes += header.Name.Length + (long)value.Length + 4;
                if (headerBytes > config.MaximumHeaderBytes) throw new ArgumentException("HTTP request headers exceed their byte limit.", nameof(input));
                headers.Add(new(header.Name, value));
            }
        }
        double? seconds = null;
        if (input.TryGetProperty("timeout", out var timeoutValue) && !(config.Behavior == HttpRequestBehavior.Python && timeoutValue.ValueKind == JsonValueKind.Null))
        {
            if (timeoutValue.ValueKind != JsonValueKind.Number || !timeoutValue.TryGetDouble(out var timeout) || double.IsNaN(timeout) || double.IsInfinity(timeout) || timeout <= 0 || timeout * 1000 > int.MaxValue - 1)
                throw new ArgumentException("timeout must be positive, finite, and fit the native timer range.", nameof(input));
            seconds = timeout;
        }
        if (config.Behavior == HttpRequestBehavior.TypeScript) seconds ??= 30;
        else if (config.HttpClient is null) seconds ??= 5;
        else if (config.HttpClient.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            seconds = seconds is { } requested ? Math.Min(requested, config.HttpClient.Timeout.TotalSeconds) : config.HttpClient.Timeout.TotalSeconds;
        var budget = seconds is { } finite ? TimeSpan.FromMilliseconds(Math.Ceiling(finite * 1000)) : (TimeSpan?)null;
        return new(method, uri, body, headers.AsReadOnly(), budget);
    }

    private static string RequiredString(JsonElement input, string name) => input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new ArgumentException(name + " must be a string.", nameof(input));
}
