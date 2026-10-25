using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Mcp;
using Strands.Harness.Testing;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class McpHarnessTests
{
    [Fact]
    public async Task RealHttpServerToolsArePrefixedInvokedAndOwnedByTheAgent()
    {
        await using var server = new McpTestServer();
        var options = Options(server);
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        var tool = Assert.Single(agent.Tools);
        Assert.Equal("docs_echo", tool.Spec.Name);
        var result = await agent.InvokeAsync("echo a value");
        Assert.Equal("done", result.ToString());
        Assert.Equal(1, server.Calls);
        Assert.Equal(0, server.Deletions);
        await agent.DisposeAsync();
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, server.Deletions);
    }

    [Fact]
    public async Task DiscoveryRpcFailurePropagatesEvenWithContinueAndClosesTheSession()
    {
        await using var server = new McpTestServer { FailDiscovery = true };
        await Assert.ThrowsAnyAsync<Exception>(() => Harness.CreateAsync(Options(server) with { BackgroundTasks = null }));
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, server.Deletions);
    }

    [Fact]
    public async Task PluginInitializationFailurePreservesItsErrorAndClosesFactoryConnections()
    {
        await using var server = new McpTestServer();
        var primary = new InvalidOperationException("plugin initialization failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Harness.CreateAsync(Options(server) with { BackgroundTasks = null, Plugins = [new FailingPlugin(primary)] }));
        Assert.Same(primary, actual);
        Assert.Equal(1, server.Initializations);
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollisionsBeforeOrDuringInitializationCloseTransferredConnections(bool pluginCollision)
    {
        await using var server = new McpTestServer();
        var collision = new FunctionTool(new("docs_echo", "Conflicting tool", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("unused"));
        var options = Options(server) with
        {
            Tools = pluginCollision ? [] : new[] { collision },
            Plugins = pluginCollision ? [new ToolPlugin(collision)] : [],
        };
        Exception failure = pluginCollision
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => Harness.CreateAsync(options with { BackgroundTasks = null }))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => Harness.CreateAsync(options with { BackgroundTasks = null }));
        Assert.Contains("docs_echo", failure.Message, StringComparison.Ordinal);
        await server.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, server.Deletions);
    }

    [Fact]
    public async Task FailureInLaterDiscoveryClosesEveryEarlierAcquisitionAndPreservesCleanupErrors()
    {
        await using var first = new McpTestServer();
        await using var failing = new McpTestServer { FailDiscovery = true };
        var model = new ThrowingDisposalModel();
        var options = Options(first) with
        {
            AgentOptions = new() { Model = model, OwnsModel = true },
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["first"] = new() { Url = first.Endpoint.AbsoluteUri },
                ["failing"] = new() { Url = failing.Endpoint.AbsoluteUri },
            }),
        };
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Harness.CreateAsync(options with { BackgroundTasks = null }));
        Assert.Contains("fixture discovery failure", failure.Message, StringComparison.Ordinal);
        Assert.IsNotType<IOException>(failure);
        Assert.Contains(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]), error => error is IOException);
        await Task.WhenAll(first.Deleted.Task, failing.Deleted.Task).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, first.Deletions);
        Assert.Equal(1, failing.Deletions);
        Assert.Equal(1, model.DisposeCalls);
    }

    [Fact]
    public async Task CancellationDuringNegotiationStopsConstructionWithoutStartingLaterServers()
    {
        await using var blocked = new McpTestServer { InitializationGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var later = new McpTestServer();
        using var source = new CancellationTokenSource();
        var options = Options(blocked) with
        {
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["blocked"] = new() { Url = blocked.Endpoint.AbsoluteUri },
                ["later"] = new() { Url = later.Endpoint.AbsoluteUri },
            }),
        };
        var construction = Harness.CreateAsync(options with { BackgroundTasks = null }, source.Token);
        await blocked.InitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => construction.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, later.Initializations);
    }

    [Fact]
    public async Task NestedDelegationNarrowsNamedServersWithoutReconnectingOrClosingBorrowedSessions()
    {
        await using var docs = new McpTestServer();
        await using var excluded = new McpTestServer();
        var step = 0;
        var model = new SequenceModel(request =>
        {
            switch (step++)
            {
                case 0: return Call("subagent", new { task = "child", tools = new[] { "subagent" }, mcp_servers = new[] { "docs" } });
                case 1:
                    Assert.Equal(new[] { "docs_echo", "subagent" }, request.Tools!.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
                    return Call("docs_echo", new { value = "child value" });
                case 2:
                    var delegation = Assert.Single(request.Tools!, tool => tool.Name == "subagent");
                    Assert.Equal(new[] { "docs" }, delegation.InputSchema.GetProperty("properties").GetProperty("mcp_servers").GetProperty("items").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
                    return Call("subagent", new { task = "grandchild", tools = Array.Empty<string>(), mcp_servers = new[] { "docs" } });
                case 3:
                    Assert.Equal("docs_echo", Assert.Single(request.Tools!).Name);
                    return Call("docs_echo", new { value = "grandchild value" });
                default: return Text("done");
            }
        });
        await using var agent = await Harness.CreateAsync(Options(docs) with
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = ["subagent"],
            Subagent = new() { MaxDepth = 3 },
            McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
            {
                ["docs"] = new() { Url = docs.Endpoint.AbsoluteUri },
                ["excluded"] = new() { Url = excluded.Endpoint.AbsoluteUri },
            }),
        });
        Assert.Equal("done", (await agent.InvokeAsync("delegate")).ToString());
        Assert.Equal(7, step);
        Assert.Equal(1, docs.Initializations);
        Assert.Equal(1, excluded.Initializations);
        Assert.Equal(2, docs.Calls);
        Assert.Equal(0, excluded.Calls);
        Assert.Equal(0, docs.Deletions);
        Assert.Equal(0, excluded.Deletions);
        await agent.DisposeAsync();
        await Task.WhenAll(docs.Deleted.Task, excluded.Deleted.Task).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static HarnessOptions Options(McpTestServer server) => new()
    {
        Model = new ToolModel(),
        BuiltinTools = [],
        BuiltinPlugins = [],
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        McpServers = McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
        {
            ["docs"] = new() { Url = server.Endpoint.AbsoluteUri },
        }),
    };
    private sealed class FailingPlugin(Exception primary) : IPlugin
    {
        public string Name => "failing";
        public IReadOnlyList<ITool> GetTools() => [];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => throw primary;
    }
    private sealed class ToolPlugin(ITool tool) : IPlugin
    {
        public string Name => "extra-tool";
        public IReadOnlyList<ITool> GetTools() => [tool];
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => default;
    }
    private sealed class ThrowingDisposalModel : IModel, IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This model must not be invoked.");
        public ValueTask DisposeAsync() { DisposeCalls++; throw new IOException("model cleanup failed"); }
    }
    private sealed class ToolModel : IModel
    {
        private int _turn;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent();
            if (_turn++ == 0)
            {
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("mcp-call", "docs_echo", JsonSerializer.SerializeToElement(new { value = "native value" })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                Assert.Equal("native value", Assert.IsType<ToolResultBlock>(Assert.Single(request.Messages.Last().Content)).Content);
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            await Task.CompletedTask;
        }
    }
    private static ModelStreamEvent[] Call(string name, object input) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call-" + name, name, JsonSerializer.SerializeToElement(input))),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    ];
    private static ModelStreamEvent[] Text(string text) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    ];
    private sealed class SequenceModel(Func<ModelRequest, ModelStreamEvent[]> next) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var item in next(request)) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
}
