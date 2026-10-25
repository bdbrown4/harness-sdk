using System.Text.Json;
using Strands.Agents.Mcp;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolProviderTests
{
    [Fact]
    public async Task ConstructionAndRepeatedAdmissionDoNotConnect()
    {
        var calls = 0;
        var provider = new McpToolProvider(_ => { calls++; throw new IOException("must not connect"); });
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        await provider.AddConsumerAsync(context);
        await provider.RemoveConsumerAsync(context);
        await provider.RemoveConsumerAsync(context);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DistinctConsumersShareOrderedExactCachedToolsAndOnlyTheLastCloses()
    {
        using var server = new McpRouterTests.RouterServer
        {
            Handle = request => request.GetProperty("method").GetString() != "tools/list" ? null :
                request.GetProperty("params").TryGetProperty("cursor", out _) ?
                    new { tools = new[] { Definition("second") } } :
                    new { tools = new[] { Definition("first"), Definition("hidden") }, nextCursor = "next" }
        };
        var calls = 0;
        McpClient? client = null;
        var provider = new McpToolProvider(async token => { calls++; return client = await server.ConnectAsync(token); },
            new() { Discovery = new() { Prefix = "api", ToolFilter = name => name != "hidden" } });
        await using var firstAgent = McpRouterTests.NewAgent();
        await using var secondAgent = McpRouterTests.NewAgent();
        var first = new ToolProviderContext(firstAgent); var second = new ToolProviderContext(secondAgent);
        await provider.AddConsumerAsync(first); await provider.AddConsumerAsync(second);
        var tools = await provider.LoadToolsAsync(first);
        var reused = await provider.LoadToolsAsync(second);
        Assert.Equal(new[] { "api_first", "api_second" }, tools.Select(tool => tool.Spec.Name));
        Assert.Same(tools, reused);
        Assert.All(tools, tool => Assert.IsType<McpTool>(tool));
        Assert.Equal(1, calls);
        await provider.RemoveConsumerAsync(first);
        Assert.True(client!.IsConnected);
        Assert.Equal(0, server.Deletes);
        await provider.RemoveConsumerAsync(second);
        Assert.False(client.IsConnected);
        Assert.Equal(1, server.Deletes);
    }

    [Fact]
    public async Task RepeatedAdmissionUsesAnIdSetAndCanReconnectAfterTheOnlyRemoval()
    {
        var servers = new List<McpRouterTests.RouterServer>();
        var provider = new McpToolProvider(token => { var server = new McpRouterTests.RouterServer(); servers.Add(server); return server.ConnectAsync(token); });
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context); await provider.AddConsumerAsync(context);
        Assert.Single(await provider.LoadToolsAsync(context));
        await provider.RemoveConsumerAsync(context); await provider.RemoveConsumerAsync(context);
        Assert.True(Assert.Single(servers).Disposed);
        await provider.AddConsumerAsync(context);
        Assert.Single(await provider.LoadToolsAsync(context));
        Assert.Equal(2, servers.Count);
        await provider.RemoveConsumerAsync(context);
        Assert.All(servers, server => Assert.True(server.Disposed));
    }

    [Fact]
    public async Task ContinuedConnectionFailureIsStickyOnlyUntilEveryConsumerLeaves()
    {
        var failure = new IOException("server unavailable"); var calls = 0;
        using var server = new McpRouterTests.RouterServer();
        var provider = new McpToolProvider(token => ++calls == 1 ? Task.FromException<McpClient>(failure) : server.ConnectAsync(token), new() { ContinueOnError = true });
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        Assert.Empty(await provider.LoadToolsAsync(context)); Assert.Empty(await provider.LoadToolsAsync(context));
        Assert.Equal(1, calls);
        await provider.RemoveConsumerAsync(context);
        await provider.AddConsumerAsync(context);
        Assert.Single(await provider.LoadToolsAsync(context)); Assert.Equal(2, calls);
        await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task StrictConnectionFailureIsNotStickyAndRetainsItsOriginalException()
    {
        var failure = new IOException("connection failed"); var calls = 0;
        var provider = new McpToolProvider(_ => { calls++; return Task.FromException<McpClient>(failure); });
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => provider.LoadToolsAsync(context).AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => provider.LoadToolsAsync(context).AsTask()));
        Assert.Equal(2, calls);
        await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task DiscoveryFailuresRemainFatalAndRetryOnTheSameConnectedClient()
    {
        var lists = 0; var connections = 0;
        using var server = new McpRouterTests.RouterServer { Handle = request => request.GetProperty("method").GetString() == "tools/list" && ++lists == 1 ? new { wrong = true } : null };
        var provider = new McpToolProvider(token => { connections++; return server.ConnectAsync(token); }, new() { ContinueOnError = true });
        await using var agent = McpRouterTests.NewAgent();
        var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadToolsAsync(context).AsTask());
        Assert.Single(await provider.LoadToolsAsync(context));
        Assert.Equal(1, connections); Assert.Equal(2, lists);
        await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task ACancelledLeaderDoesNotCancelAnotherAdmittedConsumersConnection()
    {
        var entered = Signal(); var release = Signal(); var calls = 0; CancellationToken generation = default;
        using var server = new McpRouterTests.RouterServer();
        var provider = new McpToolProvider(async token => { calls++; generation = token; entered.TrySetResult(true); await release.Task; return await server.ConnectAsync(token); });
        await using var firstAgent = McpRouterTests.NewAgent(); await using var secondAgent = McpRouterTests.NewAgent();
        var first = new ToolProviderContext(firstAgent); var second = new ToolProviderContext(secondAgent);
        await provider.AddConsumerAsync(first); await provider.AddConsumerAsync(second);
        using var caller = new CancellationTokenSource();
        var leader = provider.LoadToolsAsync(first, caller.Token).AsTask();
        await Bounded(entered.Task);
        var follower = provider.LoadToolsAsync(second).AsTask();
        caller.Cancel();
        try
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leader);
            Assert.Equal(caller.Token, failure.CancellationToken);
            await provider.RemoveConsumerAsync(first);
            Assert.False(generation.IsCancellationRequested);
        }
        finally { release.TrySetResult(true); }
        Assert.Single(await follower); Assert.Equal(1, calls);
        await provider.RemoveConsumerAsync(second);
    }

    [Fact]
    public async Task LoadingRequiresAnAdmittedConsumer()
    {
        var provider = new McpToolProvider(_ => throw new IOException("must not connect"));
        await using var agent = McpRouterTests.NewAgent();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadToolsAsync(new(agent)).AsTask());
    }

    [Fact]
    public async Task ContinueOnErrorRetainsTheSpecificExistingOverlongPrefixSkipPolicy()
    {
        using var server = new McpRouterTests.RouterServer
        {
            Handle = request => request.GetProperty("method").GetString() == "tools/list" ?
            new { tools = new[] { Definition(new string('x', 63)), Definition("short") } } : null
        };
        var provider = new McpToolProvider(server.ConnectAsync, new() { ContinueOnError = true, Discovery = new() { Prefix = "api" } });
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        await provider.AddConsumerAsync(context);
        Assert.Equal("api_short", Assert.Single(await provider.LoadToolsAsync(context)).Spec.Name);
        await provider.RemoveConsumerAsync(context);
    }

    [Fact]
    public async Task PrecancelledLifecycleCallsDoNotAdmitRemoveOrConnect()
    {
        var calls = 0; var provider = new McpToolProvider(_ => { calls++; throw new IOException("must not connect"); });
        await using var agent = McpRouterTests.NewAgent(); var context = new ToolProviderContext(agent);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.AddConsumerAsync(context, caller.Token).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.LoadToolsAsync(context).AsTask());
        await provider.AddConsumerAsync(context);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.LoadToolsAsync(context, caller.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RemoveConsumerAsync(context, caller.Token).AsTask());
        await provider.RemoveConsumerAsync(context); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void InvalidDiscoveryBoundsFailBeforeAnyConnectorCallback(int pages, int tools)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new McpToolProvider(_ => throw new IOException("must not connect"),
            new() { Discovery = new() { MaximumPages = pages, MaximumTools = tools } }));

    internal static object Definition(string name) => new { name, inputSchema = new { type = "object" } };
    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Bounded(Task task)
        => Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
}
