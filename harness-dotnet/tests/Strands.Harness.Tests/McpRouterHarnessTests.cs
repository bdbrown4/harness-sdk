using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Mcp;
using Strands.Agents.Tests.Mcp;
using Strands.Harness.Mcp;
using Strands.Harness.Testing;

namespace Strands.Harness.Tests;

public sealed class McpRouterHarnessTests
{
    [Fact]
    public async Task DeclarativeRouterConnectsLazilyAndUsesRealHttpWithoutRegisteringRemoteTools()
    {
        await using var server = new McpTestServer();
        var configuration = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["api"] = new() { Url = server.Endpoint.AbsoluteUri, Headers = new Dictionary<string, string> { ["x-api-key"] = "fixed" }, Prefix = "unused" }
        }, continueOnError: false);
        await using var router = await configuration.CreateRouterAsync(new LocalFileSandbox());
        await using var agent = new Agent(new() { Model = new UnusedModel(), Tools = [router] });
        Assert.Equal(0, server.Initializations);

        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "chosen" });
        var listing = await Command(router, agent, new { command = "list_tools", connection_id = "chosen" });
        Assert.Equal("echo", Assert.IsType<JsonBlock>(Assert.Single(listing.ContentBlocks)).Json[0].GetProperty("name").GetString());
        var result = await Command(router, agent, new { command = "call_tool", connection_id = "chosen", tool_name = "echo", arguments = new { value = "Café 雪" } });

        Assert.Equal("Café 雪", result.Content);
        Assert.Equal("fixed", server.LastCallApiKey);
        Assert.Equal(1, server.Initializations);
        Assert.Same(router, Assert.Single(agent.Tools));
        await agent.ShutdownAsync();
        Assert.Equal(1, server.Deletions);
    }

    [Fact]
    public async Task RouterRunsAsTheOnlyRegisteredToolThroughTheNativeAgentLoop()
    {
        await using var server = new McpTestServer();
        await using var router = await McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        { ["api"] = new() { Url = server.Endpoint.AbsoluteUri } }, continueOnError: false).CreateRouterAsync(new LocalFileSandbox());
        var model = new RouterModel();
        await using var agent = new Agent(new() { Model = model, Tools = [router] });

        var result = await agent.InvokeAsync("Use the allowlisted API");

        Assert.Equal("complete", result.Message!.Text);
        Assert.Equal(6, result.Turns);
        Assert.Equal(1, server.Calls);
        Assert.Equal(1, server.Deletions);
        Assert.All(model.ToolNames, value => Assert.Equal("mcp_router", value));
        Assert.Equal(5, agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Count());
    }

    [Fact]
    public async Task SelectedSandboxSuppliesTheFileAndDisabledNamesDoNotAcquireAConnection()
    {
        await using var server = new McpTestServer();
        var root = Path.Combine(Path.GetTempPath(), "strands virtual router " + Guid.NewGuid().ToString("N"));
        var sandbox = new ConfigSandbox(JsonSerializer.Serialize(new
        { mcpServers = new Dictionary<string, object> { ["disabled"] = new { url = server.Endpoint.AbsoluteUri, disabled = true }, ["api"] = new { url = server.Endpoint.AbsoluteUri } } }));
        await using var router = await McpServerConfiguration.FromFile("servers.json", root).CreateRouterAsync(sandbox);
        await using var agent = new Agent(new() { Model = new UnusedModel() });
        Assert.Equal(Path.Combine(root, "servers.json"), Assert.Single(sandbox.Reads));
        Assert.Contains("'disabled'", router.Spec.Description);
        await Assert.ThrowsAsync<McpRouterToolException>(() => Command(router, agent, new { command = "connect", server_name = "disabled", connection_id = "d" }));
        Assert.Equal(0, server.Initializations);
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "a" });
        Assert.Equal(1, server.Initializations);
    }

    [Fact]
    public async Task DeclarativeDiscoveryFilterAndCustomRouterSettingsDoNotBroadenTheAllowlist()
    {
        await using var server = new McpTestServer();
        await using var router = await McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        { ["api"] = new() { Url = server.Endpoint.AbsoluteUri, ToolFilters = new() { Allowed = [] } } }, continueOnError: false)
            .CreateRouterAsync(new LocalFileSandbox(), name: "selected_router", description: "Application router", maxConnections: 1, errorPolicy: McpRouterErrorPolicy.ReturnResult);
        await using var agent = new Agent(new() { Model = new UnusedModel() });
        Assert.Equal("selected_router", router.Spec.Name); Assert.Equal("Application router", router.Spec.Description);
        await Command(router, agent, new { command = "connect", server_name = "api", connection_id = "a" });
        Assert.Equal(0, Assert.IsType<JsonBlock>(Assert.Single((await Command(router, agent, new { command = "list_tools", connection_id = "a" })).ContentBlocks)).Json.GetArrayLength());
        Assert.Equal("direct", (await Command(router, agent, new { command = "call_tool", connection_id = "a", tool_name = "echo", arguments = new { value = "direct" } })).Content);
        await Assert.ThrowsAsync<McpRouterToolException>(() => Command(router, agent, new { command = "connect", server_name = "not configured", connection_id = "other" }));
        Assert.Equal(1, server.Initializations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(McpTasksProtocolProfile.Legacy20251125)]
    [InlineData(McpTasksProtocolProfile.Finalized20260728)]
    [InlineData(McpTasksProtocolProfile.Negotiated)]
    public async Task RealStdioRouterPreservesArgumentsEnvironmentWorkingDirectoryAndTaskProfiles(McpTasksProtocolProfile? profile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands router stdio " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var variable = "STRANDS_ROUTER_" + Guid.NewGuid().ToString("N");
        var marker = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(directory, "sentinel"), marker);
        Process? process = null;
        try
        {
            var flags = profile is null ? Array.Empty<string>() : new[] { profile == McpTasksProtocolProfile.Legacy20251125 ? "--legacy-tasks" : "--tasks" };
            var arguments = flags.Concat(new[] { "argument with spaces", "Café 雪" }).ToArray();
            var configuration = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            { ["stdio"] = Stdio(arguments) with { WorkingDirectory = directory, Environment = new Dictionary<string, string> { [variable] = "configured Café" }, TasksConfig = profile is { } selected ? new() { ProtocolProfile = selected, PollTimeout = TimeSpan.FromSeconds(15) } : null } }, continueOnError: false);
            await using var router = await configuration.CreateRouterAsync(new LocalFileSandbox());
            await using var agent = new Agent(new() { Model = new UnusedModel() });
            await Deadline(Command(router, agent, new { command = "connect", server_name = "stdio", connection_id = "native" }));
            await Deadline(Command(router, agent, new { command = "list_tools", connection_id = "native" }));
            var result = await Deadline(Command(router, agent, new { command = "call_tool", connection_id = "native", tool_name = "echo", arguments = new { inspectRuntime = true, environment = variable, inherited = "STRANDS_ROUTER_NOT_INHERITED", sentinel = "sentinel" } }));
            using var document = JsonDocument.Parse(result.Content);
            var runtime = document.RootElement;
            process = Process.GetProcessById(runtime.GetProperty("processId").GetInt32());
            Assert.Equal("configured Café", runtime.GetProperty("environment").GetString());
            Assert.Equal(marker, runtime.GetProperty("sentinel").GetString());
            Assert.Equal(marker, File.ReadAllText(Path.Combine(runtime.GetProperty("workingDirectory").GetString()!, "sentinel")));
            Assert.Equal(arguments, runtime.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal("task result", (await Deadline(Command(router, agent, new { command = "call_tool", connection_id = "native", tool_name = "echo", arguments = new { value = "task result" } }))).Content);
            await Deadline(Command(router, agent, new { command = "disconnect", connection_id = "native" }));
            Assert.True(await Task.Run(() => process.WaitForExit(10000)), "Disconnect must reap the actual owned fixture process.");
        }
        finally
        {
            if (process is not null) { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(10000); } } finally { process.Dispose(); } }
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LazyHttpConnectionsUseDeclarativeClientCredentialsAndReleaseTheirAuthenticatedSession()
    {
        await using var server = new OAuthLoopbackServer();
        server.Respond = request => Task.FromResult(RpcResponse(request));
        await using var router = await McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        { ["secured"] = new() { Url = server.Resource.AbsoluteUri, Auth = new() { ClientId = "client", ClientSecret = "secret" } } }, continueOnError: false).CreateRouterAsync(new LocalFileSandbox());
        await using var agent = new Agent(new() { Model = new UnusedModel() });
        Assert.Empty(server.Requests);
        await Command(router, agent, new { command = "connect", server_name = "secured", connection_id = "auth" });
        Assert.Equal("authenticated", (await Command(router, agent, new { command = "call_tool", connection_id = "auth", tool_name = "echo", arguments = new { value = "authenticated" } })).Content);
        await Command(router, agent, new { command = "disconnect", connection_id = "auth" });
        Assert.Single(server.Requests, request => request.Path == "/token");
        Assert.Equal("Bearer access-1", Assert.Single(server.Requests, request => request.Method == "DELETE").Headers["Authorization"]);
    }

    private static McpServerOptions Stdio(string[] arguments)
    {
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture));
        return new() { Command = fixture, Arguments = arguments };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture));
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, Path.DirectorySeparatorChar == '\\' ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture }.Concat(arguments).ToArray() };
#endif
    }
    private static async Task<T> Deadline<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(25))));
        return await task;
    }
    private sealed class ConfigSandbox(string json) : ISandbox
    {
        internal List<string> Reads { get; } = [];
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { Reads.Add(path); return new(json); }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class RouterModel : IModel
    {
        private int _turn;
        internal List<string> ToolNames { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ToolNames.Add(Assert.Single(request.Tools!).Name);
            var turn = _turn++;
            yield return new ModelMessageStartEvent();
            object? input = turn switch
            {
                0 => new { command = "list_connections" },
                1 => new { command = "connect", server_name = "api", connection_id = "c" },
                2 => new { command = "list_tools", connection_id = "c" },
                3 => new { command = "call_tool", connection_id = "c", tool_name = "echo", arguments = new { value = "from model" } },
                4 => new { command = "disconnect", connection_id = "c" },
                _ => null
            };
            if (input is not null) yield return new ModelContentBlockStartEvent(0, new("call-" + turn, "mcp_router", JsonSerializer.SerializeToElement(input)));
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "complete"); }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(input is null ? StopReasons.EndTurn : StopReasons.ToolUse);
            await Task.CompletedTask;
        }
    }
    private static OAuthResponse? RpcResponse(OAuthRequest request)
    {
        if (request.Path != "/mcp" || !request.Headers.TryGetValue("Authorization", out var authorization) || !authorization.StartsWith("Bearer access-", StringComparison.Ordinal)) return null;
        if (request.Method == "DELETE") return new(204, "");
        if (request.Method != "POST") return new(405, "");
        using var document = JsonDocument.Parse(request.Body);
        var rpc = document.RootElement;
        if (!rpc.TryGetProperty("id", out var id)) return new(202, "");
        if (rpc.GetProperty("method").GetString() == "server/discover") return OAuthLoopbackServer.Json(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } });
        object result = rpc.GetProperty("method").GetString() switch
        {
            "initialize" => new { protocolVersion = "2025-11-25", serverInfo = new { name = "oauth-router", version = "1" }, capabilities = new { tools = new { } } },
            "tools/call" => new { content = new[] { new { type = "text", text = rpc.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString() } } },
            _ => throw new InvalidDataException("Unexpected fixture RPC method.")
        };
        return new(200, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), new Dictionary<string, string> { ["Mcp-Session-Id"] = "router-auth" });
    }

    internal static Task<ToolResultBlock> Command(McpRouter router, Agent agent, object value, CancellationToken token = default)
    {
        var input = JsonSerializer.SerializeToElement(value);
        return router.InvokeResultAsync(input, new(agent, new("router", router.Spec.Name, input), new Dictionary<string, object?>()), token).AsTask();
    }
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
