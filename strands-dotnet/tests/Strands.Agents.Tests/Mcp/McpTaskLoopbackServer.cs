using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Strands.Agents.Tests.Mcp;

internal sealed class McpTaskLoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpMessageInvoker _dispatch;
    private readonly List<TcpClient> _clients = [];
    private readonly List<Task> _requests = [];
    private readonly Task _accept;
    internal McpTaskLoopbackServer(HttpMessageHandler handler)
    {
        _dispatch = new(handler, disposeHandler: false);
        _listener.Start();
        Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/mcp");
        _accept = AcceptAsync();
    }
    internal Uri Endpoint { get; }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception error) when (_shutdown.IsCancellationRequested && error is SocketException or ObjectDisposedException) { return; }
            lock (_clients) { _clients.Add(client); _requests.Add(ServeAsync(client)); }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var header = new List<byte>();
                while (header.Count < 65536)
                {
                    var next = await ReadExactlyAsync(stream, 1).ConfigureAwait(false);
                    header.Add(next[0]);
                    if (header.Count >= 4 && header[header.Count - 4] == 13 && header[header.Count - 3] == 10 && header[header.Count - 2] == 13 && header[header.Count - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                using var request = new HttpRequestMessage(new HttpMethod(lines[0].Split(' ')[0]), Endpoint);
                var length = 0;
                var chunked = false;
                foreach (var line in lines.Skip(1).Where(value => value.Contains(':')))
                {
                    var index = line.IndexOf(':');
                    var name = line.Substring(0, index);
                    var value = line.Substring(index + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = int.Parse(value, CultureInfo.InvariantCulture);
                    else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) chunked = value.Equals("chunked", StringComparison.OrdinalIgnoreCase);
                    else request.Headers.TryAddWithoutValidation(name, value);
                }
                var body = chunked ? await ReadChunkedAsync(stream).ConfigureAwait(false) : await ReadExactlyAsync(stream, length).ConfigureAwait(false);
                request.Content = new ByteArrayContent(body);
                using var response = request.Method == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                    : await _dispatch.SendAsync(request, _shutdown.Token).ConfigureAwait(false);
                var responseBody = response.Content is null ? Array.Empty<byte>() : await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 " + (int)response.StatusCode + " Fixture\r\nContent-Type: application/json\r\nContent-Length: " + responseBody.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, 0, responseHeader.Length, _shutdown.Token).ConfigureAwait(false);
                await stream.WriteAsync(responseBody, 0, responseBody.Length, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.Shutdown) { }
        catch (Exception error) when (_shutdown.IsCancellationRequested && error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
    {
        if (length < 0 || length > 1024 * 1024) throw new InvalidDataException("Fixture request exceeds its byte bound.");
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, _shutdown.Token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }

    private async Task<byte[]> ReadChunkedAsync(NetworkStream stream)
    {
        using var output = new MemoryStream();
        while (true)
        {
            var size = int.Parse((await ReadLineAsync(stream).ConfigureAwait(false)).Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (size == 0) { while ((await ReadLineAsync(stream).ConfigureAwait(false)).Length != 0) { } return output.ToArray(); }
            if (output.Length + size > 1024 * 1024) throw new InvalidDataException("Fixture request exceeds its byte bound.");
            var bytes = await ReadExactlyAsync(stream, size).ConfigureAwait(false);
            output.Write(bytes, 0, bytes.Length);
            if ((await ReadLineAsync(stream).ConfigureAwait(false)).Length != 0) throw new InvalidDataException("Malformed fixture chunk terminator.");
        }
    }

    private async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 65536)
        {
            bytes.Add((await ReadExactlyAsync(stream, 1).ConfigureAwait(false))[0]);
            if (bytes.Count >= 2 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10) return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
        }
        throw new InvalidDataException("Fixture HTTP line exceeds its byte bound.");
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        TcpClient[] clients;
        Task[] requests;
        lock (_clients) { clients = _clients.ToArray(); requests = _requests.ToArray(); }
        foreach (var client in clients) client.Dispose();
        try { await _accept.ConfigureAwait(false); await Task.WhenAll(requests).ConfigureAwait(false); }
        finally { _dispatch.Dispose(); _shutdown.Dispose(); }
    }
}
