using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents;
using Strands.Agents.Mcp;
using Strands.Harness.Mcp;
using Strands.Harness.Testing;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Harness.Tests;

public sealed class McpProviderHarnessTests
{
    [Fact]
    public async Task DeclarativeConnectionsRemainAliveUntilTheLastRealChildConsumerLeaves()
    {
        await using var server = new McpTestServer();
        Agent? parent = null;
        Agent? child = null;
        try
        {
            parent = await Harness.CreateAsync(Options() with { McpServers = Servers(server) });
            child = parent.CreateChild();
            await child.InitializeAsync();
            Assert.Same(Assert.Single(parent.Tools), Assert.Single(child.Tools));
            Assert.Equal(1, server.Initializations);
            await parent.DisposeAsync();
            Assert.Equal(0, server.Deletions);
            var result = await child.InvokeToolAsync(new("child-call", "docs_echo", JsonSerializer.SerializeToElement(new { value = "Café 雪" })));
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal("Café 雪", result.Content);
            await child.DisposeAsync();
            Assert.Equal(1, server.Deletions);
        }
        finally { if (parent is not null) await parent.DisposeAsync(); if (child is not null) await child.DisposeAsync(); }
    }

    [Fact]
    public async Task ACallerNamedMcpProviderIsOfferedThroughTheDefaultServerAxis()
    {
        await using var server = new McpTestServer();
        using var http = new HttpClient();
        var provider = Provider(server, http, "application", "app");
        await using var agent = await Harness.CreateAsync(Options() with
        {
            BuiltinTools = ["subagent"],
            AgentOptions = new() { Model = new UnusedModel(), ToolProviders = [provider] }
        });
        var tool = Assert.Single(agent.Tools, value => value.Spec.Name == "subagent");
        var properties = tool.Spec.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("mcp_servers", out var servers));
        Assert.Equal(new[] { "application" }, servers.GetProperty("items").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("app_echo", properties.GetProperty("tools").GetProperty("items").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
    }

    internal static McpToolProvider Provider(McpTestServer server, HttpClient http, string? name, string prefix) => new(
        token => Client.CreateHttpAsync(http, new() { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new() { ProtocolVersion = "2025-11-25" }, token), new() { Name = name, Discovery = new() { Prefix = prefix } });
    internal static McpServerConfiguration Servers(McpTestServer server) => McpServerConfiguration.FromServers(new Dictionary<string, McpServerOptions>
    { ["docs"] = new() { Url = server.Endpoint.AbsoluteUri } }, continueOnError: false);
    internal static HarnessOptions Options() => new()
    {
        Model = new UnusedModel(),
        BuiltinTools = [],
        BuiltinPlugins = [],
        BackgroundTasks = null,
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        UseEnvironmentTelemetry = false,
    };
    internal sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
