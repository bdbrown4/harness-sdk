using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;
using TaskServer = Strands.Agents.Tests.Mcp.McpTaskClientTests.TaskServer;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpUrlElicitationGuidanceTests
{
    [Fact]
    public async Task ValidUrlElicitationBecomesActionableCorrelatedAgentToolErrorWithOriginalCause()
    {
        using var handler = UrlErrorServer();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var tool = Assert.Single(await client.ListToolsAsync());
        var hooks = new HookRegistry();
        ToolResultBlock? observed = null;
        hooks.Register<AfterToolCallEvent>((value, _) => { observed = value.Result; return default; });
        var model = new UnusedModel();
        await using var agent = new Agent(new AgentOptions { Model = model, Tools = [tool], Hooks = hooks });

        var result = await agent.InvokeToolAsync(new ToolUseBlock("guidance-call", tool.Spec.Name, Input()));

        Assert.Equal("guidance-call", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.NotNull(observed);
        var heldCause = Assert.IsType<UrlElicitationRequiredException>(observed.Error);
        Assert.Same(heldCause, result.Error);
        AssertValidCause(heldCause);
        Assert.Equal(0, model.Calls);
        Assert.Single(handler.Requests, value => value.Method == "tools/call");
        Assert.StartsWith("MCP Elicitation required:", result.Content);
        Assert.Contains(heldCause.Message, result.Content);
        Assert.Contains("https://consent.example.test/authorize", result.Content);
        Assert.Contains("authorization-1", result.Content);
    }

    [Fact]
    public async Task RawCallStillThrowsTheOfficialTypedUrlException()
    {
        using var handler = UrlErrorServer();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);

        var error = await Assert.ThrowsAnyAsync<McpProtocolException>(() => client.CallToolAsync("lookup", Input()).AsTask());

        AssertValidCause(Assert.IsType<UrlElicitationRequiredException>(error));
        Assert.Single(handler.Requests, value => value.Method == "tools/call");
    }

    private static void AssertValidCause(UrlElicitationRequiredException error)
    {
        Assert.Equal(McpErrorCode.UrlElicitationRequired, error.ErrorCode);
        Assert.Contains("authorize this lookup", error.Message);
        var elicitation = Assert.Single(JsonSerializer.SerializeToElement(error.Elicitations).EnumerateArray());
        Assert.Equal("url", elicitation.GetProperty("mode").GetString());
        Assert.Equal("https://consent.example.test/authorize", elicitation.GetProperty("url").GetString());
        Assert.Equal("authorization-1", elicitation.GetProperty("elicitationId").GetString());
    }

    private static TaskServer UrlErrorServer()
    {
        var handler = McpLegacyTaskTests.Server();
        var baseline = handler.Respond!;
        handler.Respond = (request, token) => request.Method == "tools/call"
            ? Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = request.Envelope.GetProperty("id"),
                    error = new
                    {
                        code = -32042,
                        message = "authorize this lookup",
                        data = new
                        {
                            elicitations = new[]
                            {
                                new { mode = "url", message = "authorize", elicitationId = "authorization-1", url = "https://consent.example.test/authorize" }
                            }
                        }
                    }
                }), Encoding.UTF8, "application/json")
            })
            : baseline(request, token);
        return handler;
    }

    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint) => Client.CreateHttpAsync(new McpClientSettings(), http,
        new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) });

    private static JsonElement Input() => JsonSerializer.SerializeToElement(new { });

    private sealed class UnusedModel : IModel
    {
        internal int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The guidance test invokes one registered MCP tool without a model.");
        }
    }
}
