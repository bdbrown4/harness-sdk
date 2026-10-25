using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Xunit.Abstractions;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpInclusiveMinimumTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedDefaultMetadataReachesRawAndAdaptedCalls(bool adapted)
    {
        using var handler = new WireHandler();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        using var source = JsonDocument.Parse("""{"tenant":{"name":"original"},"defaultOnly":true}""");
        var connecting = ConnectAsync(http, server.Endpoint, new() { DefaultToolCallMetadata = source.RootElement });
        source.Dispose();
        await using var client = await connecting;
        var input = JsonSerializer.SerializeToElement(new { });
        if (adapted)
        {
            var tool = Assert.Single(await client.ListToolsAsync());
            await using var agent = NewAgent();
            var result = await tool.InvokeResultAsync(input, new ToolContext(agent, new("bound-call", tool.Spec.Name, input), new Dictionary<string, object?>()));
            Assert.Equal("bound-call", result.ToolUseId);
            Assert.Equal(ToolResultStatus.Success, result.Status);
        }
        else
        {
            var result = await client.CallToolAsync("lookup", input);
            Assert.Equal("ok", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text);
        }
        var meta = CallMetadata(handler);
        Assert.Equal("original", meta.GetProperty("tenant").GetProperty("name").GetString());
        Assert.True(meta.GetProperty("defaultOnly").GetBoolean());
    }

    [Fact]
    public async Task OptionsWithNullMetaStillInheritClientDefault()
    {
        using var handler = new WireHandler();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, new()
        {
            DefaultToolCallMetadata = JsonSerializer.SerializeToElement(new { tenant = "default" })
        });
        var options = new RequestOptions { Meta = null };
        await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), options: options);
        Assert.Equal("default", CallMetadata(handler).GetProperty("tenant").GetString());
        Assert.Null(options.Meta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitEmptyOrNonemptyMetadataReplacesClientDefault(bool nonempty)
    {
        using var handler = new WireHandler();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, new()
        {
            DefaultToolCallMetadata = JsonSerializer.SerializeToElement(new { tenant = "default", defaultOnly = true })
        });
        var supplied = nonempty ? new JsonObject { ["tenant"] = "explicit" } : new JsonObject();
        var before = supplied.ToJsonString();
        await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), options: new RequestOptions { Meta = supplied });
        var meta = CallMetadata(handler);
        Assert.False(meta.TryGetProperty("defaultOnly", out _));
        if (nonempty) Assert.Equal("explicit", meta.GetProperty("tenant").GetString());
        else Assert.False(meta.TryGetProperty("tenant", out _));
        Assert.Equal(before, supplied.ToJsonString());
    }

    [Fact]
    public async Task OfficialUrlElicitationExceptionShapeIsRecordedFromLoopbackWire()
    {
        using var handler = new WireHandler { UrlError = true };
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, new());
        var error = await Assert.ThrowsAnyAsync<McpProtocolException>(() => client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask());
        var data = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in error.Data) data[entry.Key.ToString()!] = entry.Value?.ToString();
        output.WriteLine("MCP_URL_SHAPE " + JsonSerializer.Serialize(new
        {
            Type = error.GetType().FullName,
            Code = (int)error.ErrorCode,
            error.Message,
            Typed = error is UrlElicitationRequiredException,
            Elicitations = error is UrlElicitationRequiredException typed ? JsonSerializer.SerializeToElement(typed.Elicitations) : (JsonElement?)null,
            Data = data
        }));
        Assert.Equal(McpErrorCode.UrlElicitationRequired, error.ErrorCode);
        Assert.Contains("authorize this lookup", error.Message);
        Assert.Single(handler.Requests, value => value.GetProperty("method").GetString() == "tools/call");
    }

    [Fact]
    public async Task FirstRouterAssociationAfterAgentShutdownRejectsBeforeConnector()
    {
        var calls = 0;
        await using var router = new McpRouter(new()
        {
            Servers = new Dictionary<string, Func<CancellationToken, Task<Client>>>
            {
                ["allowed"] = _ => { calls++; throw new InvalidOperationException("Connector must not run after owning agent shutdown."); }
            }
        });
        await using var agent = NewAgent();
        await agent.ShutdownAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => RouterConnectAsync(router, agent));
        Assert.Equal(0, calls);
    }

    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint, McpClientSettings settings, CancellationToken token = default) => Client.CreateHttpAsync(settings, http,
        new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) }, token);
    private static JsonElement CallMetadata(WireHandler handler)
    {
        var parameters = Assert.Single(handler.Requests, value => value.GetProperty("method").GetString() == "tools/call").GetProperty("params");
        return parameters.TryGetProperty("_meta", out var value) ? value : JsonSerializer.SerializeToElement(new { });
    }
    private static Agent NewAgent() => new(new AgentOptions { Model = new UnusedModel() });
    private static Task<ToolResultBlock> RouterConnectAsync(McpRouter router, Agent agent)
    {
        var input = JsonSerializer.SerializeToElement(new { command = "connect", server_name = "allowed", connection_id = "one" });
        return router.InvokeResultAsync(input, new ToolContext(agent, new("router-call", router.Spec.Name, input), new Dictionary<string, object?>())).AsTask();
    }
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class WireHandler : HttpMessageHandler
    {
        private readonly List<JsonElement> _requests = [];
        internal IReadOnlyList<JsonElement> Requests { get { lock (_requests) return _requests.ToArray(); } }
        internal bool UrlError { get; init; }
        internal bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement.Clone();
            lock (_requests) _requests.Add(root);
            if (!root.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var method = root.GetProperty("method").GetString();
            object payload;
            if (method == "tools/call" && UrlError)
            {
                payload = new { jsonrpc = "2.0", id, error = new { code = -32042, message = "authorize this lookup", data = new { elicitations = new[] { new { mode = "url", message = "authorize", elicitationId = "authorization-1", url = "https://consent.example.test/authorize" } } } } };
            }
            else
            {
                object result = method switch
                {
                    "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "inclusive-fixture", version = "1" } },
                    "tools/list" => new { tools = new[] { new { name = "lookup", description = "Lookup", inputSchema = new { type = "object" } } } },
                    "tools/call" => new { content = new[] { new { type = "text", text = "ok" } } },
                    _ => throw new InvalidOperationException("Unexpected MCP method " + method)
                };
                payload = new { jsonrpc = "2.0", id, result };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
