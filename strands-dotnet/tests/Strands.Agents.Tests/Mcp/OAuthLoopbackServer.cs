using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Tests.Mcp;

internal sealed class OAuthLoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accept;
    private int _tokens;
    internal System.Collections.Concurrent.ConcurrentQueue<OAuthRequest> Requests { get; } = new();
    internal Uri Origin { get; }
    internal Uri Resource => new(Origin, "mcp");
    internal Func<OAuthRequest, Task<OAuthResponse?>>? Respond { get; set; }

    internal OAuthLoopbackServer()
    {
        _listener.Start();
        Origin = new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/");
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                lock (_connections) _connections.Add(ServeAsync(client));
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var headerBytes = new List<byte>();
            var single = new byte[1];
            while (headerBytes.Count < 32768)
            {
                if (await stream.ReadAsync(single, 0, 1, _stop.Token).ConfigureAwait(false) == 0) return;
                headerBytes.Add(single[0]);
                var count = headerBytes.Count;
                if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10 && headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10) break;
            }
            var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
            }
            var length = headers.TryGetValue("Content-Length", out var text) ? int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("This bounded fixture requires a replayable fixed-length request.");
            if (length > 20 * 1024 * 1024) throw new InvalidDataException("Fixture body limit exceeded.");
            var body = new byte[length];
            for (var offset = 0; offset < length;)
            {
                var read = await stream.ReadAsync(body, offset, length - offset, _stop.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            var request = new OAuthRequest(first[0], first[1], headers, body);
            Requests.Enqueue(request);
            var response = Respond is { } respond ? await respond(request).ConfigureAwait(false) : null;
            response ??= DefaultResponse(request);
            var output = Encoding.UTF8.GetBytes(response.Body);
            var responseHeaders = "HTTP/1.1 " + response.Status + " Response\r\nConnection: close\r\nContent-Type: application/json\r\n" +
                (response.Chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + output.Length + "\r\n")
                + string.Join("", response.Headers.Select(pair => pair.Key + ": " + pair.Value + "\r\n")) + "\r\n";
            var head = Encoding.ASCII.GetBytes(responseHeaders);
            try
            {
                await stream.WriteAsync(head, 0, head.Length, _stop.Token).ConfigureAwait(false);
                if (response.Chunked)
                {
                    var prefix = Encoding.ASCII.GetBytes(output.Length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
                    await stream.WriteAsync(prefix, 0, prefix.Length, _stop.Token).ConfigureAwait(false);
                    await stream.WriteAsync(output, 0, output.Length, _stop.Token).ConfigureAwait(false);
                    var end = Encoding.ASCII.GetBytes("\r\n0\r\n\r\n");
                    await stream.WriteAsync(end, 0, end.Length, _stop.Token).ConfigureAwait(false);
                }
                else await stream.WriteAsync(output, 0, output.Length, _stop.Token).ConfigureAwait(false);
            }
            catch (IOException) { /* A canceled test request may close its accepted socket before the response. */ }
        }
    }

    private OAuthResponse DefaultResponse(OAuthRequest request) => request.Path switch
    {
        "/mcp" => request.Headers.TryGetValue("Authorization", out var authorization) && authorization.StartsWith("Bearer access-", StringComparison.Ordinal)
            ? new(200, "{\"accepted\":true}")
            : new(401, "{}", new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer resource_metadata=\"" + new Uri(Origin, ".well-known/oauth-protected-resource/mcp") + "\", scope=\"required\"" }),
        "/.well-known/oauth-protected-resource/mcp" => Json(new { resource = Resource.AbsoluteUri, authorization_servers = new[] { new Uri(Origin, "issuer").AbsoluteUri }, scopes_supported = new[] { "metadata" } }),
        "/.well-known/oauth-authorization-server/issuer" => Json(new { issuer = new Uri(Origin, "issuer").AbsoluteUri, token_endpoint = new Uri(Origin, "token").AbsoluteUri, grant_types_supported = new[] { "client_credentials" }, token_endpoint_auth_methods_supported = new[] { "client_secret_basic" } }),
        "/token" => Json(new { access_token = "access-" + Interlocked.Increment(ref _tokens), token_type = "Bearer", expires_in = 3600 }),
        _ => new(404, "{}")
    };

    internal static OAuthResponse Json(object value) => new(200, JsonSerializer.Serialize(value));
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept.ConfigureAwait(false);
        Task[] pending;
        lock (_connections) pending = _connections.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        _stop.Dispose();
    }
}

internal sealed record OAuthRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body);
internal sealed class OAuthResponse(int status, string body, IReadOnlyDictionary<string, string>? headers = null, bool chunked = false)
{
    internal int Status { get; } = status;
    internal string Body { get; } = body;
    internal IReadOnlyDictionary<string, string> Headers { get; } = headers ?? new Dictionary<string, string>();
    internal bool Chunked { get; } = chunked;
}
