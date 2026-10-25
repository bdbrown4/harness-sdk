using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Strands.OpenTelemetry.Tests;

internal sealed class OtlpLoopback : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<Request> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;
    private readonly object _gate = new();
    private readonly int _statusCode;
    private readonly bool _withholdResponse;
    private TcpClient? _active;
    private bool _disposed;

    internal OtlpLoopback(int statusCode = 200, bool withholdResponse = false)
    {
        _statusCode = statusCode;
        _withholdResponse = withholdResponse;
        _listener.Start();
        Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture));
        _server = ServeAsync();
    }

    internal Uri Endpoint { get; }
    internal bool HasReceivedRequest => _first.Task.Status == TaskStatus.RanToCompletion;

    internal async Task<Request> ReceiveAsync()
    {
        if (await Task.WhenAny(_first.Task, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false) != _first.Task)
            throw new TimeoutException("No OTLP request reached the real TCP collector.");
        return await _first.Task.ConfigureAwait(false);
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return;
                    _active = client;
                }
                try
                {
                    var stream = client.GetStream();
                    var requestLine = await ReadLineAsync(stream).ConfigureAwait(false);
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var totalHeaderSize = requestLine.Length;
                    while (true)
                    {
                        var line = await ReadLineAsync(stream).ConfigureAwait(false);
                        if (line.Length == 0) break;
                        totalHeaderSize += line.Length;
                        if (totalHeaderSize > 64 * 1024) throw new InvalidDataException("Collector headers exceeded the test bound.");
                        var colon = line.IndexOf(':');
                        if (colon <= 0) throw new InvalidDataException("Malformed collector header.");
                        headers.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim());
                    }
                    if (headers.TryGetValue("Expect", out var expectation) && expectation.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        await stream.WriteAsync(interim, 0, interim.Length).ConfigureAwait(false);
                    }
                    byte[] body;
                    if (headers.TryGetValue("Transfer-Encoding", out var transfer) && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        using var buffer = new MemoryStream();
                        while (true)
                        {
                            var line = await ReadLineAsync(stream).ConfigureAwait(false);
                            var extension = line.IndexOf(';');
                            var length = int.Parse(extension < 0 ? line : line.Substring(0, extension), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            if (length == 0)
                            {
                                while ((await ReadLineAsync(stream).ConfigureAwait(false)).Length != 0) { }
                                break;
                            }
                            if (length < 0 || buffer.Length + length > 2 * 1024 * 1024) throw new InvalidDataException("Collector body exceeded the test bound.");
                            var chunk = await ReadExactAsync(stream, length).ConfigureAwait(false);
                            buffer.Write(chunk, 0, chunk.Length);
                            if ((await ReadLineAsync(stream).ConfigureAwait(false)).Length != 0) throw new InvalidDataException("Malformed chunk delimiter.");
                        }
                        body = buffer.ToArray();
                    }
                    else
                    {
                        var length = int.Parse(headers["Content-Length"], CultureInfo.InvariantCulture);
                        if (length < 0 || length > 2 * 1024 * 1024) throw new InvalidDataException("Collector body exceeded the test bound.");
                        body = await ReadExactAsync(stream, length).ConfigureAwait(false);
                    }
                    var parts = requestLine.Split(' ');
                    var request = new Request(parts[0], parts[1], headers, body);
                    _first.TrySetResult(request);
                    if (_withholdResponse)
                    {
                        await _stopped.Task.ConfigureAwait(false);
                        return;
                    }
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + _statusCode.ToString(CultureInfo.InvariantCulture) +
                        " Response\r\nContent-Type: application/x-protobuf\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
                }
                finally { lock (_gate) _active = null; }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_disposed) return;
            }
            _first.TrySetException(error);
            throw;
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var single = new byte[1];
        while (bytes.Count <= 8192)
        {
            if (await stream.ReadAsync(single, 0, 1).ConfigureAwait(false) == 0) throw new EndOfStreamException();
            bytes.Add(single[0]);
            if (bytes.Count >= 2 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
        }
        throw new InvalidDataException("Collector line exceeded the test bound.");
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _stopped.TrySetResult(true);
            _listener.Stop();
            _active?.Dispose();
        }
        if (!_server.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Collector did not stop.");
    }

    internal sealed class Request(string method, string path, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        internal string Method { get; } = method;
        internal string Path { get; } = path;
        internal IReadOnlyDictionary<string, string> Headers { get; } = headers;
        internal byte[] Body { get; } = body;
    }
}
