using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ATaskTests
{
    [Fact]
    public async Task RetrievalCancellationAndResubscriptionUseTheOriginalTaskCorrelation()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(new("https://agent.test/")));

        Assert.Equal("working", (await client.GetTaskAsync("task", 3)).GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("canceled", (await client.CancelTaskAsync("task")).GetProperty("status").GetProperty("state").GetString());
        var updates = new List<JsonElement>();
        await foreach (var update in client.ResubscribeAsync("task")) updates.Add(update);

        Assert.Equal(new[] { "tasks/get", "tasks/cancel", "tasks/resubscribe" }, handler.Methods);
        Assert.Single(updates);
        Assert.Equal("task", updates[0].GetProperty("id").GetString());
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<string> Methods { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(A2AClientTests.Card, Encoding.UTF8, "application/json") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            var method = root.GetProperty("method").GetString()!;
            Methods.Add(method);
            Assert.Equal("task", root.GetProperty("params").GetProperty("id").GetString());
            if (method == "tasks/get") Assert.Equal(3, root.GetProperty("params").GetProperty("historyLength").GetInt32());
            var id = root.GetProperty("id").GetString()!;
            var task = new { kind = "task", id = "task", contextId = "context", status = new { state = method == "tasks/get" ? "working" : "canceled" } };
            return new(HttpStatusCode.OK) { Content = new StringContent(method == "tasks/resubscribe" ? A2AAgentTests.Frame(id, task) : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = task }), Encoding.UTF8, method == "tasks/resubscribe" ? "text/event-stream" : "application/json") };
        }
    }
}
