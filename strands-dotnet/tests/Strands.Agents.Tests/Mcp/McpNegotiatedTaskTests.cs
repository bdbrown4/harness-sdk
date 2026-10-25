using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpNegotiatedTaskTests
{
    [Fact]
    public void EnabledTasksDefaultToNegotiatedProtocolSelection()
    {
        Assert.Equal("Negotiated", new McpTasksConfig().ProtocolProfile.ToString());
    }

    [Fact]
    public void AddingNegotiationPreservesTheExplicitProfilesNumericValues()
    {
        Assert.Equal(0, (int)McpTasksProtocolProfile.Finalized20260728);
        Assert.Equal(1, (int)McpTasksProtocolProfile.Legacy20251125);
        Assert.Equal(2, (int)McpTasksProtocolProfile.Negotiated);
    }

    [Fact]
    public async Task DefaultSelectionUsesLegacyTasksAfterTheRealHttpHandshake()
    {
        using var handler = McpLegacyTaskTests.Server();
        var baseline = handler.Respond!;
        handler.Respond = (request, token) => request.Method == "server/discover"
            ? Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = request.Envelope.GetProperty("id"),
                    error = new { code = -32601, message = "Legacy fixture requires initialize." }
                }), Encoding.UTF8, "application/json")
            })
            : baseline(request, token);
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, null, server.Endpoint);

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { value = "Café 雪" }));

        Assert.Equal("legacy result Café 雪", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Single(handler.Requests, request => request.Method == "tools/list");
        Assert.Single(handler.Requests, request => request.Method == "tasks/result");
        Assert.Empty(Assert.Single(handler.Requests, request => request.Method == "tools/call").Parameters.GetProperty("task").EnumerateObject());
        var initialize = Assert.Single(handler.Requests, request => request.Method == "initialize");
        Assert.True(initialize.Parameters.GetProperty("capabilities").GetProperty("extensions").TryGetProperty("io.modelcontextprotocol/tasks", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegotiatedRetentionIsSentOnlyWhenLegacyExecutionIsSelected(bool legacy)
    {
        using var server = legacy ? McpLegacyTaskTests.Server() : new McpTaskClientTests.TaskServer();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, legacy ? "2025-11-25" : "2026-07-28", config: new()
        { LegacyTimeToLive = TimeSpan.FromSeconds(45), PollInterval = TimeSpan.FromMilliseconds(10) });

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal(legacy ? "legacy result Café 雪" : "done", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        var parameters = Assert.Single(server.Requests, request => request.Method == "tools/call").Parameters;
        Assert.Equal(legacy, parameters.TryGetProperty("task", out var task));
        if (legacy) Assert.Equal(45000, task.GetProperty("ttl").GetInt64());
        Assert.False(parameters.TryGetProperty("ttl", out _));
        Assert.False(parameters.TryGetProperty("legacyTtl", out _));
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("get")]
    [InlineData("update")]
    [InlineData("cancel")]
    public async Task NegotiatedModernSessionsExposeTheFinalizedLifecycle(string operation)
    {
        using var server = new McpTaskClientTests.TaskServer();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, "2026-07-28");

        if (operation == "submit") Assert.NotNull((await client.SubmitToolAsync("lookup", JsonSerializer.SerializeToElement(new { }))).Task);
        else if (operation == "get") Assert.Equal(server.TaskId, (await client.GetTaskAsync(server.TaskId)).TaskId);
        else if (operation == "cancel") Assert.Equal("complete", (await client.CancelTaskAsync(server.TaskId)).GetProperty("resultType").GetString());
        else Assert.Equal("complete", (await client.UpdateTaskAsync(server.TaskId, new Dictionary<string, InputResponse>())).GetProperty("resultType").GetString());
    }

    [Fact]
    public async Task DefaultSelectionUsesFinalizedTasksAfterRealHttpDiscovery()
    {
        using var handler = new McpTaskClientTests.TaskServer();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, null, server.Endpoint);

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal("done", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Single(handler.Requests, request => request.Method == "server/discover");
        Assert.Single(handler.Requests, request => request.Method == "tasks/get");
        Assert.DoesNotContain(handler.Requests, request => request.Method is "initialize" or "tools/list" or "tasks/result");
    }

    [Theory]
    [InlineData(true, true, false, "forbidden")]
    [InlineData(true, false, false, "optional")]
    [InlineData(true, false, true, "optional")]
    [InlineData(false, true, false, "required")]
    public async Task NegotiationNeverCombinesAuthorityFromDifferentProtocolGenerations(bool legacy, bool legacyCapability, bool modernExtension, string support)
    {
        using var server = OrdinaryServer(legacy, legacyCapability, modernExtension, support);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, legacy ? "2025-11-25" : "2026-07-28");

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal("ordinary", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.False(Assert.Single(server.Requests, request => request.Method == "tools/call").Parameters.TryGetProperty("task", out _));
        Assert.DoesNotContain(server.Requests, request => request.Method.StartsWith("tasks/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(McpTasksProtocolProfile.Finalized20260728, true)]
    [InlineData(McpTasksProtocolProfile.Legacy20251125, false)]
    public async Task ExplicitProfilesRemainSelectedWhenTheOtherGenerationIsAdvertised(McpTasksProtocolProfile profile, bool legacy)
    {
        using var server = OrdinaryServer(legacy, legacyCapability: true, modernExtension: !legacy, support: "optional");
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, legacy ? "2025-11-25" : "2026-07-28", config: new() { ProtocolProfile = profile });

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal("ordinary", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.DoesNotContain(server.Requests, request => request.Method is "tools/list" || request.Method.StartsWith("tasks/", StringComparison.Ordinal));
        Assert.False(Assert.Single(server.Requests, request => request.Method == "tools/call").Parameters.TryGetProperty("task", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NegotiatedLifecycleRequiresModernProtocolAndItsExtension(bool legacy)
    {
        using var server = OrdinaryServer(legacy, legacyCapability: true);
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, legacy ? "2025-11-25" : "2026-07-28");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetTaskAsync(server.TaskId).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UpdateTaskAsync(server.TaskId, new Dictionary<string, InputResponse>()).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CancelTaskAsync(server.TaskId).AsTask());

        Assert.DoesNotContain(server.Requests, request => request.Method == "tools/call" || request.Method.StartsWith("tasks/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryFallbackRetainsBothRequestAndOverallBudgets(bool overall)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = OrdinaryServer(legacy: false);
        var baseline = server.Respond!;
        server.Respond = async (request, token) =>
        {
            if (request.Method == "tools/call") { entered.TrySetResult(true); await release.Task.ConfigureAwait(false); }
            return await baseline(request, token).ConfigureAwait(false);
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, "2026-07-28", config: new()
        {
            RequestTimeout = TimeSpan.FromMilliseconds(overall ? 5000 : 200),
            PollTimeout = TimeSpan.FromMilliseconds(overall ? 200 : 5000)
        });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(5000)));
            Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(2000)));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => invocation);
            Assert.Contains(overall ? "overall" : "inactivity", error.Message);
        }
        finally
        {
            release.TrySetResult(true);
            try { await invocation; } catch (TimeoutException) { }
        }
    }

    [Fact]
    public async Task OrdinaryFallbackRetainsRequestedCancellationAndBorrowedHttpOwnership()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = OrdinaryServer(legacy: false);
        var baseline = server.Respond!;
        server.Respond = async (request, token) =>
        {
            if (request.Method != "tools/call") return await baseline(request, token).ConfigureAwait(false);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return null;
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http, "2026-07-28");
        using var cancellation = new CancellationTokenSource();
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), cancellationToken: cancellation.Token).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(5000)));

        cancellation.Cancel();

        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(5000)));
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation)).CancellationToken);
        await client.DisposeAsync();
        Assert.False(server.Disposed);
    }

    [Fact]
    public async Task OmittingTaskSettingsPreservesOrdinaryCallsToTaskCapableServers()
    {
        using var server = McpLegacyTaskTests.Server();
        using var http = new HttpClient(server);
        await using var client = await Client.CreateHttpAsync(http,
            new HttpClientTransportOptions { Endpoint = new Uri("https://ordinary.mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new McpClientOptions { ProtocolVersion = "2025-11-25" });

        var result = await client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal("ordinary", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.DoesNotContain(server.Requests, request => request.Method == "tools/list" || request.Method.StartsWith("tasks/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenuineStdioNegotiatesEachGenerationAndReapsItsOwnedProcess(bool legacy)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands negotiated mcp " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "legacy-created.txt");
            await using var client = await Client.CreateStdioAsync(new McpClientSettings { TasksConfig = new() { PollTimeout = TimeSpan.FromSeconds(20) } },
                StdioOptions(legacy, marker), new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(10) });
            using var process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo!.Version, System.Globalization.CultureInfo.InvariantCulture));

            var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "negotiated Café 雪" }));

            Assert.Equal("negotiated Café 雪", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.Equal(legacy ? "2025-11-25" : "2026-07-28", client.Protocol.NegotiatedProtocolVersion);
            Assert.Equal(legacy, File.Exists(marker));
            await client.DisposeAsync();
            Assert.True(await Task.Run(() => process.WaitForExit(10000)), "The official connection must reap its owned negotiated stdio process.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static McpTaskClientTests.TaskServer OrdinaryServer(bool legacy, bool legacyCapability = false, bool modernExtension = false, string support = "forbidden")
    {
        var server = new McpTaskClientTests.TaskServer();
        var capabilities = new Dictionary<string, object> { ["tools"] = new { } };
        if (legacyCapability) capabilities["tasks"] = new { cancel = new { }, requests = new { tools = new { call = new { } } } };
        if (modernExtension) capabilities["extensions"] = new Dictionary<string, object> { ["io.modelcontextprotocol/tasks"] = new { } };
        server.Respond = (request, token) => Task.FromResult(request.Method switch
        {
            "initialize" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            { protocolVersion = legacy ? "2025-11-25" : "2026-07-28", capabilities, serverInfo = new { name = "ordinary", version = "1.0" } }),
            "server/discover" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            { supportedVersions = new[] { "2026-07-28" }, capabilities, ttlMs = 0, cacheScope = "private" }),
            "tools/list" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            { tools = new[] { new { name = "lookup", inputSchema = new { type = "object" }, execution = new { taskSupport = support } } } }),
            "tools/call" => McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            { content = new[] { new { type = "text", text = "ordinary" } } }),
            _ => null
        });
        return server;
    }

    private static StdioClientTransportOptions StdioOptions(bool legacy, string marker)
    {
        var arguments = new[] { legacy ? "--legacy-tasks" : "--tasks", "--legacy-task-marker=" + marker };
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture));
        return new() { Command = fixture, Arguments = arguments };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture));
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture }.Concat(arguments).ToArray() };
#endif
    }

    private static Task<Client> ConnectAsync(HttpClient http, string? protocolVersion, Uri? endpoint = null, McpTasksConfig? config = null) =>
        Client.CreateHttpAsync(new McpClientSettings { TasksConfig = config ?? new() { PollInterval = TimeSpan.FromMilliseconds(10) } }, http,
            new HttpClientTransportOptions { Endpoint = endpoint ?? new Uri("https://negotiated.mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new McpClientOptions { ProtocolVersion = protocolVersion, InitializationTimeout = TimeSpan.FromSeconds(5) });
}
