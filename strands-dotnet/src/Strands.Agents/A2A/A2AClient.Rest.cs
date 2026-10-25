using System.Globalization;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.A2A;

public sealed partial class A2AClient
{
    private async Task<JsonElement> CallRestAsync(ClientEndpoint endpoint, string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        using var request = CreateRestRequest(endpoint.Url, method, parameters, streaming: false);
        var deleting = method == "tasks/pushNotificationConfig/delete";
        var response = await SendRestAsync(request, "application/json", cancellationToken, allowEmpty: deleting).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            if (deleting && (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)) return JsonSerializer.SerializeToElement(new { });
            var body = await A2AHttp.ReadJsonAsync(response.Content, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            return method switch
            {
                "message/send" => A2ARestJson.ReadMessageResult(body),
                "tasks/get" or "tasks/cancel" => A2ARestJson.ReadTask(body),
                "agent/getAuthenticatedExtendedCard" => A2ARestJson.ReadCard(body),
                "tasks/pushNotificationConfig/set" or "tasks/pushNotificationConfig/get" => A2ARestJson.ReadPushConfiguration(body),
                "tasks/pushNotificationConfig/list" => A2ARestJson.ReadPushConfigurations(body),
                _ => body
            };
        }
        catch (Exception error) { primary = error; throw; }
        finally { A2AHttp.Dispose(response, primary); }
    }

    private static HttpRequestMessage CreateRestRequest(Uri endpoint, string method, JsonElement? parameters, bool streaming)
    {
        var verb = HttpMethod.Post;
        string path;
        object? body = null;
        string? query = null;
        switch (method)
        {
            case "message/send" when !streaming:
            case "message/stream" when streaming:
                path = method == "message/send" ? "/v1/message:send" : "/v1/message:stream";
                body = A2ARestJson.WriteMessageRequest(RequireParameters(parameters));
                break;
            case "tasks/get" when !streaming:
                verb = HttpMethod.Get;
                var get = RequireParameters(parameters);
                path = "/v1/tasks/" + Uri.EscapeDataString(A2AJson.RequiredString(get, "id"));
                if (get.TryGetProperty("historyLength", out var length))
                {
                    if (length.ValueKind != JsonValueKind.Number || !length.TryGetInt32(out var count) || count < 0) throw new A2AProtocolException("historyLength must be a nonnegative integer.");
                    query = "historyLength=" + count.ToString(CultureInfo.InvariantCulture);
                }
                break;
            case "tasks/cancel" when !streaming:
                var id = A2AJson.RequiredString(RequireParameters(parameters), "id");
                path = "/v1/tasks/" + Uri.EscapeDataString(id) + ":cancel";
                body = new { name = "tasks/" + id };
                break;
            case "tasks/resubscribe" when streaming:
                verb = HttpMethod.Get;
                path = "/v1/tasks/" + Uri.EscapeDataString(A2AJson.RequiredString(RequireParameters(parameters), "id")) + ":subscribe";
                break;
            case "agent/getAuthenticatedExtendedCard" when !streaming:
                verb = HttpMethod.Get;
                path = "/v1/card";
                break;
            case "tasks/pushNotificationConfig/set" when !streaming:
                var set = RequireParameters(parameters);
                var taskId = A2AJson.RequiredString(set, "taskId");
                path = PushConfigurationPath(taskId);
                body = A2ARestJson.WritePushConfiguration(taskId, A2AJson.RequiredObject(set, "pushNotificationConfig"));
                break;
            case "tasks/pushNotificationConfig/get" when !streaming:
            case "tasks/pushNotificationConfig/delete" when !streaming:
                var config = RequireParameters(parameters);
                verb = method.EndsWith("/delete", StringComparison.Ordinal) ? HttpMethod.Delete : HttpMethod.Get;
                path = PushConfigurationPath(A2AJson.RequiredString(config, "id")) + "/" + Uri.EscapeDataString(A2AJson.RequiredString(config, "pushNotificationConfigId"));
                break;
            case "tasks/pushNotificationConfig/list" when !streaming:
                verb = HttpMethod.Get;
                path = PushConfigurationPath(A2AJson.RequiredString(RequireParameters(parameters), "id"));
                break;
            default:
                throw new NotSupportedException("The A2A HTTP+JSON binding does not support method '" + method + "' in this request mode.");
        }
        var suffix = endpoint.Query.TrimStart('?');
        if (query is not null) suffix = suffix.Length == 0 ? query : suffix + "&" + query;
        var uri = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + path + (suffix.Length == 0 ? "" : "?" + suffix);
        var request = new HttpRequestMessage(verb, uri);
        if (body is not null) request.Content = ProviderHttp.JsonContent(body);
        request.Headers.Accept.Add(new(streaming ? "text/event-stream" : "application/json"));
        return request;
    }

    private static string PushConfigurationPath(string taskId) => "/v1/tasks/" + Uri.EscapeDataString(taskId) + "/pushNotificationConfigs";

    private static JsonElement RequireParameters(JsonElement? parameters)
    {
        if (parameters is not { } value) throw new ArgumentException("The A2A operation requires parameters.", nameof(parameters));
        A2AJson.RequireObject(value);
        return value;
    }

    private async Task<HttpResponseMessage> SendRestAsync(HttpRequestMessage request, string mediaType, CancellationToken cancellationToken, bool allowJsonError = false, bool allowEmpty = false)
    {
        var response = await A2AHttp.SendResponseAsync(_client, request, cancellationToken).ConfigureAwait(false);
        Exception? primary = null;
        try
        {
            var actual = response.Content?.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode)
            {
                if (string.Equals(actual, "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var body = await A2AHttp.ReadJsonAsync(response.Content!, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                        throw A2ARestJson.ReadError(body);
                    }
                    catch (A2AProtocolException error)
                    { throw new A2AHttpException($"The A2A server returned HTTP {(int)response.StatusCode} with an invalid error body.", response.StatusCode, error); }
                }
                throw new A2AHttpException($"The A2A server returned HTTP {(int)response.StatusCode}.", response.StatusCode);
            }
            if (allowEmpty && (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content?.Headers.ContentLength == 0)) return response;
            if (!string.Equals(actual, mediaType, StringComparison.OrdinalIgnoreCase) && !(allowJsonError && string.Equals(actual, "application/json", StringComparison.OrdinalIgnoreCase)))
                throw new A2AProtocolException($"The A2A server must return {mediaType}.");
            return response;
        }
        catch (Exception error) { primary = error; throw; }
        finally { if (primary is not null) A2AHttp.Dispose(response, primary); }
    }
}
