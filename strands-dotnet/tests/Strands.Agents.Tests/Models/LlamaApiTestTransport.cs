using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Tests.Models;

internal sealed class LlamaApiTestTransport(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    internal List<JsonElement> Bodies { get; } = [];
    internal List<string?> Authorizations { get; } = [];
    internal Uri? Endpoint;
    internal Func<HttpResponseMessage>? ResponseFactory;
    internal Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponse { get; set; }
    internal int Disposals;
    internal static string Frame(string json, string? type = null) => (type is null ? "" : "event: " + type + "\n") + "data: " + json + "\n\n";
    internal static string TextStream(string text = "answer", string reason = "stop") =>
        Frame("""{"event":{"event_type":"start","delta":{"type":"text","text":""}}}""") +
        Frame(JsonSerializer.Serialize(new { @event = new { event_type = "progress", delta = new { type = "text", text } } })) +
        Frame(JsonSerializer.Serialize(new { @event = new { event_type = "complete", delta = new { type = "text", text = "" }, stop_reason = reason } }));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Endpoint = request.RequestUri; Authorizations.Add(request.Headers.Authorization?.ToString());
        Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync().ConfigureAwait(false)));
        if (BeforeResponse is { } before) await before(request, cancellationToken).ConfigureAwait(false);
        if (ResponseFactory is { } factory) return factory();
        return new(status) { Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "text/event-stream" : "application/json"), RequestMessage = request };
    }
    protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
}
