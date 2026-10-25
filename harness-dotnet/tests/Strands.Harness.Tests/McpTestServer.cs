using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Strands.Harness.Testing;

internal sealed class McpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<TcpClient> _clients = [];
    private readonly List<Task> _requests = [];
    private readonly Task _accept;
    private int _initializations, _deletions, _calls;
    internal McpTestServer()
    {
        _listener.Start();
        Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/mcp");
        _accept = AcceptAsync();
    }
    internal Uri Endpoint { get; }
    internal int Initializations => Volatile.Read(ref _initializations);
    internal int Deletions => Volatile.Read(ref _deletions);
    internal int Calls => Volatile.Read(ref _calls);
    internal bool FailDiscovery { get; set; }
    internal bool FailInitialization { get; set; }
    internal string? Authorization { get; private set; }
    internal string? ApiKey { get; private set; }
    internal string? LastCallApiKey { get; private set; }
    internal string ToolName { get; set; } = "echo";
    internal string? ToolResponseText { get; set; }
    internal bool ToolIsError { get; set; }
    internal JsonElement? LastArguments { get; private set; }
    internal TaskCompletionSource<bool>? ToolCallGate { get; set; }
    internal TaskCompletionSource<bool> ToolCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool>? InitializationGate { get; set; }
    internal TaskCompletionSource<bool> InitializationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool LastClientReadReady { get { lock (_clients) return _clients.Count > 0 && _clients[_clients.Count - 1].Client.Poll(0, SelectMode.SelectRead); } }
    internal Task WaitForRequestsAsync() { lock (_clients) return Task.WhenAll(_requests); }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch (Exception exception) when (_shutdown.IsCancellationRequested && exception is SocketException or ObjectDisposedException) { return; }
            lock (_clients) { _clients.Add(client); _requests.Add(RespondAsync(client)); }
        }
    }
    private async Task RespondAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var header = new List<byte>();
                var single = new byte[1];
                while (header.Count < 65536)
                {
                    if (await stream.ReadAsync(single, 0, 1, _shutdown.Token) == 0) return;
                    header.Add(single[0]);
                    if (header.Count >= 4 && header[header.Count - 4] == 13 && header[header.Count - 3] == 10 && header[header.Count - 2] == 13 && header[header.Count - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines[0].StartsWith("GET ", StringComparison.Ordinal)) { await SendAsync(stream, 405, null); return; }
                if (lines[0].StartsWith("DELETE ", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _deletions);
                    Deleted.TrySetResult(true);
                    await SendAsync(stream, 204, null);
                    return;
                }
                var length = 0;
                var chunked = false;
                string? apiKey = null;
                foreach (var line in lines)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && line.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) chunked = true;
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) Authorization = line.Substring(14).Trim();
                    if (line.StartsWith("x-api-key:", StringComparison.OrdinalIgnoreCase)) ApiKey = apiKey = line.Substring(10).Trim();
                }
                var body = chunked ? await ReadChunkedAsync(stream) : await ReadExactlyAsync(stream, length);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id)) { await SendAsync(stream, 202, null); return; }
                var method = root.GetProperty("method").GetString();
                object response;
                if (method == "initialize")
                {
                    Interlocked.Increment(ref _initializations); InitializationStarted.TrySetResult(true);
                    if (InitializationGate is { } gate) await UntilShutdownAsync(gate.Task);
                    response = FailInitialization
                        ? (object)new { jsonrpc = "2.0", id, error = new { code = -32603, message = "fixture initialization failure" } }
                        : new { jsonrpc = "2.0", id, result = (object)new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "harness-fixture", version = "1.0" } } };
                }
                else if (method == "tools/list" && FailDiscovery) response = new { jsonrpc = "2.0", id, error = new { code = -32603, message = "fixture discovery failure" } };
                else if (method == "tools/list") response = new { jsonrpc = "2.0", id, result = new { tools = new[] { new { name = ToolName, description = "Echo a value", inputSchema = new { type = "object" } } } } };
                else if (method == "tools/call")
                {
                    Interlocked.Increment(ref _calls);
                    LastArguments = root.GetProperty("params").GetProperty("arguments").Clone();
                    LastCallApiKey = apiKey;
                    ToolCallStarted.TrySetResult(true);
                    if (ToolCallGate is { } gate) await UntilShutdownAsync(gate.Task);
                    var value = ToolResponseText ?? LastArguments.Value.GetProperty("value").GetString();
                    response = new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = value } }, isError = ToolIsError } };
                }
                else response = new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found: " + method } };
                await SendAsync(stream, 200, response);
            }
        }
        catch (Exception exception) when (_shutdown.IsCancellationRequested && exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
    private async Task SendAsync(NetworkStream stream, int status, object? response)
    {
        var body = response is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(response);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: application/json\r\nMcp-Session-Id: harness-fixture-session\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(header, 0, header.Length, _shutdown.Token);
            await stream.WriteAsync(body, 0, body.Length, _shutdown.Token);
        }
        catch (IOException exception) when (exception.InnerException is SocketException socket &&
            socket.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.Shutdown)
        {
            // Cancellation or session disposal can close the peer before any response acknowledgement.
            // Other IO/protocol errors remain observable through the request task and fixture disposal.
        }
    }
    private async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
    {
        if (length < 0 || length > 1024 * 1024) throw new InvalidDataException("Unexpected MCP fixture request length.");
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = await stream.ReadAsync(bytes, offset, length - offset, _shutdown.Token);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }
    private async Task<byte[]> ReadChunkedAsync(NetworkStream stream)
    {
        using var bytes = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stream);
            var size = int.Parse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            if (size == 0) { while ((await ReadLineAsync(stream)).Length != 0) { } return bytes.ToArray(); }
            if (bytes.Length + size > 1024 * 1024) throw new InvalidDataException("MCP fixture request exceeded its bound.");
            var chunk = await ReadExactlyAsync(stream, size); bytes.Write(chunk, 0, chunk.Length);
            if ((await ReadLineAsync(stream)).Length != 0) throw new InvalidDataException("Malformed HTTP chunk terminator.");
        }
    }
    private async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 65536)
        {
            var next = await ReadExactlyAsync(stream, 1); bytes.Add(next[0]);
            if (bytes.Count >= 2 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10) return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
        }
        throw new InvalidDataException("MCP fixture HTTP line exceeded its bound.");
    }
    private async Task UntilShutdownAsync(Task task)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _shutdown.Token.Register(() => cancelled.TrySetResult(true));
        await Task.WhenAny(task, cancelled.Task); _shutdown.Token.ThrowIfCancellationRequested(); await task;
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel(); _listener.Stop();
        lock (_clients) foreach (var client in _clients) client.Dispose();
        await _accept;
        Task requests;
        lock (_clients) requests = Task.WhenAll(_requests);
        if (await Task.WhenAny(requests, Task.Delay(TimeSpan.FromSeconds(10))) != requests) throw new TimeoutException("MCP fixture cleanup remained pending.");
        await requests; _shutdown.Dispose();
    }
}
