using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Strands.Agents.Tests.A2A;

internal sealed class A2ATestServer : IAsyncDisposable
{
    internal sealed record Request(string Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers);
    internal sealed record Response(string Body, string ContentType = "application/json", bool Chunked = false, int FragmentSize = 3, Task? WaitBeforeBody = null);
    private const int MaximumHeaders = 32 * 1024, MaximumBody = 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<Request, Response> _respond;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentQueue<Exception> _errors = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accept;
    internal ConcurrentQueue<Request> Requests { get; } = new();
    internal TaskCompletionSource<bool> HeadersSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> RpcHeadersSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Uri BaseUri { get; }

    internal A2ATestServer(Func<Request, Response> respond)
    {
        _respond = respond; _listener.Start();
        BaseUri = new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _clients.TryAdd(client, 0);
                _connections.Add(ServeAsync(client));
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
                var request = await ReadRequestAsync(stream);
                Requests.Enqueue(request);
                var response = _respond(request);
                var body = Encoding.UTF8.GetBytes(response.Body);
                var framing = response.Chunked ? "Transfer-Encoding: chunked" : "Content-Length: " + body.Length;
                var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + response.ContentType + "; charset=utf-8\r\n" + framing + "\r\nConnection: close\r\n\r\n");
                try
                {
                    await stream.WriteAsync(headers, 0, headers.Length, _stop.Token);
                    HeadersSent.TrySetResult(true);
                    if (request.Method == "POST") RpcHeadersSent.TrySetResult(true);
                    if (response.WaitBeforeBody is { } wait)
                    {
                        var stopped = Task.Delay(Timeout.Infinite, _stop.Token);
                        if (await Task.WhenAny(wait, stopped) != wait) return;
                        await wait;
                    }
                    for (var offset = 0; offset < body.Length; offset += response.FragmentSize)
                    {
                        var count = Math.Min(response.FragmentSize, body.Length - offset);
                        if (response.Chunked) await WriteAsciiAsync(stream, count.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
                        await stream.WriteAsync(body, offset, count, _stop.Token);
                        if (response.Chunked) await WriteAsciiAsync(stream, "\r\n");
                    }
                    if (response.Chunked) await WriteAsciiAsync(stream, "0\r\n\r\n");
                }
                catch (Exception error) when (error is IOException or SocketException || _stop.IsCancellationRequested && error is ObjectDisposedException or OperationCanceledException) { }
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception error) { _errors.Enqueue(error); }
        finally { _clients.TryRemove(client, out _); }
    }
    private async Task<Request> ReadRequestAsync(NetworkStream stream)
    {
        var requestLine = (await ReadLineAsync(stream, MaximumHeaders)).Split(' ');
        if (requestLine.Length != 3) throw new InvalidDataException("Malformed HTTP request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var size = 0;
        while (true)
        {
            var line = await ReadLineAsync(stream, MaximumHeaders - size); size += line.Length + 2;
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            if (separator <= 0 || headers.ContainsKey(line.Substring(0, separator))) throw new InvalidDataException("Malformed or duplicate request header.");
            headers.Add(line.Substring(0, separator), line.Substring(separator + 1).Trim());
        }
        using var body = new MemoryStream();
        if (headers.TryGetValue("Transfer-Encoding", out var transfer))
        {
            if (!string.Equals(transfer, "chunked", StringComparison.OrdinalIgnoreCase) || headers.ContainsKey("Content-Length")) throw new InvalidDataException("Unsupported request framing.");
            while (true)
            {
                var line = await ReadLineAsync(stream, 128);
                var end = line.IndexOf(';');
                if (!int.TryParse(end < 0 ? line : line.Substring(0, end), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var count) || count < 0) throw new InvalidDataException("Invalid chunk size.");
                if (count == 0)
                {
                    var trailers = 0;
                    while ((line = await ReadLineAsync(stream, MaximumHeaders - trailers)).Length > 0) trailers += line.Length + 2;
                    break;
                }
                await CopyBodyAsync(stream, body, count);
                if ((await ReadLineAsync(stream, 2)).Length != 0) throw new InvalidDataException("Invalid chunk terminator.");
            }
        }
        else if (headers.TryGetValue("Content-Length", out var length))
        {
            if (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0) throw new InvalidDataException("Invalid Content-Length.");
            await CopyBodyAsync(stream, body, count);
        }
        return new(requestLine[0], requestLine[1], new UTF8Encoding(false, true).GetString(body.ToArray()), headers);
    }
    private async Task CopyBodyAsync(NetworkStream stream, MemoryStream body, int count)
    {
        if (body.Length + count > MaximumBody) throw new InvalidDataException("Request body exceeds the fixture bound.");
        var buffer = new byte[Math.Min(4096, count)];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, count), _stop.Token);
            if (read == 0) throw new EndOfStreamException();
            body.Write(buffer, 0, read); count -= read;
        }
    }
    private async Task<string> ReadLineAsync(NetworkStream stream, int maximum)
    {
        if (maximum <= 0) throw new InvalidDataException("Headers exceed the fixture bound.");
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
        throw new InvalidDataException("HTTP line exceeds the fixture bound.");
    }
    private async Task WriteAsciiAsync(NetworkStream stream, string value)
    { var bytes = Encoding.ASCII.GetBytes(value); await stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token); }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
        if (!_errors.IsEmpty) throw new AggregateException(_errors);
    }
}
