using System.Net.Http;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit.Abstractions;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeHttpDrainTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedListenCancellationPhysicallyEndsTheRealHttpBody(bool settingsOverload)
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        var transport = new HttpClientTransportOptions { Endpoint = server.Endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false };
        var options = new McpClientOptions { ProtocolVersion = "2026-07-28" };
        var client = settingsOverload ? await Client.CreateHttpAsync(new(), http, transport, options) : await Client.CreateHttpAsync(http, transport, options);
        Task<McpToolChangeSubscription>? creation = null;
        McpToolChangeSubscription? watch = null;
        Task? closing = null;
        try
        {
            output.WriteLine("CONNECTED " + DateTime.UtcNow.ToString("O"));
            creation = client.WatchToolChangesAsync((snapshot, token) => default).AsTask();
            Assert.Same(server.ListenEntered.Task, await Task.WhenAny(server.ListenEntered.Task, Task.Delay(10_000)));
            output.WriteLine("LISTEN_ENTERED " + DateTime.UtcNow.ToString("O"));
            Assert.Same(creation, await Task.WhenAny(creation, Task.Delay(10_000)));
            watch = await creation;
            output.WriteLine("WATCH_RETURNED " + DateTime.UtcNow.ToString("O"));
            closing = watch.DisposeAsync().AsTask();
            output.WriteLine("DISPOSE_REQUESTED " + DateTime.UtcNow.ToString("O"));

            Assert.Same(closing, await Task.WhenAny(closing, Task.Delay(3_000)));
            await closing;
            Assert.Same(server.ListenClosed.Task, await Task.WhenAny(server.ListenClosed.Task, Task.Delay(3_000)));
            output.WriteLine("PHYSICALLY_DRAINED " + DateTime.UtcNow.ToString("O"));
            Assert.Single(await client.ListToolsAsync());
            using var reusedHttp = await http.GetAsync(server.Endpoint);
        }
        finally
        {
            output.WriteLine("FORCING_SERVER_EOF_FOR_CHARACTERIZATION_CLEANUP " + DateTime.UtcNow.ToString("O"));
            server.EndListen();
            if (watch is null && creation is not null)
                try { watch = await creation; } catch (Exception error) { output.WriteLine("CREATION_CLEANUP: " + error.GetType().Name); }
            if (watch is not null)
                try { await watch.DisposeAsync(); } catch (Exception error) { output.WriteLine("WATCH_CLEANUP: " + error.GetType().Name); }
            await client.DisposeAsync();
        }
        using var borrowedAfterClose = await http.GetAsync(server.Endpoint);
    }
}
