using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.A2A;

public sealed partial class A2AClient
{
    /// <summary>Streams detached v0.3 results through the selected JSONRPC or HTTP+JSON binding. The caller owns and must dispose the returned iterator.</summary>
    public IAsyncEnumerable<JsonElement> StreamCallAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(method, nameof(method));
        if (parameters is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("A2A method parameters must be an object.", nameof(parameters));
        return StreamRpcAsync(method, parameters?.Clone(), cancellationToken);
    }

    private async IAsyncEnumerable<JsonElement> StreamRpcAsync(string method, JsonElement? parameters, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var card = await GetAgentCardAsync(cancellationToken).ConfigureAwait(false);
        if (!A2AJson.OptionalBoolean(A2AJson.RequiredObject(card, "capabilities"), "streaming")) throw new NotSupportedException("The remote agent does not advertise streaming.");
        var endpoint = _endpoint!;
        var id = Guid.NewGuid().ToString("N");
        using var request = endpoint.Rest ? CreateRestRequest(endpoint.Url, method, parameters, streaming: true) : CreateRpcRequest(endpoint.Url, method, parameters, id, "text/event-stream");
        var response = endpoint.Rest
            ? await SendRestAsync(request, "text/event-stream", cancellationToken, allowJsonError: true).ConfigureAwait(false)
            : await A2AHttp.SendAsync(_client, request, "text/event-stream", cancellationToken, allowJsonError: true).ConfigureAwait(false);
        IAsyncEnumerator<SseFrame>? frames = null;
        Exception? primary = null;
        var state = new A2AStreamState();
        try
        {
            try
            {
                if (string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    var body = await A2AHttp.ReadJsonAsync(response.Content, _options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                    if (endpoint.Rest) throw A2ARestJson.ReadError(body);
                    _ = ReadResult(body, id);
                    throw new A2AProtocolException("A successful streaming response requires text/event-stream.");
                }
                var stream = await ProviderHttp.OpenBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                frames = ServerSentEvents.ReadAsync(stream, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                JsonElement result;
                bool closed;
                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false)) throw new A2AProtocolException("The A2A stream ended without a final task status or message.");
                    using var document = JsonDocument.Parse(frames.Current.Data);
                    A2AJson.ValidateUniqueProperties(document.RootElement);
                    if (endpoint.Rest && frames.Current.Event == "error") throw A2ARestJson.ReadError(document.RootElement);
                    result = endpoint.Rest ? A2ARestJson.ReadStreamResult(document.RootElement) : ReadResult(document.RootElement, id);
                    closed = state.Observe(result);
                }
                catch (Exception error)
                {
                    primary = error is ModelException or JsonException ? new A2AProtocolException("The A2A server-sent event is malformed, truncated or exceeds its 4 MiB limit.", error) : error;
                    ExceptionDispatchInfo.Capture(primary).Throw();
                    throw;
                }
                yield return result;
                if (closed) yield break;
            }
        }
        finally
        {
            Exception? cleanup = null;
            try { if (frames is not null) await frames.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { cleanup = error; }
            try { response.Dispose(); }
            catch (Exception error) { if (cleanup is null) cleanup = error; else A2AHttp.AddCleanup(cleanup, error); }
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                else A2AHttp.AddCleanup(primary, cleanup);
            }
        }
    }
}

internal sealed class A2AStreamState
{
    private string? _taskId;
    private string? _contextId;

    internal bool Observe(JsonElement value)
    {
        var kind = A2AJson.RequiredString(value, "kind");
        if (kind == "message")
        {
            if (_taskId is not null) throw new A2AProtocolException("An A2A Message-only response cannot follow task updates.");
            _ = A2AJson.RequiredString(value, "messageId");
            if (A2AJson.RequiredString(value, "role") != "agent") throw new A2AProtocolException("An A2A response message must have role agent.");
            RequireParts(value);
            return true;
        }
        if (kind is not ("task" or "status-update" or "artifact-update")) throw new A2AProtocolException("Unsupported A2A stream result kind: " + kind + ".");
        var taskId = A2AJson.RequiredString(value, kind == "task" ? "id" : "taskId");
        var contextId = A2AJson.RequiredString(value, "contextId");
        if (_taskId is not null && (_taskId != taskId || _contextId != contextId)) throw new A2AProtocolException("A2A stream task and context identifiers must remain correlated.");
        _taskId = taskId; _contextId = contextId;
        if (kind == "artifact-update")
        {
            var artifact = A2AJson.RequiredObject(value, "artifact");
            _ = A2AJson.RequiredString(artifact, "artifactId");
            RequireParts(artifact);
            _ = A2AJson.OptionalBoolean(value, "append");
            _ = A2AJson.OptionalBoolean(value, "lastChunk");
            return false;
        }
        var state = A2AJson.RequiredString(A2AJson.RequiredObject(value, "status"), "state");
        if (state is not ("submitted" or "working" or "input-required" or "auth-required" or "completed" or "failed" or "canceled" or "rejected" or "unknown"))
            throw new A2AProtocolException("Unsupported A2A task state: " + state + ".");
        var terminal = state is "completed" or "failed" or "canceled" or "rejected";
        if (kind == "task") return terminal || state is "input-required" or "auth-required";
        if (!value.TryGetProperty("final", out var final) || final.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new A2AProtocolException("An A2A status update requires a final boolean.");
        if (final.ValueKind == JsonValueKind.True && !terminal && state is not ("input-required" or "auth-required"))
            throw new A2AProtocolException("A final A2A status cannot remain submitted, working or unknown.");
        return final.ValueKind == JsonValueKind.True;
    }

    private static void RequireParts(JsonElement value)
    {
        if (!value.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            throw new A2AProtocolException("A2A parts must be an array.");
    }
}
