using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.A2A;

public sealed partial class A2AClient
{
    /// <summary>Invokes one v0.3 method through the selected JSONRPC or HTTP+JSON binding and returns its detached native protocol result. Parameters use the common A2A wire shape.</summary>
    public async Task<JsonElement> CallAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(method, nameof(method));
        if (parameters is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("A2A method parameters must be an object.", nameof(parameters));
        var copy = parameters?.Clone();
        await GetAgentCardAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = _endpoint!;
        if (endpoint.Rest) return await CallRestAsync(endpoint, method, copy, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        using var request = CreateRpcRequest(endpoint.Url, method, copy, id, "application/json");
        var response = await A2AHttp.SendAsync(_client, request, "application/json", cancellationToken).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            var body = await A2AHttp.ReadJsonAsync(response.Content, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            return ReadResult(body, id);
        }
        catch (Exception error) { primary = error; throw; }
        finally { A2AHttp.Dispose(response, primary); }
    }

    private static HttpRequestMessage CreateRpcRequest(Uri endpoint, string method, JsonElement? parameters, string id, string mediaType)
    {
        var body = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is { } value) body["params"] = value;
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = ProviderHttp.JsonContent(body) };
        request.Headers.Accept.Add(new(mediaType));
        return request;
    }

    private static JsonElement ReadResult(JsonElement response, string id)
    {
        if (A2AJson.RequiredString(response, "jsonrpc") != "2.0") throw new A2AProtocolException("The A2A response version does not match.");
        var matches = response.TryGetProperty("id", out var requestId) && requestId.ValueKind == JsonValueKind.String && requestId.GetString() == id;
        var hasResult = response.TryGetProperty("result", out var result);
        var hasError = response.TryGetProperty("error", out var error);
        if (hasResult == hasError) throw new A2AProtocolException("The A2A response must contain exactly one of result and error.");
        if (hasResult)
        {
            if (!matches) throw new A2AProtocolException("The A2A response request identifier does not match.");
            return result.Clone();
        }
        A2AJson.RequireObject(error);
        if (!error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var number)) throw new A2AProtocolException("A JSON-RPC error requires an integer code.");
        if (!matches && !(requestId.ValueKind == JsonValueKind.Null && number is -32700 or -32600)) throw new A2AProtocolException("The A2A error request identifier does not match.");
        var message = A2AJson.RequiredString(error, "message");
        throw new A2ARpcException(number, message, error.TryGetProperty("data", out var data) ? data : null);
    }
}
