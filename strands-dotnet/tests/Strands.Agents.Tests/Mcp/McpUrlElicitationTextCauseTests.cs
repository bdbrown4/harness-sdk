using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpUrlElicitationTextCauseTests
{
    [Fact]
    public async Task DirectTextCallKeepsTheOfficialTypedCause()
    {
        using var handler = McpLegacyTaskTests.Server();
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
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await Client.CreateHttpAsync(new McpClientSettings(), http,
            new HttpClientTransportOptions { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) });
        var tool = Assert.Single(await client.ListToolsAsync());
        await using var agent = new Agent(new AgentOptions { Model = new UnusedModel() });
        var input = JsonSerializer.SerializeToElement(new { });
        var context = new ToolContext(agent, new ToolUseBlock("text-call", tool.Spec.Name, input), new Dictionary<string, object?>());

        var error = await Assert.ThrowsAnyAsync<Exception>(() => tool.InvokeAsync(input, context).AsTask());

        // A text error must retain the official typed cause, directly or as its inner exception.
        var cause = error as UrlElicitationRequiredException
            ?? Assert.IsType<UrlElicitationRequiredException>(Assert.IsType<InvalidOperationException>(error).InnerException);
        Assert.Equal(McpErrorCode.UrlElicitationRequired, cause.ErrorCode);
        Assert.Contains("authorize this lookup", cause.Message);
        var elicitation = Assert.Single(JsonSerializer.SerializeToElement(cause.Elicitations).EnumerateArray());
        Assert.Equal("authorization-1", elicitation.GetProperty("elicitationId").GetString());
        Assert.Equal("https://consent.example.test/authorize", elicitation.GetProperty("url").GetString());
        Assert.Single(handler.Requests, value => value.Method == "tools/call");
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A direct text MCP tool call must not invoke a model.");
    }
}
