using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using McpClient = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolProviderProtocolTests
{
    [Fact]
    public async Task ARealHttpConnectionRetainsRichToolCapabilitiesAndBorrowedHttpOwnership()
    {
        using var handler = new McpRouterTests.RouterServer
        {
            HandleAsync = (_, _) => Task.FromResult<object?>(new
            {
                content = new object[] { new { type = "text", text = "Café 雪" }, new { type = "image", mimeType = "image/png", data = Convert.ToBase64String(new byte[] { 0, 255, 13, 10 }) } },
                structuredContent = false,
                _meta = new { route = "provider" }
            })
        };
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        var provider = new McpToolProvider(token => McpClient.CreateHttpAsync(http,
            new() { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
            new() { ProtocolVersion = "2025-11-25" }, token));
        var agent = McpToolProviderAgentTests.NewAgent(provider);
        try
        {
            await agent.InitializeAsync(); Assert.IsType<McpTool>(Assert.Single(agent.Tools));
            var result = await agent.InvokeToolAsync(new("rich-call", "echo", JsonSerializer.SerializeToElement(new { text = "input" })));
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal("Café 雪", Assert.IsType<TextBlock>(result.ContentBlocks[0]).Text);
            Assert.Equal(new byte[] { 0, 255, 13, 10 }, Convert.FromBase64String(Assert.IsType<ImageBlock>(result.ContentBlocks[1]).Data!));
            Assert.Equal(JsonValueKind.False, result.StructuredContent!.Value.ValueKind);
            Assert.Equal("provider", result.Metadata!.Value.GetProperty("route").GetString());
        }
        finally { await agent.DisposeAsync(); }
        using var stillBorrowed = await http.GetAsync(server.Endpoint);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, stillBorrowed.StatusCode);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("finalized")]
    [InlineData("legacy")]
    public async Task RealStdioGenerationCallsAndReapsItsOwnedProcess(string profile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-mcp-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        Agent? agent = null;
        McpClient? connected = null;
        try
        {
            var options = StdioOptions(directory, profile == "ordinary" ? Array.Empty<string>() : new[] { profile == "legacy" ? "--legacy-tasks" : "--tasks" });
            var provider = new McpToolProvider(async token => connected = await McpClient.CreateStdioAsync(
                new() { TasksConfig = profile == "ordinary" ? null : new() }, options,
                new() { ProtocolVersion = profile == "finalized" ? "2026-07-28" : "2025-11-25" }, token));
            agent = McpToolProviderAgentTests.NewAgent(provider);
            await agent.InitializeAsync();
            process = Process.GetProcessById(int.Parse(connected!.Protocol.ServerInfo.Version, CultureInfo.InvariantCulture));
            var result = await agent.InvokeToolAsync(new("stdio-call", "echo", JsonSerializer.SerializeToElement(new { value = "Café 雪" })));
            Assert.Equal(ToolResultStatus.Success, result.Status); Assert.Equal("Café 雪", result.Content);
            await agent.DisposeAsync();
            Assert.True(process.HasExited, "Last provider removal must await the native owned stdio process exit.");
            Assert.False(connected.IsConnected);
        }
        finally { if (agent is not null) await agent.DisposeAsync(); process?.Dispose(); Directory.Delete(directory, true); }
    }

    private static StdioClientTransportOptions StdioOptions(string directory, string[] arguments)
    {
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture));
        return new() { Command = fixture, Arguments = arguments, WorkingDirectory = directory, ShutdownTimeout = TimeSpan.FromSeconds(2) };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture));
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, Path.DirectorySeparatorChar == '\\' ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture }.Concat(arguments).ToArray(), WorkingDirectory = directory, ShutdownTimeout = TimeSpan.FromSeconds(2) };
#endif
    }
}
