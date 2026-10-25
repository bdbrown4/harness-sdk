using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpTransportTests
{
    [Fact]
    public async Task StdioDiscoveryInvocationAndDisposalOwnTheChildProcess()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture), "The build-only stdio fixture reference must be built before this test.");
        var dotnetRoot = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        var dotnet = Path.Combine(dotnetRoot.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        await using var client = await Client.CreateStdioAsync(new StdioClientTransportOptions
        {
            Command = dotnet,
            Arguments = [fixture],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }, new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(10) });
        using var process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, System.Globalization.CultureInfo.InvariantCulture));

        var tool = Assert.Single(await client.ListToolsAsync());
        var result = await client.CallToolAsync(tool.ServerName, JsonSerializer.SerializeToElement(new { value = "Café ☕" }));

        Assert.Equal("Café ☕", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text);
        await client.DisposeAsync();
        Assert.False(client.IsConnected);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellationInterruptsAnInFlightRequestWithoutOwningHttpClient()
    {
        using var server = new McpClientTests.FixtureServer();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RespondAsync = async (root, cancellationToken) =>
        {
            if (root.GetProperty("method").GetString() == "tools/call")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return null;
        };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);
        using var cancellation = new CancellationTokenSource();
        var call = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), cancellationToken: cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(client.IsConnected);
        Assert.Single(await client.ListToolsAsync());
        var firstCleanup = client.DisposeAsync().AsTask();
        Assert.Same(firstCleanup, client.DisposeAsync().AsTask());
        await firstCleanup;
        Assert.Equal(1, server.SessionDeletes);
        Assert.False(server.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListToolsAsync());
    }
}
