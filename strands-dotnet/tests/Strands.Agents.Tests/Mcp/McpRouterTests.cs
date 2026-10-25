using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using McpClient = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpRouterTests
{
    [Fact]
    public async Task FullProtocolLifecycleKeepsConnectionsAcrossCommandsAndUsesOriginalToolNames()
    {
        using var server = new RouterServer();
        await using var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();

        Assert.Equal("", (await Command(router, agent, new { command = "list_connections" })).Content);
        Assert.Contains("api", (await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "selected" })).Content);
        Assert.Equal("selected", (await Command(router, agent, new { command = "list_connections" })).Content);
        var listed = await Command(router, agent, new { command = "list_tools", connection_id = "selected" });
        var specs = Assert.IsType<JsonBlock>(Assert.Single(listed.ContentBlocks));
        Assert.Equal("echo", specs.Json[0].GetProperty("name").GetString());
        var result = await Command(router, agent, new { command = "call_tool", connection_id = "selected", tool_name = "echo", arguments = new { text = "Café 雪" } });
        Assert.Equal("Café 雪", result.Content);
        Assert.Equal("router-call", result.ToolUseId);
        Assert.Equal("echo", Assert.Single(server.Requests, value => value.GetProperty("method").GetString() == "tools/call").GetProperty("params").GetProperty("name").GetString());
        Assert.Equal("Successfully disconnected", (await Command(router, agent, new { command = "disconnect", connection_id = "selected" })).Content);
        Assert.Equal("", (await Command(router, agent, new { command = "list_connections" })).Content);
        Assert.Equal(1, server.Deletes);
        Assert.True(server.Disposed);
    }

    [Fact]
    public async Task CancellationStopsWaitingForAnUncooperativeConnectorAndClosesItsLateClient()
    {
        using var server = new RouterServer();
        var candidate = await server.ConnectAsync(default);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<McpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var router = new McpRouter(new()
        {
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["api"] = _ => { entered.TrySetResult(true); return release.Task; } }
        });
        await using var agent = NewAgent();
        using var cancellation = new CancellationTokenSource();
        var connecting = Command(router, agent, new { command = "connect", server_name = "api", connection_id = "late" }, cancellation.Token);
        try
        {
            await Bounded(entered.Task);
            cancellation.Cancel();
            await Bounded(connecting);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            release.TrySetResult(candidate);
            try { await connecting; } catch (OperationCanceledException) { }
        }
        await Bounded(server.DisposedSignal.Task);
        Assert.Equal(1, server.Deletes);
        Assert.Equal("", (await Command(router, agent, new { command = "list_connections" })).Content);
    }

    [Fact]
    public async Task DisconnectCancellationStopsItsWaitWhileTheOwnedCloseStillDrainsAnActiveCall()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new RouterServer { HandleAsync = async (_, _) => { entered.TrySetResult(true); await release.Task; return new { content = new[] { new { type = "text", text = "completed" } } }; } };
        await using var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "active" });
        var calling = Command(router, agent, new { command = "call_tool", connection_id = "active", tool_name = "echo" });
        using var cancellation = new CancellationTokenSource();
        Task<ToolResultBlock>? disconnecting = null;
        try
        {
            await Bounded(entered.Task);
            disconnecting = Command(router, agent, new { command = "disconnect", connection_id = "active" }, cancellation.Token);
            cancellation.Cancel();
            await Bounded(disconnecting);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnecting);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(server.Disposed);
        }
        finally
        {
            release.TrySetResult(true);
            await calling;
            if (disconnecting is not null) try { await disconnecting; } catch (OperationCanceledException) { }
        }
        await Bounded(server.DisposedSignal.Task);
        Assert.Equal(1, server.Deletes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"command\":\"unknown\"}")]
    [InlineData("{\"command\":\"connect\"}")]
    [InlineData("{\"command\":\"connect\",\"connection_id\":\"c\"}")]
    [InlineData("{\"command\":\"call_tool\",\"connection_id\":\"c\"}")]
    [InlineData("{\"command\":\"list_connections\",\"arguments\":[]}")]
    public async Task InvalidCommandsAreRejectedWithoutCallingAConnector(string json)
    {
        var connections = 0;
        await using var router = new McpRouter(new()
        {
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["allowed"] = _ => { connections++; throw new InvalidOperationException("must not connect"); } }
        });
        await using var agent = NewAgent();
        using var document = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<McpRouterToolException>(async () => await router.InvokeResultAsync(document.RootElement, new(agent, new("call", "mcp_router", document.RootElement), new Dictionary<string, object?>())));
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task AllowlistAndDescriptionAreCopiedBeforeCallerMutation()
    {
        var servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
        { ["z"] = _ => throw new InvalidOperationException(), ["a"] = _ => throw new InvalidOperationException() };
        await using var router = new McpRouter(new() { Servers = servers });
        servers.Clear();
        await using var agent = NewAgent();
        Assert.Contains("'a', 'z'", router.Spec.Description);
        var error = await Assert.ThrowsAsync<McpRouterToolException>(() => Command(router, agent, new { command = "connect", server_name = "https://model-provided.invalid", connection_id = "new" }));
        Assert.Contains("a, z", error.Message);
        Assert.Equal("mcp_router", router.Spec.Name);
    }

    [Theory]
    [InlineData(McpRouterErrorPolicy.Throw)]
    [InlineData(McpRouterErrorPolicy.ReturnResult)]
    public async Task ExplicitErrorPoliciesPreserveTheSelectedTypeScriptOrPythonBehavior(McpRouterErrorPolicy policy)
    {
        using var server = new RouterServer { HandleAsync = (_, _) => Task.FromResult<object?>(new { isError = true, content = new[] { new { type = "text", text = "remote failure" } }, structuredContent = false }) };
        await using var router = new McpRouter(new() { ErrorPolicy = policy, Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        if (policy == McpRouterErrorPolicy.Throw)
        {
            var error = await Assert.ThrowsAsync<McpRouterToolException>(() => Command(router, agent, new { command = "call_tool", connection_id = "c", tool_name = "unlisted" }));
            Assert.Equal("remote failure", error.Message);
        }
        else
        {
            var result = await Command(router, agent, new { command = "call_tool", connection_id = "c", tool_name = "unlisted" });
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal("remote failure", result.Content);
            Assert.Equal(JsonValueKind.False, result.StructuredContent!.Value.ValueKind);
            Assert.Equal("router-call", result.ToolUseId);
        }
    }

    [Fact]
    public async Task DirectCallsRetainRichResultsWithoutRequiringDiscovery()
    {
        using var server = new RouterServer
        {
            HandleAsync = (_, _) => Task.FromResult<object?>(new
            { content = new object[] { new { type = "text", text = "rich" }, new { type = "image", mimeType = "image/png", data = "AQID" } }, structuredContent = new { value = 42 } })
        };
        await using var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        var result = await Command(router, agent, new { command = "call_tool", connection_id = "c", tool_name = "unlisted" });
        Assert.Equal("AQID", Assert.IsType<ImageBlock>(result.ContentBlocks[1]).Data);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("value").GetInt32());
        Assert.DoesNotContain(server.Requests, value => value.GetProperty("method").GetString() == "tools/list");
    }

    [Fact]
    public async Task PythonStyleResultsRetainOpaqueProtocolMetadataAlongsideRichErrorContent()
    {
        using var server = new RouterServer
        {
            HandleAsync = (_, _) => Task.FromResult<object?>(new
            {
                isError = true,
                content = new[] { new { type = "text", text = "remote failure" } },
                structuredContent = false,
                _meta = new { vendor = new { receipt = "opaque", values = new[] { 1, 2 } } }
            })
        };
        await using var router = new McpRouter(new() { ErrorPolicy = McpRouterErrorPolicy.ReturnResult, Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });

        var result = await Command(router, agent, new { command = "call_tool", connection_id = "c", tool_name = "echo" });
        var serialized = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(serialized.TryGetProperty("metadata", out var metadata), "The Python-style MCP result must retain the protocol _meta object.");
        Assert.Equal("opaque", metadata.GetProperty("vendor").GetProperty("receipt").GetString());
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal(JsonValueKind.False, result.StructuredContent!.Value.ValueKind);
    }

    [Fact]
    public async Task DiscoveryPaginatesInServerOrderAndItsFilterDoesNotBecomeCallAuthority()
    {
        var pages = 0;
        using var server = new RouterServer
        {
            Handle = root => root.GetProperty("method").GetString() != "tools/list" ? null :
            JsonSerializer.Deserialize<JsonElement>(pages++ == 0 ? """{"tools":[{"name":"hidden","inputSchema":{"type":"object"}}],"nextCursor":"next"}""" : """{"tools":[{"name":"echo","inputSchema":{"type":"object"}}]}""")
        };
        await using var router = new McpRouter(new() { ToolFilter = (serverName, name) => serverName == "api" && name == "echo", Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        var result = await Command(router, agent, new { command = "list_tools", connection_id = "c" });
        Assert.Equal("echo", Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json[0].GetProperty("name").GetString());
        Assert.Equal(2, pages);
        Assert.Equal("called", (await Command(router, agent, new { command = "call_tool", connection_id = "c", tool_name = "hidden", arguments = new { text = "called" } })).Content);
    }

    [Fact]
    public async Task RouterDiscoveryDoesNotApplyAgentRegistrationNameLimitsToServerSideNames()
    {
        var serverName = new string('x', 65);
        using var server = new RouterServer
        {
            Handle = request => request.GetProperty("method").GetString() == "tools/list"
            ? new { tools = new[] { new { name = serverName, inputSchema = new { type = "object" } } } } : null
        };
        await using var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });

        var result = await Command(router, agent, new { command = "list_tools", connection_id = "c" });

        Assert.Equal(serverName, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task TextInvocationProjectsToolListingsAsJsonWithoutDiscardingTheResult()
    {
        using var server = new RouterServer();
        await using var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        var input = JsonSerializer.SerializeToElement(new { command = "list_tools", connection_id = "c" });

        var result = await router.InvokeAsync(input, new(agent, new("call", router.Spec.Name, input), new Dictionary<string, object?>()));

        Assert.Contains("echo", result);
        Assert.Equal("echo", JsonSerializer.Deserialize<JsonElement>(result)[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SharedRouterKeepsAgentsAndChildrenIndependentAndShutsDownOwnedClients()
    {
        var servers = new List<RouterServer>();
        await using var router = new McpRouter(new()
        {
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["api"] = token => { var server = new RouterServer(); servers.Add(server); return server.ConnectAsync(token); } }
        });
        await using var parent = NewAgent();
        await using var child = parent.CreateChild();
        await Command(router, parent, new { command = "connect", server_name = "api", connection_id = "z" });
        await Command(router, parent, new { command = "connect", server_name = "api", connection_id = "a" });
        Assert.Equal("a, z", (await Command(router, parent, new { command = "list_connections" })).Content);
        Assert.Equal("", (await Command(router, child, new { command = "list_connections" })).Content);
        Assert.Empty(parent.Messages); Assert.Empty(parent.State.GetAll());
        await parent.ShutdownAsync();
        Assert.All(servers, server => Assert.True(server.Disposed));
        await Command(router, child, new { command = "connect", server_name = "api", connection_id = "a" });
        Assert.Equal("a", (await Command(router, child, new { command = "list_connections" })).Content);
    }

    [Fact]
    public async Task ConcurrentDuplicateAdmissionClosesOnlyTheLosingCandidate()
    {
        using var first = new RouterServer(); using var second = new RouterServer();
        var clients = new[] { await first.ConnectAsync(default), await second.ConnectAsync(default) };
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var router = new McpRouter(new()
        {
            MaxConnections = 2,
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["api"] = async _ => { var index = Interlocked.Increment(ref count) - 1; if (index == 1) entered.TrySetResult(true); await release.Task; return clients[index]; } }
        });
        await using var agent = NewAgent();
        var one = Command(router, agent, new { command = "connect", server_name = "api", connection_id = "same" });
        var two = Command(router, agent, new { command = "connect", server_name = "api", connection_id = "same" });
        try { await Bounded(entered.Task); }
        finally { release.TrySetResult(true); }
        try { await Task.WhenAll(one, two); } catch (McpRouterToolException) { }
        _ = Assert.Single(new[] { one, two }, task => task.Status == TaskStatus.RanToCompletion);
        Assert.IsType<McpRouterToolException>(Assert.Single(new[] { one, two }, task => task.IsFaulted).Exception!.InnerException);
        Assert.Equal(1, first.Deletes + second.Deletes);
        await agent.ShutdownAsync();
        Assert.Equal(2, first.Deletes + second.Deletes);
    }

    [Fact]
    public async Task ATransferredClientCannotBeReusedOrCloseAnotherAgentsActiveConnection()
    {
        using var server = new RouterServer();
        var client = await server.ConnectAsync(default);
        await using var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = _ => Task.FromResult(client) } });
        await using var parent = NewAgent(); await using var other = NewAgent();
        await Command(router, parent, new { command = "connect", server_name = "api", connection_id = "owned" });
        var failure = await Assert.ThrowsAsync<McpRouterToolException>(() => Command(router, other, new { command = "connect", server_name = "api", connection_id = "borrowed" }));
        Assert.Contains("fresh", failure.Message);
        Assert.True(client.IsConnected);
        Assert.Equal("still active", (await Command(router, parent, new { command = "call_tool", connection_id = "owned", tool_name = "echo", arguments = new { text = "still active" } })).Content);
    }

    [Fact]
    public async Task AConnectorCannotDisposeItsRouterWhileItsOwnCommandHoldsAnOperationLease()
    {
        using var server = new RouterServer();
        McpRouter? instance = null;
        await using var router = instance = new(new()
        {
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["api"] = token => { Assert.Throws<InvalidOperationException>(() => instance!.DisposeAsync()); return server.ConnectAsync(token); } }
        });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        Assert.Equal("c", (await Command(router, agent, new { command = "list_connections" })).Content);
    }

    [Fact]
    public async Task AnUncooperativeCanceledFactoryRetainsItsPhysicalAdmissionSlotUntilItReturns()
    {
        using var first = new RouterServer(); using var second = new RouterServer();
        var firstClient = await first.ConnectAsync(default); var secondClient = await second.ConnectAsync(default);
        var release = new TaskCompletionSource<McpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var router = new McpRouter(new()
        {
            MaxConnections = 1,
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["api"] = _ => Interlocked.Increment(ref count) == 1 ? release.Task : Task.FromResult(secondClient) }
        });
        await using var agent = NewAgent();
        using var cancellation = new CancellationTokenSource();
        var firstAttempt = Command(router, agent, new { command = "connect", server_name = "api", connection_id = "first" }, cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAttempt);
        var cleanup = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(error.Data["Strands.CleanupTask"]);
        var secondAttempt = Command(router, agent, new { command = "connect", server_name = "api", connection_id = "second" });
        try { Assert.Equal(1, count); Assert.False(secondAttempt.IsCompleted); }
        finally { release.TrySetResult(firstClient); }
        Assert.Empty(await cleanup);
        await secondAttempt;
        Assert.True(first.Disposed); Assert.False(second.Disposed);
        Assert.Equal("second", (await Command(router, agent, new { command = "list_connections" })).Content);
    }

    [Fact]
    public async Task ALateTransferredClientIsClosedAfterRouterDisposalWithoutReopeningItsAdmissionSlot()
    {
        using var server = new RouterServer();
        var candidate = await server.ConnectAsync(default);
        var release = new TaskCompletionSource<McpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = _ => release.Task } });
        await using var agent = NewAgent();
        using var cancellation = new CancellationTokenSource();
        var connecting = Command(router, agent, new { command = "connect", server_name = "api", connection_id = "late" }, cancellation.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        var cleanup = Assert.IsAssignableFrom<Task<IReadOnlyList<Exception>>>(error.Data["Strands.CleanupTask"]);
        try { await Bounded(router.DisposeAsync().AsTask()); }
        finally { release.TrySetResult(candidate); }

        Assert.Empty(await cleanup);
        Assert.True(server.Disposed);
        Assert.Equal(1, server.Deletes);
    }

    [Fact]
    public async Task FullConnectionSetsRejectAndCloseNewCandidatesWithoutEvictingExistingConnections()
    {
        var servers = new List<RouterServer>();
        await using var router = new McpRouter(new()
        {
            MaxConnections = 1,
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["api"] = token => { var server = new RouterServer(); servers.Add(server); return server.ConnectAsync(token); } }
        });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "first" });
        var error = await Assert.ThrowsAsync<McpRouterToolException>(() => Command(router, agent, new { command = "connect", server_name = "api", connection_id = "second" }));
        Assert.Contains("limit of 1", error.Message); Assert.Contains("first", error.Message);
        Assert.False(servers[0].Disposed); Assert.True(servers[1].Disposed);
        Assert.Equal("first", (await Command(router, agent, new { command = "list_connections" })).Content);
    }

    [Fact]
    public async Task DisposalIsMemoizedAndRejectsFutureCommandsWithoutReconnecting()
    {
        using var server = new RouterServer();
        var calls = 0;
        var router = new McpRouter(new()
        {
            Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>
            { ["api"] = token => { calls++; return server.ConnectAsync(token); } }
        });
        await using var agent = NewAgent();
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "c" });
        var disposing = router.DisposeAsync().AsTask();
        Assert.Same(disposing, router.DisposeAsync().AsTask());
        await disposing;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Command(router, agent, new { command = "connect", server_name = "api", connection_id = "other" }));
        Assert.Equal(1, calls); Assert.Equal(1, server.Deletes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidConnectionCapsAreRejectedAtConstruction(int limit)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new McpRouter(new() { MaxConnections = limit, Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = _ => throw new InvalidOperationException() } }));

    [Fact]
    public void EmptyAllowlistAndInvalidPoliciesAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>>() }));
        Assert.Throws<ArgumentException>(() => new McpRouter(new() { ErrorPolicy = (McpRouterErrorPolicy)99, Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = _ => throw new InvalidOperationException() } }));
    }

    internal static async Task Bounded(Task operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(3))));
    }

    internal static Agent NewAgent() => new(new AgentOptions { Model = new UnusedModel() });
    internal static Task<ToolResultBlock> Command(McpRouter router, Agent agent, object arguments, CancellationToken token = default)
    {
        var input = JsonSerializer.SerializeToElement(arguments);
        return router.InvokeResultAsync(input, new ToolContext(agent, new("router-call", "mcp_router", input), new Dictionary<string, object?>()), token).AsTask();
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    internal sealed class RouterServer : HttpMessageHandler
    {
        internal List<JsonElement> Requests { get; } = [];
        internal int Deletes { get; private set; }
        internal bool Disposed { get; private set; }
        internal Func<JsonElement, CancellationToken, Task<object?>>? HandleAsync { get; init; }
        internal Func<JsonElement, object?>? Handle { get; init; }
        internal TaskCompletionSource<bool> DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task<McpClient> ConnectAsync(CancellationToken token)
        {
            var http = new HttpClient(this);
            try
            {
                return await McpClient.CreateAsync(new McpClientSettings { OwnsTransport = true }, new HttpClientTransport(new()
                {
                    Endpoint = new Uri("https://router.example.test/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false
                }, http, ownsHttpClient: true), new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) }, token);
            }
            catch { http.Dispose(); throw; }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) { Deletes++; return new(HttpStatusCode.NoContent) { Content = new StringContent("") }; }
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement.Clone();
            lock (Requests) Requests.Add(root);
            if (!root.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted) { Content = new StringContent("") };
            var method = root.GetProperty("method").GetString();
            if (method == "server/discover") return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Not supported" } }), Encoding.UTF8, "application/json") };
            object result = Handle?.Invoke(root) ?? (method == "tools/call" && HandleAsync is not null ? await HandleAsync(root, cancellationToken) : null) ?? method switch
            {
                "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "router-fixture", version = "1" } },
                "tools/list" => new { tools = new[] { new { name = "echo", description = "Echo text", inputSchema = new { type = "object" } } } },
                "tools/call" => new { content = new[] { new { type = "text", text = root.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString() } } },
                _ => throw new InvalidOperationException("Unexpected MCP request " + method)
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json") };
            response.Headers.Add("Mcp-Session-Id", "router-session");
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; DisposedSignal.TrySetResult(true); base.Dispose(disposing); }
    }
}
