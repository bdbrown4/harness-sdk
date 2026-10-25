using System.Net.Http;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeTransportTests
{
    [Fact]
    public async Task AnUnrelatedTransportFailureDuringOwnCloseIsRetained()
    {
        await using var server = new McpToolChangeLoopbackServer();
        using var http = new HttpClient();
        // A custom IClientTransport owns its HTTP-read cancellation policy. Give this injected
        // failure fixture the same cooperative body boundary as the native Framework factory.
        using var transportHttp = new Client.HttpClientBridge(http);
        var primary = new McpException("a different transport failure");
        var transport = new CloseFailureTransport(new HttpClientTransport(new()
        {
            Endpoint = server.Endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        }, transportHttp, ownsHttpClient: false), primary);
        await using var client = await Client.CreateAsync(transport, new() { ProtocolVersion = "2026-07-28" });
        var subscription = await client.WatchToolChangesAsync((snapshot, token) => default);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => subscription.DisposeAsync().AsTask());

        Assert.Same(primary, failure);
        Assert.Same(primary, await Assert.ThrowsAnyAsync<Exception>(() => subscription.Completion));
        Assert.True(client.IsConnected);
        Assert.Equal(0, transport.Disposals);
        await client.DisposeAsync();
        Assert.Equal(1, transport.Disposals);
    }

    private sealed class CloseFailureTransport(IClientTransport inner, Exception primary) : IClientTransport
    {
        internal int Disposals;
        public string Name => inner.Name;
        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            new Connection(this, await inner.ConnectAsync(cancellationToken));

        private sealed class Connection(CloseFailureTransport owner, ITransport connection) : ITransport
        {
            public string? SessionId => connection.SessionId;
            public ChannelReader<JsonRpcMessage> MessageReader => connection.MessageReader;
            public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
            {
                try { await connection.SendMessageAsync(message, cancellationToken); }
                catch (Exception) when (message is JsonRpcRequest { Method: "subscriptions/listen" } && cancellationToken.IsCancellationRequested)
                { throw owner.Failure; }
            }
            public async ValueTask DisposeAsync()
            { Interlocked.Increment(ref owner.Disposals); await connection.DisposeAsync(); }
        }

        private Exception Failure => primary;
    }
}
