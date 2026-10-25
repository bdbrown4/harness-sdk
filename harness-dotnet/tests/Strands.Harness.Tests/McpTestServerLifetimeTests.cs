using System.Net.Sockets;
using System.Text;
using Strands.Harness.Testing;

namespace Strands.Harness.Tests;

public sealed class McpTestServerLifetimeTests
{
    [Fact]
    public async Task ResetBeforeInitializationResponseIsAnExpectedPeerDisconnect()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new McpTestServer { InitializationGate = gate };
        Exception? requestError = null;
        Exception? disposalError;
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync("127.0.0.1", server.Endpoint.Port);
                var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";
                await WriteAsync(client, "POST /mcp HTTP/1.1\r\nHost: localhost\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                Assert.Same(server.InitializationStarted.Task, await Task.WhenAny(server.InitializationStarted.Task, Task.Delay(TimeSpan.FromSeconds(10))));
                client.Client.LingerState = new LingerOption(true, 0);
            }
            // The full request was consumed before the gate. Read readiness now means FIN/RST reached
            // the server socket; response writing cannot race ahead of the client's deliberate reset.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!server.LastClientReadReady) await Task.Delay(1, timeout.Token);
            gate.TrySetResult(true);
            var requests = server.WaitForRequestsAsync();
            Assert.Same(requests, await Task.WhenAny(requests, Task.Delay(TimeSpan.FromSeconds(10))));
            requestError = await Record.ExceptionAsync(() => requests);
        }
        finally { disposalError = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask()); }

        Assert.Null(requestError);
        Assert.Null(disposalError);
        Assert.Equal(1, server.Initializations);
    }

    [Fact]
    public async Task UnexpectedFixtureIOExceptionIsRetainedDuringRequestAndDisposal()
    {
        var server = new McpTestServer();
        Exception? requestError = null;
        Exception? disposalError;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.Endpoint.Port);
            await WriteAsync(client, "POST /mcp HTTP/1.1\r\nHost: localhost\r\nContent-Length: 2097152\r\n\r\n");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task requests;
            do
            {
                await Task.Delay(1, timeout.Token);
                requests = server.WaitForRequestsAsync();
            } while (!requests.IsFaulted);
            requestError = await Record.ExceptionAsync(() => requests);
        }
        finally { disposalError = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask()); }

        Assert.IsType<InvalidDataException>(requestError);
        Assert.Same(requestError, disposalError);
    }

    private static Task WriteAsync(TcpClient client, string request)
    {
        var bytes = Encoding.UTF8.GetBytes(request);
        return client.GetStream().WriteAsync(bytes, 0, bytes.Length);
    }
}
