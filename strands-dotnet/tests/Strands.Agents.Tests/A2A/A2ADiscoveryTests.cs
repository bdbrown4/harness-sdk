using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ADiscoveryTests
{
    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.3.0")]
    public async Task LegacyAndPinnedCardsRetainTheirUnrecognizedExtensions(string protocol)
    {
        var card = Card(new() { ["protocolVersion"] = protocol, ["x-remote-extension"] = new { custom = true } });
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(card))));
        var result = await new A2AClient(http, new(new("https://agent.test/"))).GetAgentCardAsync();
        Assert.True(result.GetProperty("x-remote-extension").GetProperty("custom").GetBoolean());
    }
    [Fact]
    public async Task JsonRpcAdditionalInterfaceAndExplicitEndpointOverrideAreSelectedDeliberately()
    {
        var advertised = Card(new() { ["preferredTransport"] = "GRPC", ["additionalInterfaces"] = new[] { new { transport = "JSONRPC", url = "https://agent.test/alternative" } } });
        Uri? actual = null;
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Json(advertised);
            actual = request.RequestUri;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return Json(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = document.RootElement.GetProperty("id").GetString(), result = new { } }));
        }));
        await new A2AClient(http, new(new("https://agent.test/"))).CallAsync("extension/method");
        Assert.Equal(new Uri("https://agent.test/alternative"), actual);
        await new A2AClient(http, new(new("https://agent.test/")) { Endpoint = new("https://agent.test/explicit") }).CallAsync("extension/method");
        Assert.Equal(new Uri("https://agent.test/explicit"), actual);
    }
    [Fact]
    public async Task ConcurrentDiscoverySharesOneRequestAndPrecancelledCachedLookupsStillCancel()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new Handler(async (_, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(true); await release.Task; return Json(A2AClientTests.Card); }));
        var client = new A2AClient(http, new(new("https://agent.test/")));
        var first = client.GetAgentCardAsync();
        await entered.Task;
        var second = client.GetAgentCardAsync();
        release.SetResult(true);
        await Task.WhenAll(first, second);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAgentCardAsync(cancellation.Token));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task InvalidDiscoveryDoesNotPoisonTheSuccessfulCache()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(Interlocked.Increment(ref calls) == 1 ? "{}" : A2AClientTests.Card))));
        var client = new A2AClient(http, new(new("https://agent.test/")));
        await Assert.ThrowsAsync<A2AProtocolException>(() => client.GetAgentCardAsync());
        Assert.Equal("Remote researcher", (await client.GetAgentCardAsync()).GetProperty("name").GetString());
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task CompleteResponseBoundsAndHttpFailuresAreTyped()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(A2AClientTests.Card))));
        await Assert.ThrowsAsync<A2AProtocolException>(() => new A2AClient(http, new(new("https://agent.test/")) { MaxResponseBytes = 16 }).GetAgentCardAsync());
        using var failing = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") })));
        var error = await Assert.ThrowsAsync<A2AHttpException>(() => new A2AClient(failing, new(new("https://agent.test/"))).GetAgentCardAsync());
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }
    [Fact]
    public async Task ExplicitNonStreamingModeUsesMessageSendAndRetainsProtocolRequestConfiguration()
    {
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Json(Card(new() { ["capabilities"] = new { streaming = false } }));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            Assert.Equal("message/send", body.GetProperty("method").GetString());
            Assert.Equal(2, body.GetProperty("params").GetProperty("configuration").GetProperty("historyLength").GetInt32());
            return Json(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = body.GetProperty("id").GetString(), result = new { kind = "message", messageId = "answer", role = "agent", parts = new[] { new { kind = "text", text = "complete" } } } }));
        }));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));
        Assert.Equal("complete", (await agent.InvokeAsync("Question", new() { Configuration = JsonSerializer.SerializeToElement(new { historyLength = 2 }) })).ToString());
    }
    private static string Card(Dictionary<string, object> changes)
    {
        var card = JsonSerializer.Deserialize<Dictionary<string, object>>(A2AClientTests.Card)!;
        foreach (var change in changes) card[change.Key] = change.Value;
        return JsonSerializer.Serialize(card);
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken); }
}
