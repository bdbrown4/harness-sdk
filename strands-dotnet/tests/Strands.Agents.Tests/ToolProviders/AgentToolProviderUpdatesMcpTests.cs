using System.Net.Http;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Strands.Agents.Tests.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesMcpTests
{
    [Fact]
    public async Task AgentPublicationOptInAloneDoesNotEnableMcpWireNotifications()
    {
        await using var server = new McpToolChangeLoopbackServer(); using var http = new HttpClient();
        var provider = new McpToolProvider(token => Client.CreateHttpAsync(http,
            new() { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new() { ProtocolVersion = "2026-07-28" }, token));
        await using var agent = new Agent(new() { Model = new AgentToolProviderUpdatesTests.TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await agent.InitializeAsync(); Assert.Equal("first", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(0, server.ListenCount); Assert.Equal(1, server.ListCount);
        server.ToolName = "ignored";
        await agent.InvokeAsync("run"); Assert.Equal("first", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(0, server.ListenCount); Assert.Equal(1, server.ListCount);
    }

    [Fact]
    public async Task OneAcceptedMcpCacheFansOutToInitializedAgentsEvenIfTheApplicationObserverFails()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new McpToolProvider(token => Client.CreateHttpAsync(http,
            new() { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new() { ProtocolVersion = "2026-07-28" }, token), new()
            {
                ToolChanges = new() { Debounce = TimeSpan.Zero },
                OnToolsChanged = (_, _) => { changed.TrySetResult(true); throw new IOException("application observer"); },
            });
        await using var first = new Agent(new() { Model = new AgentToolProviderUpdatesTests.TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await using var second = new Agent(new() { Model = new AgentToolProviderUpdatesTests.TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await first.InitializeAsync(); await second.InitializeAsync();
        Assert.Equal("first", Assert.Single(first.Tools).Spec.Name);
        Assert.Equal(1, server.ListenCount);
        server.ToolName = "updated";
        await server.NotifyAsync();
        Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(10_000)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (first.Tools.Single().Spec.Name != "updated" || second.Tools.Single().Spec.Name != "updated")
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        Assert.Equal(1, server.ListenCount);
    }
}
