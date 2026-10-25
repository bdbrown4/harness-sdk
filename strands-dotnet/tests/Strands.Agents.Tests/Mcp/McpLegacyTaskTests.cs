using System.Net;
using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpLegacyTaskTests
{
    [Theory]
    [InlineData("optional")]
    [InlineData("required")]
    public async Task GenuineHttpDiscoversTaskSupportAndConsumesTheLegacyResult(string support)
    {
        using var handler = Server(support);
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { value = "legacy Café 雪" }));

        Assert.Equal("legacy result Café 雪", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Empty(Assert.Single(handler.Requests, request => request.Method == "tools/call").Parameters.GetProperty("task").EnumerateObject());
        Assert.Single(handler.Requests, request => request.Method == "tools/list");
        Assert.Single(handler.Requests, request => request.Method == "tasks/get");
        Assert.Single(handler.Requests, request => request.Method == "tasks/result");
    }

    [Fact]
    public async Task LegacyInitializationDoesNotMutateCallerCapabilities()
    {
        using var server = Server();
        using var http = new HttpClient(server);
        var capabilities = new ClientCapabilities();
        var before = JsonSerializer.Serialize(capabilities);
        var options = new McpClientOptions
        {
            ProtocolVersion = "2025-11-25",
            Capabilities = capabilities,
            Handlers = new()
            { ElicitationHandler = (_, _) => new ValueTask<ElicitResult>(new ElicitResult { Action = "cancel" }) }
        };
        await using var client = await Client.CreateHttpAsync(new McpClientSettings { TasksConfig = new() { ProtocolProfile = McpTasksProtocolProfile.Legacy20251125 } }, http,
            new HttpClientTransportOptions { Endpoint = new Uri("https://legacy.mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false }, options);

        Assert.Equal(before, JsonSerializer.Serialize(capabilities));
        Assert.Same(capabilities, options.Capabilities);
    }

    [Theory]
    [InlineData("forbidden", true)]
    [InlineData("optional", false)]
    public async Task TaskAugmentationRequiresBothServerAndToolAuthority(string support, bool capability)
    {
        using var server = Server(support, capability);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal("ordinary", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.False(Assert.Single(server.Requests, request => request.Method == "tools/call").Parameters.TryGetProperty("task", out _));
        Assert.DoesNotContain(server.Requests, request => request.Method.StartsWith("tasks/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitPythonStyleWireTtlIsSentOnlyWhenConfigured()
    {
        using var server = Server();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, config: new()
        { ProtocolProfile = McpTasksProtocolProfile.Legacy20251125, LegacyTimeToLive = TimeSpan.FromSeconds(45), PollInterval = TimeSpan.FromMilliseconds(10) });

        await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal(45000, Assert.Single(server.Requests, request => request.Method == "tools/call").Parameters.GetProperty("task").GetProperty("ttl").GetInt64());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10000)]
    [InlineData(1)]
    public async Task InvalidLegacyTtlFailsBeforeAnyConnectionOrRequest(long ticks)
    {
        using var server = Server();
        using var http = new HttpClient(server);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ConnectAsync(http, config: new()
        { ProtocolProfile = McpTasksProtocolProfile.Legacy20251125, LegacyTimeToLive = TimeSpan.FromTicks(ticks) }));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task LegacyRetentionCannotBeSilentlyIgnoredByTheFinalizedProfile()
    {
        using var server = Server();
        using var http = new HttpClient(server);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ConnectAsync(http, config: new() { ProtocolProfile = McpTasksProtocolProfile.Finalized20260728, LegacyTimeToLive = TimeSpan.FromSeconds(1) }));
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task LegacyTerminalFailuresRetainTheirStatusWithoutInventingRpcCodes(string status)
    {
        using var server = Server();
        var baseline = server.Respond!;
        server.Respond = (request, token) => request.Method == "tasks/get"
            ? Task.FromResult<HttpResponseMessage?>(McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            { taskId = server.TaskId, status, statusMessage = "original server status", createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:01Z", ttl = (int?)null }))
            : baseline(request, token);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();

        if (status == "failed") Assert.Equal("original server status", (await Assert.ThrowsAsync<McpLegacyTaskFailedException>(() => invocation)).StatusMessage);
        else Assert.Contains("original server status", (await Assert.ThrowsAsync<McpTaskCancelledException>(() => invocation)).Message);
        Assert.DoesNotContain(server.Requests, request => request.Method is "tasks/result" or "tasks/cancel");
    }

    [Fact]
    public async Task CompletedIsErrorRemainsAToolResultRatherThanAFailedTask()
    {
        using var server = Server();
        var baseline = server.Respond!;
        server.Respond = (request, token) => request.Method == "tasks/result"
            ? Task.FromResult<HttpResponseMessage?>(McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            { isError = true, content = new[] { new { type = "text", text = "tool body failed" } } })) : baseline(request, token);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        Assert.True((await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }))).IsError);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("get")]
    [InlineData("update")]
    [InlineData("cancel")]
    public async Task FinalizedLifecycleMethodsRejectTheLegacyOnlyProfile(string operation)
    {
        using var server = Server();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (operation == "submit") await client.SubmitToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));
            else if (operation == "get") await client.GetTaskAsync(server.TaskId);
            else if (operation == "cancel") await client.CancelTaskAsync(server.TaskId);
            else await client.UpdateTaskAsync(server.TaskId, new Dictionary<string, InputResponse>());
        });
        Assert.DoesNotContain(server.Requests, request => request.Method is "tools/call" or "tasks/get" or "tasks/update" or "tasks/cancel");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("status")]
    [InlineData("ttl")]
    public async Task MalformedPollStatesCancelOnlyTheOriginalTask(string fault)
    {
        using var server = Server();
        var baseline = server.Respond!;
        server.Respond = (request, token) =>
        {
            if (request.Method != "tasks/get") return baseline(request, token);
            var state = JsonSerializer.SerializeToElement(State(server.TaskId, "completed")).EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            if (fault == "identity") state["taskId"] = JsonSerializer.SerializeToElement("another-task");
            else if (fault == "status") state["status"] = JsonSerializer.SerializeToElement("unknown");
            else state.Remove("ttl");
            return Task.FromResult<HttpResponseMessage?>(McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), state));
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask());

        Assert.Equal(server.TaskId, Assert.Single(server.Requests, request => request.Method == "tasks/cancel").Parameters.GetProperty("taskId").GetString());
        Assert.DoesNotContain(server.Requests, request => request.Method == "tasks/result");
    }

    internal static Task<Client> ConnectAsync(HttpClient http, Uri? endpoint = null, McpTasksConfig? config = null, McpClientHandlers? handlers = null) => Client.CreateHttpAsync(
        new McpClientSettings { TasksConfig = config ?? new() { ProtocolProfile = McpTasksProtocolProfile.Legacy20251125, PollInterval = TimeSpan.FromMilliseconds(10) } }, http,
        new HttpClientTransportOptions { Endpoint = endpoint ?? new Uri("https://legacy.mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5), Handlers = handlers ?? new() });

    internal static McpTaskClientTests.TaskServer Server(string? support = "optional", bool capability = true)
    {
        var server = new McpTaskClientTests.TaskServer();
        server.Respond = (request, cancellationToken) => Task.FromResult(request.Method switch
        {
            "initialize" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            {
                protocolVersion = "2025-11-25",
                serverInfo = new { name = "legacy-task-fixture", version = "1.0" },
                capabilities = (object)(capability ? new { tools = new { }, tasks = new { cancel = new { }, requests = new { tools = new { call = new { } } } } } : new { tools = new { } })
            }),
            "tools/list" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            {
                tools = new[] { new { name = "lookup", inputSchema = new { type = "object" }, execution = new { taskSupport = support } } }
            }),
            "tools/call" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), request.Parameters.TryGetProperty("task", out _)
                ? (object)new { task = State(server.TaskId, "working") }
                : new { content = new[] { new { type = "text", text = "ordinary" } } }),
            "tasks/get" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), State(server.TaskId, "completed")),
            "tasks/result" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new { content = new[] { new { type = "text", text = "legacy result Café 雪" } } }),
            "tasks/cancel" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), State(server.TaskId, "cancelled")),
            _ => null
        });
        return server;
    }

    internal static object State(string taskId, string status, int? pollInterval = 10) => new
    { taskId, status, createdAt = "2026-10-20T10:00:00Z", lastUpdatedAt = "2026-10-20T10:00:01Z", ttl = (int?)null, pollInterval };
}
