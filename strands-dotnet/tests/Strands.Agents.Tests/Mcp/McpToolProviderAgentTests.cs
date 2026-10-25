using System.Text.Json;
using Strands.Agents.Mcp;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolProviderAgentTests
{
    [Fact]
    public async Task ParentAndChildOwnDistinctConsumerLifetimesForTheSameBorrowedProvider()
    {
        using var server = new McpRouterTests.RouterServer(); var connections = 0;
        var provider = new McpToolProvider(token => { connections++; return server.ConnectAsync(token); });
        var parent = NewAgent(provider);
        Agent? child = null;
        try
        {
            await parent.InitializeAsync(); child = parent.CreateChild(); await child.InitializeAsync();
            Assert.Same(Assert.Single(parent.Tools), Assert.Single(child.Tools)); Assert.Equal(1, connections);
            await parent.DisposeAsync(); Assert.False(server.Disposed);
            var result = await child.InvokeToolAsync(new("child-call", "echo", JsonSerializer.SerializeToElement(new { text = "child remains connected" })));
            Assert.Equal("child remains connected", result.Content);
        }
        finally { await parent.DisposeAsync(); if (child is not null) await child.DisposeAsync(); }
        Assert.True(server.Disposed); Assert.Equal(1, server.Deletes);
    }

    [Fact]
    public async Task RepeatedNonemptyDeclarationsFailRegistrationButReleaseTheIdSetOnce()
    {
        using var server = new McpRouterTests.RouterServer(); var connections = 0;
        var provider = new McpToolProvider(token => { connections++; return server.ConnectAsync(token); });
        await using var agent = NewAgent(provider, provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Empty(agent.Tools); Assert.Equal(1, connections); Assert.Equal(1, server.Deletes); Assert.True(server.Disposed);
    }

    [Fact]
    public async Task APluginFailureAfterProviderPublicationClosesTheConnectionWithoutMaskingItsCause()
    {
        using var server = new McpRouterTests.RouterServer(); var original = new IOException("plugin initialization");
        var provider = new McpToolProvider(server.ConnectAsync);
        await using var agent = new Agent(new() { Model = new UnusedModel(), ToolProviders = [provider], Plugins = [new FailingPlugin(original)] });
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Empty(agent.Tools); Assert.True(server.Disposed); Assert.Equal(1, server.Deletes);
    }

    [Fact]
    public async Task ACancelledAgentReleasesOnlyItsOwnIdWhileAnotherAgentFinishesLoading()
    {
        var entered = McpToolProviderTests.Signal(); var release = McpToolProviderTests.Signal(); CancellationToken lifetime = default;
        using var server = new McpRouterTests.RouterServer();
        var provider = new McpToolProvider(async token => { lifetime = token; entered.TrySetResult(true); await release.Task; return await server.ConnectAsync(token); });
        await using var first = NewAgent(provider); await using var second = NewAgent(provider);
        using var caller = new CancellationTokenSource(); var firstLoading = first.InitializeAsync(caller.Token);
        await McpToolProviderTests.Bounded(entered.Task);
        var secondLoading = second.InitializeAsync(); caller.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstLoading); Assert.Equal(caller.Token, error.CancellationToken);
            Assert.False(lifetime.IsCancellationRequested); Assert.Empty(first.Tools);
        }
        finally { release.TrySetResult(true); }
        await secondLoading; Assert.Single(second.Tools);
    }

    internal static Agent NewAgent(params IToolProvider[] providers) => new(new() { Model = new UnusedModel(), ToolProviders = providers });
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FailingPlugin(Exception error) : IPlugin
    {
        public string Name => "provider-test-failing-plugin";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => new(Task.FromException(error));
        public IReadOnlyList<ITool> GetTools() => [];
    }
}
