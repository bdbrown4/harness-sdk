using System.Text.Json;
using Strands.Agents;
using Strands.Harness;
using Strands.Harness.Mcp;
using Strands.Harness.Testing;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class McpHarnessCompatibilityTests
{
    [Fact]
    public async Task RealConfiguredMcpConnectionIsSharedWithChildrenAndDisposedOnlyByItsOwner()
    {
        await using var server = new McpTestServer();
        await using var agent = await Bounded(HarnessFactory.CreateAsync(Options(server) with { BackgroundTasks = null }));
        await using var child = agent.CreateChild(options => options with { Model = new EchoModel() });
        Assert.Equal("done", (await Bounded(child.InvokeAsync("echo"))).ToString());
        await child.DisposeAsync();
        Assert.Equal(0, server.Deletions);
        Assert.Equal("done", (await Bounded(agent.InvokeAsync("echo"))).ToString());
        Assert.Equal(1, server.Initializations);
        Assert.Equal(2, server.Calls);
        await agent.DisposeAsync();
        await Bounded(server.Deleted.Task);
        Assert.Equal(1, server.Deletions);
    }

    [Fact]
    public async Task PortableServerConfigurationExpandsHeadersAndInvokesARealMcpTool()
    {
        await using var server = new McpTestServer();
        var variable = "STRANDS_MCP_COMPAT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "portable-token");
        try
        {
            var json = "{\"builtinTools\":[],\"builtinPlugins\":[],\"caching\":false,\"contextManager\":false,\"skills\":false,\"memory\":false,\"session\":false,\"mcpServers\":{\"docs\":{\"url\":\"" + server.Endpoint.AbsoluteUri + "\",\"headers\":{\"Authorization\":\"Bearer ${env:" + variable + "}\"}}}}";
            var options = HarnessAgentConfig.Parse(json).ToOptions(new() { ModelResolver = _ => new EchoModel() });
            await using var agent = await Bounded(HarnessFactory.CreateAsync(options with { BackgroundTasks = null }));
            Assert.Equal("done", (await Bounded(agent.InvokeAsync("echo"))).ToString());
            Assert.Equal("Bearer portable-token", server.Authorization);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task ContinuedConnectionFailureDoesNotHideSuccessfulServers()
    {
        await using var failing = new McpTestServer { FailInitialization = true };
        await using var server = new McpTestServer();
        var config = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["failing"] = new() { Url = failing.Endpoint.AbsoluteUri },
            ["docs"] = new() { Url = server.Endpoint.AbsoluteUri },
        });
        await using var agent = await Bounded(HarnessFactory.CreateAsync(Options(server) with { BackgroundTasks = null, McpServers = config }));
        Assert.Equal("docs_echo", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(1, failing.Initializations);
        Assert.Equal("done", (await Bounded(agent.InvokeAsync("echo"))).ToString());
    }

    [Fact]
    public async Task NamedServerAuthorityRemainsNarrowedAcrossNestedSubagents()
    {
        await using var docs = new McpTestServer();
        await using var excluded = new McpTestServer();
        var model = new DelegatingModel();
        await using var agent = await Bounded(HarnessFactory.CreateAsync(Options(docs) with
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = new[] { "subagent" },
            Subagent = new() { MaxDepth = 3 },
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["docs"] = new() { Url = docs.Endpoint.AbsoluteUri },
                ["excluded"] = new() { Url = excluded.Endpoint.AbsoluteUri },
            }),
        }));
        Assert.Equal("done", (await Bounded(agent.InvokeAsync("delegate"))).ToString());
        Assert.Equal(5, model.Turns);
        Assert.Equal(1, docs.Initializations);
        Assert.Equal(1, excluded.Initializations);
        Assert.Equal(0, docs.Deletions);
        Assert.Equal(0, excluded.Deletions);
        await agent.DisposeAsync();
        await Bounded(Task.WhenAll(docs.Deleted.Task, excluded.Deleted.Task));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingStdioExecutableHonorsContinuePolicyWithinNativeClientConnectBudget(bool supplyCancellationToken)
    {
        await using var server = new McpTestServer();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var configuration = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["missing"] = new() { Command = Path.Combine(Path.GetTempPath(), "strands-missing-" + Guid.NewGuid().ToString("N")) },
            ["docs"] = new() { Url = server.Endpoint.AbsoluteUri },
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using var agent = await Bounded(HarnessFactory.CreateAsync(Options(server) with { BackgroundTasks = null, McpServers = configuration }, supplyCancellationToken ? cancellation.Token : default), TimeSpan.FromSeconds(80));
        Assert.Equal("docs_echo", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal("done", (await Bounded(agent.InvokeAsync("echo"), TimeSpan.FromSeconds(20))).ToString());
        Console.WriteLine("Missing stdio executable continued after " + watch.Elapsed.TotalSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " seconds.");
    }

    private static HarnessOptions Options(McpTestServer server) => new()
    {
        Model = new EchoModel(),
        BuiltinTools = [],
        BuiltinPlugins = [],
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions> { ["docs"] = new() { Url = server.Endpoint.AbsoluteUri } }),
    };
    private static async Task Bounded(Task operation, TimeSpan? timeout = null)
    {
        if (await Task.WhenAny(operation, Task.Delay(timeout ?? TimeSpan.FromSeconds(20))) != operation) throw new TimeoutException("MCP harness operation exceeded its test deadline.");
        await operation;
    }
    private static async Task<T> Bounded<T>(Task<T> operation, TimeSpan? timeout = null)
    {
        await Bounded((Task)operation, timeout); return await operation;
    }
    private sealed class EchoModel : IModel
    {
        private int _turn;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            if (_turn++ == 0)
            {
                yield return new ModelContentBlockStartEvent(0, new("mcp-call", "docs_echo", JsonSerializer.SerializeToElement(new { value = "portable value" })));
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                Assert.Equal("portable value", Assert.IsType<ToolResultBlock>(Assert.Single(request.Messages.Last().Content)).Content);
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }

    private sealed class DelegatingModel : IModel
    {
        public int Turns { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            var turn = Turns++;
            if (turn < 2)
            {
                if (turn == 1)
                {
                    Assert.Equal(new[] { "docs_echo", "subagent" }, request.Tools!.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
                    var delegation = Assert.Single(request.Tools!, tool => tool.Name == "subagent");
                    Assert.Equal("docs", Assert.Single(delegation.InputSchema.GetProperty("properties").GetProperty("mcp_servers").GetProperty("items").GetProperty("enum").EnumerateArray()).GetString());
                }
                var input = turn == 0
                    ? JsonSerializer.SerializeToElement(new { task = "child", tools = new[] { "subagent" }, mcp_servers = new[] { "docs" } })
                    : JsonSerializer.SerializeToElement(new { task = "grandchild", tools = Array.Empty<string>(), mcp_servers = Array.Empty<string>() });
                yield return new ModelContentBlockStartEvent(0, new("delegate-" + turn, "subagent", input));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                if (turn == 2) Assert.Empty(request.Tools!);
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }
}
