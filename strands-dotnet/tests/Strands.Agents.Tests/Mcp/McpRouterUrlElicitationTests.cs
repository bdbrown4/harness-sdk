using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;
using TaskServer = Strands.Agents.Tests.Mcp.McpTaskClientTests.TaskServer;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpRouterUrlElicitationTests
{
    [Fact]
    public Task DefaultThrowPolicyRetainsUrlGuidanceAndOfficialCause() => ScenarioAsync(false);

    [Fact]
    public Task ExplicitReturnResultRetainsCorrelatedUrlGuidanceAndOfficialCause() => ScenarioAsync(true);

    private static async Task ScenarioAsync(bool returnResult)
    {
        using var handler = UrlServer();
        var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        Client? transferred = null;
        var connections = 0;
        var servers = new Dictionary<string, Func<CancellationToken, Task<Client>>>
        {
            ["allowed"] = async token =>
            {
                Interlocked.Increment(ref connections);
                transferred = await Client.CreateHttpAsync(new McpClientSettings(), http,
                    new HttpClientTransportOptions { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
                    new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) }, token);
                return transferred;
            }
        };
        var options = new McpRouterOptions { Servers = servers };
        if (returnResult) options = options with { ErrorPolicy = McpRouterErrorPolicy.ReturnResult };
        var router = new McpRouter(options);
        servers.Clear();
        var model = new UnusedModel();
        var agent = new Agent(new AgentOptions { Model = model, Tools = [router] });
        Exception? primary = null;
        try
        {
            Assert.Equal("mcp_router", router.Spec.Name);
            Assert.Equal("object", router.Spec.InputSchema.GetProperty("properties").GetProperty("arguments").GetProperty("type").GetString());
            await Assert.ThrowsAsync<McpRouterToolException>(() => Within(Command(router, agent,
                new { command = "connect", server_name = "https://model-selected.invalid", connection_id = "forbidden" })));
            await Assert.ThrowsAsync<McpRouterToolException>(() => Within(Command(router, agent,
                new { command = "call_tool", connection_id = "selected", tool_name = "lookup", arguments = Array.Empty<object>() })));
            Assert.Equal(0, connections);
            Assert.Empty(handler.Requests);

            await Within(Command(router, agent, new { command = "connect", server_name = "allowed", connection_id = "selected" }));
            Assert.Equal(1, connections);
            Assert.NotNull(transferred);
            var listed = await Within(Command(router, agent, new { command = "list_tools", connection_id = "selected" }));
            var spec = Assert.Single(Assert.IsType<JsonBlock>(Assert.Single(listed.ContentBlocks)).Json.EnumerateArray());
            Assert.Equal("lookup", spec.GetProperty("name").GetString());
            Assert.Equal("string", spec.GetProperty("inputSchema").GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
            Assert.Equal("integer", spec.GetProperty("outputSchema").GetProperty("properties").GetProperty("answer").GetProperty("type").GetString());
            Assert.Same(router, Assert.Single(agent.Tools));
            await Assert.ThrowsAsync<InvalidDataException>(() => Within(Command(router, agent,
                new { command = "call_tool", connection_id = "selected", tool_name = "lookup", arguments = new { query = "invalid-output" } })));

            var arguments = new { query = "Café 雪", vendor = new { value = 7 } };
            string guidance;
            UrlElicitationRequiredException cause;
            if (returnResult)
            {
                var result = await Within(Command(router, agent,
                    new { command = "call_tool", connection_id = "selected", tool_name = "lookup", arguments }));
                Assert.Equal("router-guidance-call", result.ToolUseId);
                Assert.Equal(ToolResultStatus.Error, result.Status);
                cause = Assert.IsType<UrlElicitationRequiredException>(result.Error);
                guidance = result.Content;
            }
            else
            {
                var error = await Assert.ThrowsAsync<McpRouterToolException>(() => Within(Command(router, agent,
                    new { command = "call_tool", connection_id = "selected", tool_name = "lookup", arguments })));
                cause = Assert.IsType<UrlElicitationRequiredException>(error.InnerException);
                guidance = error.Message;
            }
            Assert.Equal(McpErrorCode.UrlElicitationRequired, cause.ErrorCode);
            var elicitation = Assert.Single(cause.Elicitations);
            Assert.Equal("url", elicitation.Mode);
            Assert.Equal("authorization-1", elicitation.ElicitationId);
            Assert.Equal("https://consent.example.test/authorize", elicitation.Url);
            Assert.StartsWith("MCP Elicitation required:", guidance);
            Assert.Contains(cause.Message, guidance);
            Assert.Contains("https://consent.example.test/authorize", guidance);
            Assert.Contains("authorization-1", guidance);
            var calls = handler.Requests.Where(value => value.Method == "tools/call").ToArray();
            Assert.Equal(2, calls.Length);
            Assert.All(calls, call => Assert.Equal("lookup", call.Parameters.GetProperty("name").GetString()));
            var wireArguments = calls[1].Parameters.GetProperty("arguments");
            Assert.Equal(2, wireArguments.EnumerateObject().Count());
            Assert.Equal("Café 雪", wireArguments.GetProperty("query").GetString());
            Assert.Equal(7, wireArguments.GetProperty("vendor").GetProperty("value").GetInt32());
            Assert.Empty(agent.Messages);
            Assert.Empty(agent.State.GetAll());
            Assert.Equal(0, model.Calls);
            await Within(agent.ShutdownAsync());
            Assert.False(transferred.IsConnected);
            Assert.False(handler.Disposed);
            using var response = await Within(http.GetAsync(server.Endpoint));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            var cleanup = new List<Exception>();
            await CloseAsync(() => agent.ShutdownAsync(), cleanup);
            await CloseAsync(() => agent.DisposeAsync().AsTask(), cleanup);
            await CloseAsync(() => router.DisposeAsync().AsTask(), cleanup);
            await CloseAsync(() => server.DisposeAsync().AsTask(), cleanup);
            if (cleanup.Count != 0)
            {
                if (primary is not null) primary.Data["McpRouterUrlFixtureCleanup"] = cleanup.AsReadOnly();
                else ExceptionDispatchInfo.Capture(cleanup[0]).Throw();
            }
        }
    }

    private static TaskServer UrlServer()
    {
        var handler = McpLegacyTaskTests.Server();
        var baseline = handler.Respond!;
        handler.Respond = (request, token) => request.Method == "tools/call"
            ? Task.FromResult<HttpResponseMessage?>(request.Parameters.GetProperty("arguments").GetProperty("query").GetString() == "invalid-output"
                ? TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new { content = new[] { new { type = "text", text = "bad schema" } }, structuredContent = new { answer = "invalid" } })
                : new(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        jsonrpc = "2.0",
                        id = request.Envelope.GetProperty("id"),
                        error = new
                        {
                            code = -32042,
                            message = "authorize this lookup",
                            data = new { elicitations = new[] { new { mode = "url", message = "authorize", elicitationId = "authorization-1", url = "https://consent.example.test/authorize" } } }
                        }
                    }), Encoding.UTF8, "application/json")
                })
            : request.Method == "tools/list"
            ? Task.FromResult<HttpResponseMessage?>(TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            {
                tools = new[] { new { name = "lookup", inputSchema = new { type = "object", properties = new { query = new { type = "string" } } }, outputSchema = new { type = "object", properties = new { answer = new { type = "integer" } }, required = new[] { "answer" } } } }
            }))
            : baseline(request, token);
        return handler;
    }

    private static Task<ToolResultBlock> Command(McpRouter router, Agent agent, object value)
    {
        var input = JsonSerializer.SerializeToElement(value);
        return router.InvokeResultAsync(input, new(agent, new("router-guidance-call", router.Spec.Name, input), new Dictionary<string, object?>())).AsTask();
    }

    private static async Task<T> Within<T>(Task<T> pending)
    {
        await Within((Task)pending);
        return await pending;
    }

    private static async Task Within(Task pending)
    {
        using var stop = new CancellationTokenSource();
        var watchdog = Task.Delay(TimeSpan.FromSeconds(10), stop.Token);
        try
        {
            if (await Task.WhenAny(pending, watchdog) != pending) throw new TimeoutException("MCP router URL fixture did not settle.");
            await pending;
        }
        finally
        {
            stop.Cancel();
            try { await watchdog; } catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { }
        }
    }

    private static async Task CloseAsync(Func<Task> close, List<Exception> failures)
    {
        try { await close(); } catch (Exception error) { failures.Add(error); }
    }

    private sealed class UnusedModel : IModel
    {
        internal int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The router fixture invokes the tool directly.");
        }
    }
}
