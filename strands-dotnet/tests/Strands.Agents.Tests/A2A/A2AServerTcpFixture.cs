using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

internal sealed class A2AServerTcpFixture : IAsyncDisposable
{
    private const int MaximumHeaders = 32 * 1024, MaximumBody = 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly List<Task> _connections = [];
    private readonly A2AServer _server;
    private readonly Task _accept;
    internal Uri BaseUri { get; }
    internal A2AServerTcpFixture(Func<Uri, A2AServer> create)
    {
        _listener.Start(); BaseUri = new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
        try { _server = create(BaseUri); _accept = AcceptAsync(); }
        catch { _listener.Stop(); _stop.Dispose(); throw; }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(); _clients.TryAdd(client, 0); _connections.Add(ServeAsync(client));
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var input = await ReadRequestAsync(stream);
                using var request = new HttpRequestMessage(new HttpMethod(input.Method), new Uri(BaseUri, input.Path));
                foreach (var header in input.Headers)
                    if (!header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (input.Method == "POST")
                {
                    request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(input.Body));
                    if (input.Headers.TryGetValue("Content-Type", out var mediaType)) request.Content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
                }
                using var response = await _server.HandleAsync(request, _stop.Token);
                var headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + (int)response.StatusCode + " " + response.StatusCode + "\r\nContent-Type: " + response.Content.Headers.ContentType + "\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
                try
                {
                    await stream.WriteAsync(headers, 0, headers.Length, _stop.Token);
                    using var chunks = new ChunkedWriter(stream, _stop.Token);
                    await response.Content.CopyToAsync(chunks); await WriteAsciiAsync(stream, "0\r\n\r\n", _stop.Token);
                }
                catch (Exception error) when (error is IOException or SocketException || error is HttpRequestException { InnerException: IOException { InnerException: SocketException } } || _stop.IsCancellationRequested && error is ObjectDisposedException or OperationCanceledException or HttpRequestException { InnerException: ObjectDisposedException }) { }
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception error) { _errors.Enqueue(error); }
        finally { _clients.TryRemove(client, out _); }
    }
    private async Task<A2ATestServer.Request> ReadRequestAsync(NetworkStream stream)
    {
        var requestLine = (await ReadLineAsync(stream, MaximumHeaders)).Split(' ');
        if (requestLine.Length != 3) throw new InvalidDataException("Malformed HTTP request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var size = 0;
        while (true)
        {
            var line = await ReadLineAsync(stream, MaximumHeaders - size); size += line.Length + 2;
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            if (separator <= 0 || headers.ContainsKey(line.Substring(0, separator))) throw new InvalidDataException("Malformed or duplicate HTTP header.");
            headers.Add(line.Substring(0, separator), line.Substring(separator + 1).Trim());
        }
        using var body = new MemoryStream();
        if (headers.TryGetValue("Transfer-Encoding", out var transfer))
        {
            if (!string.Equals(transfer, "chunked", StringComparison.OrdinalIgnoreCase) || headers.ContainsKey("Content-Length")) throw new InvalidDataException("Unsupported HTTP framing.");
            while (true)
            {
                var line = await ReadLineAsync(stream, 128); var extension = line.IndexOf(';');
                if (!int.TryParse(extension < 0 ? line : line.Substring(0, extension), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var count) || count < 0) throw new InvalidDataException("Invalid chunk length.");
                if (count == 0)
                {
                    var trailers = 0; while ((line = await ReadLineAsync(stream, MaximumHeaders - trailers)).Length > 0) trailers += line.Length + 2;
                    break;
                }
                await CopyBodyAsync(stream, body, count);
                if ((await ReadLineAsync(stream, 2)).Length != 0) throw new InvalidDataException("Invalid chunk terminator.");
            }
        }
        else if (headers.TryGetValue("Content-Length", out var length))
        {
            if (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0) throw new InvalidDataException("Invalid content length.");
            await CopyBodyAsync(stream, body, count);
        }
        return new(requestLine[0], requestLine[1], new UTF8Encoding(false, true).GetString(body.ToArray()), headers);
    }
    private async Task CopyBodyAsync(NetworkStream stream, MemoryStream body, int count)
    {
        if (body.Length + count > MaximumBody) throw new InvalidDataException("HTTP body exceeds the fixture limit.");
        var buffer = new byte[Math.Min(4096, count)];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, count), _stop.Token);
            if (read == 0) throw new EndOfStreamException(); body.Write(buffer, 0, read); count -= read;
        }
    }
    private async Task<string> ReadLineAsync(NetworkStream stream, int maximum)
    {
        if (maximum <= 0) throw new InvalidDataException("HTTP headers exceed the fixture limit.");
        var bytes = new List<byte>(); var single = new byte[1];
        while (bytes.Count <= maximum)
        {
            if (await stream.ReadAsync(single, 0, 1, _stop.Token) == 0) throw new EndOfStreamException();
            if (single[0] == '\n')
            {
                if (bytes.Count == 0 || bytes[bytes.Count - 1] != '\r') throw new InvalidDataException("HTTP requires CRLF.");
                bytes.RemoveAt(bytes.Count - 1); return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(single[0]);
        }
        throw new InvalidDataException("HTTP line exceeds the fixture limit.");
    }
    private static async Task WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken)
    { var bytes = Encoding.ASCII.GetBytes(value); await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken); }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); foreach (var client in _clients.Keys) client.Dispose();
        Exception? primary = null;
        try { await _server.DisposeAsync(); } catch (Exception error) { primary = error; }
        await _accept; await Task.WhenAll(_connections); _stop.Dispose();
        if (primary is not null) _errors.Enqueue(primary);
        if (!_errors.IsEmpty) throw new AggregateException(_errors);
    }
    private sealed class ChunkedWriter(NetworkStream stream, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (count == 0) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            await WriteAsciiAsync(stream, count.ToString("x", CultureInfo.InvariantCulture) + "\r\n", linked.Token);
            await stream.WriteAsync(buffer, offset, count, linked.Token); await WriteAsciiAsync(stream, "\r\n", linked.Token);
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, cancellationToken).GetAwaiter().GetResult();
        public override void Flush() => stream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
