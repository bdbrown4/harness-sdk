using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpLegacyTaskLifetimeTests
{
    [Fact]
    public async Task RealLegacyStdioDispatchesPendingElicitationDuringTheResultWait()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var client = await Strands.Agents.Mcp.McpClient.CreateStdioAsync(new McpClientSettings
        { TasksConfig = Configuration() with { RequestTimeout = TimeSpan.FromSeconds(2), PollTimeout = TimeSpan.FromSeconds(15) } }, StdioOptions("--legacy-input"),
            new McpClientOptions
            {
                ProtocolVersion = "2025-11-25",
                InitializationTimeout = TimeSpan.FromSeconds(20),
                Handlers = new()
                {
                    ElicitationHandler = async (_, _) =>
                    {
                        Interlocked.Increment(ref calls);
                        entered.TrySetResult(true);
                        await release.Task.ConfigureAwait(false);
                        return new ElicitResult { Action = "accept" };
                    }
                }
            });
        var invocation = client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "approved legacy work" })).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));
            await Task.Delay(2500);
            Assert.False(invocation.IsCompleted, "Queued server input must not expire at the ordinary inactivity timeout.");
        }
        finally { release.TrySetResult(true); }

        Assert.Equal("approved legacy work", Assert.IsType<TextContentBlock>(Assert.Single((await invocation).Content)).Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RealLegacyStdioPublishesTaskCreationAndDisposalReapsItsProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands legacy task " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "created.pid");
            await using var client = await Strands.Agents.Mcp.McpClient.CreateStdioAsync(new McpClientSettings { TasksConfig = Configuration() }, StdioOptions("--legacy-task-marker=" + marker),
                new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(20) });
            using var process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, System.Globalization.CultureInfo.InvariantCulture));

            var result = await client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "genuine legacy stdio Café 雪" }));

            Assert.Equal("genuine legacy stdio Café 雪", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.True(File.Exists(marker), "The native stdio server must observe a task-augmented call, rather than an ordinary echo.");
            Assert.Equal(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), File.ReadAllText(marker));
            await client.DisposeAsync();
            Assert.True(await Task.Run(() => process.WaitForExit(10000)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancellingRealLegacyStdioReapsTheBlockedOwnedServer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands legacy cancellation " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "blocked.pid");
            await using var client = await Strands.Agents.Mcp.McpClient.CreateStdioAsync(new McpClientSettings { TasksConfig = Configuration() }, StdioOptions("--block-task", "--task-marker=" + marker),
                new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(20) });
            using var process = Process.GetProcessById(int.Parse(client.Protocol.ServerInfo.Version, System.Globalization.CultureInfo.InvariantCulture));
            using var cancellation = new CancellationTokenSource();
            var invocation = client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "pending" }), cancellationToken: cancellation.Token).AsTask();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(marker))
            {
                Assert.False(invocation.IsCompleted, "The real legacy task must enter its blocked poll before cancellation.");
                await Task.Delay(25, startup.Token);
            }

            cancellation.Cancel();

            Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(5000)));
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation)).CancellationToken);
            await client.DisposeAsync();
            Assert.True(await Task.Run(() => process.WaitForExit(10000)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DiscoveryInactivityIsBoundedAndFallsBackWithoutTaskAugmentation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = McpLegacyTaskTests.Server();
        var baseline = server.Respond!;
        server.Respond = async (request, token) =>
        {
            if (request.Method != "tools/list") return await baseline(request, token).ConfigureAwait(false);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return null;
        };
        using var http = new HttpClient(server);
        await using var client = await McpLegacyTaskTests.ConnectAsync(http, config: Configuration() with { RequestTimeout = TimeSpan.FromMilliseconds(300), PollTimeout = TimeSpan.FromSeconds(10) });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(2000)));
        var result = await invocation;

        Assert.Equal("ordinary", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.False(Assert.Single(server.Requests, request => request.Method == "tools/call").Parameters.TryGetProperty("task", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationRetainsCallerTokenAndHonorsTheServerCancelCapability(bool supportsCancellation)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = McpLegacyTaskTests.Server();
        var baseline = server.Respond!;
        server.Respond = async (request, token) =>
        {
            if (request.Method == "initialize" && !supportsCancellation)
                return McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
                { protocolVersion = "2025-11-25", serverInfo = new { name = "legacy", version = "1" }, capabilities = new { tools = new { }, tasks = new { requests = new { tools = new { call = new { } } } } } });
            if (request.Method != "tasks/get") return await baseline(request, token).ConfigureAwait(false);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return null;
        };
        using var http = new HttpClient(server);
        await using var client = await McpLegacyTaskTests.ConnectAsync(http);
        using var cancellation = new CancellationTokenSource();
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { }), cancellationToken: cancellation.Token).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        cancellation.Cancel();

        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(5000)));
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation)).CancellationToken);
        Assert.Equal(supportsCancellation ? 1 : 0, server.Requests.Count(request => request.Method == "tasks/cancel"));
    }

    [Fact]
    public async Task InputRequiredResultWaitUsesTheOverallBudgetInsteadOfRequestInactivity()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = McpLegacyTaskTests.Server();
        var baseline = server.Respond!;
        server.Respond = async (request, token) =>
        {
            if (request.Method == "tasks/get") return McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), McpLegacyTaskTests.State(server.TaskId, "input_required"));
            if (request.Method != "tasks/result") return await baseline(request, token).ConfigureAwait(false);
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return await baseline(request, token).ConfigureAwait(false);
        };
        using var http = new HttpClient(server);
        await using var client = await McpLegacyTaskTests.ConnectAsync(http, config: Configuration() with { RequestTimeout = TimeSpan.FromSeconds(1), PollTimeout = TimeSpan.FromSeconds(10) });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));
            await Task.Delay(1500);
            Assert.False(invocation.IsCompleted, "A pending legacy input response must survive the ordinary request-inactivity budget.");
        }
        finally { release.TrySetResult(true); }
        Assert.Equal("legacy result Café 雪", Assert.IsType<TextContentBlock>(Assert.Single((await invocation).Content)).Text);
    }

    [Fact]
    public async Task InputRequiredWaitStillHonorsTheOverallDeadlineAndCancelsTheTask()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = McpLegacyTaskTests.Server();
        var baseline = server.Respond!;
        server.Respond = async (request, token) =>
        {
            if (request.Method == "tasks/get") return McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), McpLegacyTaskTests.State(server.TaskId, "input_required"));
            if (request.Method != "tasks/result") return await baseline(request, token).ConfigureAwait(false);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return null;
        };
        using var http = new HttpClient(server);
        await using var client = await McpLegacyTaskTests.ConnectAsync(http, config: Configuration() with { RequestTimeout = TimeSpan.FromSeconds(1), PollTimeout = TimeSpan.FromSeconds(3) });
        var invocation = client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask();
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(5000)));
        Assert.Contains("overall", (await Assert.ThrowsAsync<TimeoutException>(() => invocation)).Message);
        Assert.Single(server.Requests, request => request.Method == "tasks/cancel");
    }

    private static McpTasksConfig Configuration() => new() { ProtocolProfile = McpTasksProtocolProfile.Legacy20251125, PollInterval = TimeSpan.FromMilliseconds(10) };

    private static StdioClientTransportOptions StdioOptions(params string[] arguments)
    {
#if NETFRAMEWORK
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.exe");
        Assert.True(File.Exists(fixture));
        return new() { Command = fixture, Arguments = new[] { "--legacy-tasks" }.Concat(arguments).ToArray() };
#else
        var fixture = Path.Combine(AppContext.BaseDirectory, "McpStdioFixture", "Strands.Mcp.StdioServer.dll");
        Assert.True(File.Exists(fixture));
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!).Parent!.Parent!.Parent!;
        return new() { Command = Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), Arguments = new[] { fixture, "--legacy-tasks" }.Concat(arguments).ToArray() };
#endif
    }
}
