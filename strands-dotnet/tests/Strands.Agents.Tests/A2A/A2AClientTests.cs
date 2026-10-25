using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AClientTests
{
    [Fact]
    public async Task DiscoveryUsesTheWellKnownCardCachesSuccessAndBorrowsTheTransport()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Card, Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(new("https://agent.test/nested/")));

        var first = await client.GetAgentCardAsync();
        var second = await client.GetAgentCardAsync();

        Assert.Equal("Remote researcher", first.GetProperty("name").GetString());
        Assert.Equal(first.GetRawText(), second.GetRawText());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://agent.test/.well-known/agent-card.json"), request.Uri);
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task JsonRpcUsesTheAdvertisedEndpointAndPreservesWirePayloadAndCorrelation()
    {
        JsonElement sent = default;
        Uri? endpoint = null;
        var handler = new AsyncHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return JsonResponse(Card);
            endpoint = request.RequestUri;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent = body.RootElement.Clone();
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            return JsonResponse(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = sent.GetProperty("id").GetString(), result = new { kind = "message", messageId = "reply", role = "agent", parts = new[] { new { kind = "text", text = "complete" } } } }));
        });
        using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(new("https://agent.test/")));
        var parameters = JsonSerializer.SerializeToElement(new { message = new { kind = "message", messageId = "caller-message", role = "user", contextId = "opaque-context", parts = new[] { new { kind = "text", text = "research" } } } });

        var result = await client.CallAsync("message/send", parameters);

        Assert.Equal(new Uri("https://agent.test/rpc"), endpoint);
        Assert.Equal("2.0", sent.GetProperty("jsonrpc").GetString());
        Assert.Equal("message/send", sent.GetProperty("method").GetString());
        Assert.Equal(parameters.GetRawText(), sent.GetProperty("params").GetRawText());
        Assert.Equal("complete", result.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task StreamingReadsCorrelatedMultilineEventsAndFinishesAtTheFinalStatus()
    {
        var handler = new AsyncHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return JsonResponse(Card);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetString();
            Assert.Equal("message/stream", body.RootElement.GetProperty("method").GetString());
            Assert.Equal("text/event-stream", request.Headers.Accept.Single().MediaType);
            string Frame(object result) => "data: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }).Replace(",\"result\":", ",\ndata: \"result\":") + "\r\n\r\n";
            var events = "\uFEFF: heartbeat\r\n\r\n" +
                Frame(new { kind = "task", id = "task", contextId = "context", status = new { state = "working" } }) +
                Frame(new { kind = "artifact-update", taskId = "task", contextId = "context", artifact = new { artifactId = "output", parts = new[] { new { kind = "text", text = "Résumé 🐈" } } }, append = false, lastChunk = true }) +
                Frame(new { kind = "status-update", taskId = "task", contextId = "context", status = new { state = "completed" }, final = true });
            return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        });
        using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(new("https://agent.test/")));
        var results = new List<JsonElement>();

        await foreach (var result in client.StreamCallAsync("message/stream", JsonSerializer.SerializeToElement(new { message = new { } }))) results.Add(result);

        Assert.Equal(3, results.Count);
        Assert.Equal("Résumé 🐈", results[1].GetProperty("artifact").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.True(results[2].GetProperty("final").GetBoolean());
    }

    [Fact]
    public async Task AuthenticatedExtendedCardsReplaceCachedMetadataAndAdvertisedEndpoints()
    {
        var extended = Card.Replace("Remote researcher", "Private researcher").Replace("https://agent.test/rpc", "https://agent.test/private-rpc");
        var handler = new AsyncHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return JsonResponse(Card);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            var id = root.GetProperty("id").GetString();
            if (root.GetProperty("method").GetString() == "agent/getAuthenticatedExtendedCard")
                return JsonResponse("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + extended + "}");
            Assert.Equal(new Uri("https://agent.test/private-rpc"), request.RequestUri);
            return JsonResponse(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { kind = "task", id = "task", contextId = "context", status = new { state = "completed" } } }));
        });
        using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(new("https://agent.test/")));

        var card = await client.GetAuthenticatedExtendedCardAsync();

        Assert.Equal("Private researcher", card.GetProperty("name").GetString());
        Assert.Equal(card.GetRawText(), (await client.GetAgentCardAsync()).GetRawText());
        await client.GetTaskAsync("task");
    }

    internal const string Card = """{"name":"Remote researcher","description":"Research","version":"1.0.0","protocolVersion":"0.3.0","url":"https://agent.test/rpc","capabilities":{"streaming":true},"defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"],"skills":[]}""";
    private static HttpResponseMessage JsonResponse(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = [];
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));
            return Task.FromResult(respond(request));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
