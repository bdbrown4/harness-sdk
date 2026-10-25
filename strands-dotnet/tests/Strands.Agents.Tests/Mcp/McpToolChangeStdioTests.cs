using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeStdioTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GenuineStdioPushRefreshesAndDisposalJoinsTheOwnedProcess(bool modern, bool legacyObserver)
    {
        var input = Console.InputEncoding;
        var output = Console.OutputEncoding;
        var options = Options(modern);
        var settings = new McpClientSettings
        { TasksConfig = legacyObserver ? new() { ProtocolProfile = McpTasksProtocolProfile.Legacy20251125 } : null };
        await using var client = await Client.CreateStdioAsync(settings, options,
            new() { ProtocolVersion = modern ? "2026-07-28" : "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(10) });
        using var process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, CultureInfo.InvariantCulture));
        var changed = new TaskCompletionSource<McpToolsChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await client.WatchToolChangesAsync((snapshot, token) => { changed.TrySetResult(snapshot); return default; },
            new() { Prefix = "watched" }, new() { Debounce = TimeSpan.Zero });
        Assert.Equal("watched_echo", Assert.Single(subscription.Current.Tools).Spec.Name);

        var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "real stdio request" }));
        Assert.Single(result.Content);
        Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(10_000)));
        Assert.Equal("watched_updated", Assert.Single((await changed.Task).Tools).Spec.Name);

        await subscription.DisposeAsync();
        await client.DisposeAsync();
        Assert.True(await Task.Run(() => process.WaitForExit(10_000)));
        Assert.Equal(input.CodePage, Console.InputEncoding.CodePage);
        Assert.Equal(input.GetPreamble(), Console.InputEncoding.GetPreamble());
        Assert.Equal(output.CodePage, Console.OutputEncoding.CodePage);
        Assert.Equal(output.GetPreamble(), Console.OutputEncoding.GetPreamble());
    }

    private static StdioClientTransportOptions Options(bool modern)
    {
        var arguments = modern ? new[] { "--tool-changes", "--modern-tool-changes" } : new[] { "--tool-changes" };
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture));
        return new() { Command = fixture, Arguments = arguments, ShutdownTimeout = TimeSpan.FromSeconds(5) };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture));
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new()
        {
            Command = Path.Combine(root.FullName, Path.DirectorySeparatorChar == '\\' ? "dotnet.exe" : "dotnet"),
            Arguments = new[] { fixture }.Concat(arguments).ToArray(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };
#endif
    }
}
