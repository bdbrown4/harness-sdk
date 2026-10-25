using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Strands.Harness.Testing;

internal sealed record WebTestRequest(string Target, IReadOnlyDictionary<string, string> Headers);
internal sealed record WebTestResponse(byte[] Body, string? ContentType = "text/plain; charset=utf-8", int Status = 200, string? Location = null)
{
    internal string? SetCookie { get; init; }
    internal bool AllowPeerAbort { get; init; }
    internal TaskCompletionSource<bool>? BodyGate { get; init; }
    internal TaskCompletionSource<bool>? HeadersSent { get; init; }
}

internal sealed class WebTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<WebTestRequest, WebTestResponse> _response;
    private readonly List<TcpClient> _clients = [];
    private readonly List<Task> _requests = [];
    private readonly Task _accept;
    private int _calls;
    internal int Calls => Volatile.Read(ref _calls);
    internal Uri Endpoint { get; }
    internal WebTestServer(Func<WebTestRequest, WebTestResponse> response)
    {
        _response = response;
        _listener.Start();
        Endpoint = new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (Exception error) when (_shutdown.IsCancellationRequested && error is SocketException or ObjectDisposedException) { return; }
            lock (_clients) { _clients.Add(client); _requests.Add(RespondAsync(client)); }
        }
    }
    private async Task RespondAsync(TcpClient client)
    {
        WebTestResponse? response = null;
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 65536)
                {
                    if (await stream.ReadAsync(one, 0, 1, _shutdown.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[header.Count - 4] == 13 && header[header.Count - 3] == 10 && header[header.Count - 2] == 13 && header[header.Count - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1)) { var colon = line.IndexOf(':'); if (colon > 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim(); }
                Interlocked.Increment(ref _calls);
                response = _response(new(lines[0].Split(' ')[1], headers));
                var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + response.Status + " Fixture\r\n" + (response.ContentType is null ? "" : "Content-Type: " + response.ContentType + "\r\n") + "Content-Length: " + response.Body.Length + "\r\nConnection: close\r\n" + (response.Location is null ? "" : "Location: " + response.Location + "\r\n") + (response.SetCookie is null ? "" : "Set-Cookie: " + response.SetCookie + "\r\n") + "\r\n");
                await stream.WriteAsync(bytes, 0, bytes.Length, _shutdown.Token);
                response.HeadersSent?.TrySetResult(true);
                if (response.BodyGate is { } gate)
                {
                    await Task.WhenAny(gate.Task, Task.Delay(Timeout.Infinite, _shutdown.Token));
                    _shutdown.Token.ThrowIfCancellationRequested();
                    await gate.Task;
                }
                for (var offset = 0; offset < response.Body.Length; offset += 16384)
                    await stream.WriteAsync(response.Body, offset, Math.Min(16384, response.Body.Length - offset), _shutdown.Token);
            }
        }
        catch (Exception error) when (_shutdown.IsCancellationRequested && error is IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (IOException error) when (response?.AllowPeerAbort == true && error.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.Shutdown) { }
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel(); _listener.Stop();
        lock (_clients) foreach (var client in _clients) client.Dispose();
        try { await _accept; await Task.WhenAll(_requests); }
        finally { _shutdown.Dispose(); }
    }
}
