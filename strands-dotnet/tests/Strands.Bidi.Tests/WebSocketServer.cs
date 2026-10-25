using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Strands.Bidi.Tests;

// A real TCP WebSocket peer with RFC6455 masking/framing, independent of the model decoder.
internal sealed class WebSocketServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _clients = [];
    internal WebSocketServer()
    {
        _listener.Start();
        Endpoint = new("ws://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/v1/realtime");
        Connection = AcceptAsync();
    }
    internal Uri Endpoint { get; }
    internal Task<Peer> Connection { get; }
    internal Task<Peer> AcceptNextAsync() => AcceptAsync();
    private async Task<Peer> AcceptAsync()
    {
        var client = await _listener.AcceptTcpClientAsync();
        lock (_clients) _clients.Add(client);
        var stream = client.GetStream();
        var bytes = new List<byte>();
        var next = new byte[1];
        while (!Encoding.ASCII.GetString(bytes.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (bytes.Count > 16384 || await stream.ReadAsync(next, 0, 1) == 0) throw new InvalidDataException("Invalid WebSocket handshake.");
            bytes.Add(next[0]);
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        var headers = lines.Skip(1).ToDictionary(line => line.Substring(0, line.IndexOf(':')), line => line.Substring(line.IndexOf(':') + 1).Trim(), StringComparer.OrdinalIgnoreCase);
        using var sha = SHA1.Create();
        var accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        await stream.WriteAsync(response, 0, response.Length);
        return new(stream, headers, lines[0]);
    }
    internal static async Task<T> Within<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(8))) != task) throw new TimeoutException("WebSocket fixture exceeded its safety deadline.");
        return await task;
    }
    public async ValueTask DisposeAsync()
    {
        lock (_clients) foreach (var client in _clients) client.Dispose();
        _listener.Stop();
        try { await Connection; }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or IOException) { }
    }

    internal sealed class Peer(NetworkStream stream, IReadOnlyDictionary<string, string> headers, string requestLine)
    {
        internal IReadOnlyDictionary<string, string> Headers { get; } = headers;
        internal string RequestLine { get; } = requestLine;
        internal Task<byte[]> ReadFramePrefixAsync() => ReadAsync(2);
        internal async Task<JsonElement> ReceiveAsync()
        {
            using var message = new MemoryStream();
            while (true)
            {
                var prefix = await ReadAsync(2);
                var opcode = prefix[0] & 15;
                if (opcode == 8) throw new EndOfStreamException("Peer closed the WebSocket.");
                var masked = (prefix[1] & 128) != 0;
                if (!masked) throw new InvalidDataException("Client frames must be masked.");
                var length = prefix[1] & 127;
                if (length == 126) { var extended = await ReadAsync(2); length = extended[0] * 256 + extended[1]; }
                else if (length == 127) throw new InvalidDataException("Fixture rejects oversized client frames.");
                var mask = await ReadAsync(4);
                var payload = await ReadAsync(length);
                for (var index = 0; index < payload.Length; index++) payload[index] ^= mask[index % 4];
                message.Write(payload, 0, payload.Length);
                if ((prefix[0] & 128) == 0) continue;
                using var document = JsonDocument.Parse(message.ToArray());
                return document.RootElement.Clone();
            }
        }
        internal async Task SendAsync(string json, int? fragmentAt = null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            if (fragmentAt is { } cut)
            {
                await FrameAsync(bytes.Take(cut).ToArray(), 1, false);
                await FrameAsync(bytes.Skip(cut).ToArray(), 0, true);
            }
            else await FrameAsync(bytes, 1, true);
        }
        internal Task CloseAsync() => FrameAsync(new byte[] { 3, 232 }, 8, true);
        private async Task FrameAsync(byte[] payload, byte opcode, bool final)
        {
            using var frame = new MemoryStream();
            frame.WriteByte((byte)(opcode | (final ? 128 : 0)));
            if (payload.Length < 126) frame.WriteByte((byte)payload.Length);
            else { frame.WriteByte(126); frame.WriteByte((byte)(payload.Length >> 8)); frame.WriteByte((byte)payload.Length); }
            frame.Write(payload, 0, payload.Length);
            var bytes = frame.ToArray(); await stream.WriteAsync(bytes, 0, bytes.Length);
        }
        private async Task<byte[]> ReadAsync(int length)
        {
            var bytes = new byte[length];
            for (var offset = 0; offset < length;)
            {
                var count = await stream.ReadAsync(bytes, offset, length - offset);
                if (count == 0) throw new EndOfStreamException("WebSocket connection ended.");
                offset += count;
            }
            return bytes;
        }
    }
}
