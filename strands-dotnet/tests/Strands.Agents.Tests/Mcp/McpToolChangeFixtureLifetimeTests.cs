using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeFixtureLifetimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PeerHalfCloseJoinsAnAcceptedErrorWriteBeforeDisposingItsStream(bool beforePhysicalWrite, bool queuedWriter)
    {
        var written = NewSignal();
        var release = NewSignal();
        await using var server = new McpToolChangeLoopbackServer
        {
            BeforeErrorWrite = beforePhysicalWrite ? () => { written.TrySetResult(true); return release.Task; } : null,
            AfterErrorWrite = beforePhysicalWrite ? null : () => { written.TrySetResult(true); return release.Task; }
        };
        using var peer = new TcpClient();
        Task? send = null;
        Exception? sendError = null;
        var disposedWhileWriting = false;
        try
        {
            await peer.ConnectAsync(server.Endpoint.Host, server.Endpoint.Port);
            var stream = peer.GetStream();
            var body = "{\"jsonrpc\":\"2.0\",\"id\":\"fixture-listen\",\"method\":\"subscriptions/listen\",\"params\":{\"notifications\":{\"toolsListChanged\":true}}}";
            var bytes = Encoding.UTF8.GetBytes(body);
            var header = Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            Assert.Contains("200", await ReadLineAsync(stream));
            while ((await ReadLineAsync(stream)).Length != 0) { }
            Assert.Equal("event: message", await ReadLineAsync(stream));
            Assert.Contains("notifications/subscriptions/acknowledged", await ReadLineAsync(stream));
            Assert.Equal("", await ReadLineAsync(stream));

            send = server.FailListenAsync("unexpected remote failure");
            await Deadline(written.Task);
            if (queuedWriter) send = Task.WhenAll(send, server.FailListenAsync("accepted queued remote failure"));
            // FIN affects only the server's peer-close reader. Its accepted error write
            // is gated either before physical WriteAsync or between WriteAsync and FlushAsync.
            peer.Client.Shutdown(SocketShutdown.Send);
            await Deadline(Task.WhenAny(server.ListenFinished.Task, server.ListenDrainEntered.Task));
            disposedWhileWriting = server.ListenFinished.Task.IsCompleted;
            Assert.False(send.IsCompleted);
        }
        finally
        {
            release.TrySetResult(true);
            if (send is not null) sendError = await Record.ExceptionAsync(() => Deadline(send));
            peer.Dispose();
            if (send is not null) await Deadline(server.ListenFinished.Task);
        }

        Console.WriteLine("fixture write after peer-close: prematureDisposal=" + disposedWhileWriting + "; sendError=" + (sendError?.GetType().FullName ?? "none"));
        Assert.False(disposedWhileWriting, "The listen handler disposed its stream before the accepted writer physically finished.");
        Assert.Null(sendError);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Deadline(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000)));
        await task;
    }

    private static async Task<T> Deadline<T>(Task<T> task)
    {
        await Deadline((Task)task);
        return await task;
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        using var bytes = new MemoryStream();
        var one = new byte[1];
        while (bytes.Length < 65536)
        {
            if (await Deadline(stream.ReadAsync(one, 0, 1)) == 0) throw new EndOfStreamException();
            if (one[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.WriteByte(one[0]);
        }
        throw new InvalidDataException("Fixture response line exceeded its bound.");
    }
}
