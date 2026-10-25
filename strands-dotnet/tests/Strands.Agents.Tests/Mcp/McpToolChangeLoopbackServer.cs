using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Strands.Agents.Tests.Mcp;

internal sealed class McpToolChangeLoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = [];
    private readonly List<Task> _requests = [];
    private readonly List<Exception> _errors = [];
    private readonly List<Subscription> _subscriptions = [];
    private readonly Task _accept;
    private int _listCount;
    private int _listenCount;

    internal McpToolChangeLoopbackServer()
    {
        _listener.Start();
        Endpoint = new("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/mcp");
        _accept = AcceptAsync();
    }

    internal Uri Endpoint { get; }
    internal bool Grant { get; set; } = true;
    internal bool HoldAcknowledgement { get; set; }
    internal bool OmitAcknowledgementMetadata { get; set; }
    internal bool ForeignAcknowledgementFirst { get; set; }
    internal bool ChangeBeforeAcknowledgement { get; set; }
    internal bool MalformedAcknowledgement { get; set; }
    internal bool NumericAcknowledgementId { get; set; }
    internal string ToolName { get; set; } = "first";
    internal Func<int, CancellationToken, Task>? BeforeList { get; set; }
    internal Func<int, bool>? FailList { get; set; }
    internal int ListCount => Volatile.Read(ref _listCount);
    internal int ListenCount => Volatile.Read(ref _listenCount);
    internal TaskCompletionSource<bool> ListenClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> ListenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> ReleaseAcknowledgement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Per-instance phase observations used only by the fixture lifetime regression.
    internal Func<Task>? BeforeErrorWrite { get; set; }
    internal Func<Task>? AfterErrorWrite { get; set; }
    internal TaskCompletionSource<bool> ListenDrainEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> ListenFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void EndListen()
    {
        lock (_gate) foreach (var subscription in _subscriptions) subscription.Socket.Shutdown(SocketShutdown.Send);
    }

    internal async Task NotifyAsync(string? subscriptionId = null)
    {
        Subscription[] streams;
        lock (_gate) streams = _subscriptions.ToArray();
        foreach (var subscription in streams)
            await subscription.SendAsync("notifications/tools/list_changed", new
            { _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/subscriptionId"] = subscriptionId ?? subscription.Id } }, _shutdown.Token).ConfigureAwait(false);
    }

    internal async Task CancelListenAsync()
    {
        Subscription[] streams;
        lock (_gate) streams = _subscriptions.ToArray();
        foreach (var subscription in streams)
            await subscription.SendAsync("notifications/cancelled", new { requestId = subscription.Id, reason = "fixture subscription canceled" }, _shutdown.Token).ConfigureAwait(false);
    }

    internal async Task RepeatAcknowledgementAsync()
    {
        Subscription[] streams;
        lock (_gate) streams = _subscriptions.ToArray();
        foreach (var subscription in streams)
            await subscription.SendAsync("notifications/subscriptions/acknowledged", new
            { notifications = new { toolsListChanged = true }, _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/subscriptionId"] = subscription.Id } }, _shutdown.Token).ConfigureAwait(false);
    }

    internal async Task FailListenAsync(string message)
    {
        Subscription[] streams;
        lock (_gate) streams = _subscriptions.ToArray();
        foreach (var subscription in streams) await subscription.SendErrorAsync(message, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception error) when (_shutdown.IsCancellationRequested && error is SocketException or ObjectDisposedException) { return; }
            lock (_gate) { _clients.Add(client); _requests.Add(ServeAsync(client)); }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var listening = false;
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var first = await ReadLineAsync(stream).ConfigureAwait(false);
                var method = first.Split(' ')[0];
                var length = 0;
                var chunked = false;
                while (true)
                {
                    var line = await ReadLineAsync(stream).ConfigureAwait(false);
                    if (line.Length == 0) break;
                    var colon = line.IndexOf(':');
                    if (colon <= 0) throw new InvalidDataException("Malformed fixture header.");
                    var name = line.Substring(0, colon);
                    var value = line.Substring(colon + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = int.Parse(value, CultureInfo.InvariantCulture);
                    if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) chunked = value.Equals("chunked", StringComparison.OrdinalIgnoreCase);
                }
                if (method != "POST") { await WriteJsonAsync(stream, "{}", method == "DELETE" ? 204 : 405).ConfigureAwait(false); return; }
                var body = chunked ? await ReadChunkedAsync(stream).ConfigureAwait(false) : await ReadExactlyAsync(stream, length).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                var request = document.RootElement;
                if (!request.TryGetProperty("id", out var id)) { await WriteJsonAsync(stream, "{}", 202).ConfigureAwait(false); return; }
                var rpc = request.GetProperty("method").GetString();
                if (rpc == "subscriptions/listen")
                {
                    listening = true;
                    if (request.GetProperty("params").GetProperty("notifications").GetProperty("toolsListChanged").ValueKind != JsonValueKind.True)
                        throw new InvalidDataException("Client did not request tool notifications.");
                    Interlocked.Increment(ref _listenCount);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, 0, header.Length, _shutdown.Token).ConfigureAwait(false);
                    var subscription = new Subscription(stream, client.Client, id.GetString()!,
                        () => BeforeErrorWrite?.Invoke() ?? Task.CompletedTask, () => AfterErrorWrite?.Invoke() ?? Task.CompletedTask);
                    lock (_gate) _subscriptions.Add(subscription);
                    try
                    {
                        ListenEntered.TrySetResult(true);
                        var one = new byte[1];
                        var peerClosed = stream.ReadAsync(one, 0, one.Length, _shutdown.Token);
                        if (HoldAcknowledgement && await Task.WhenAny(ReleaseAcknowledgement.Task, peerClosed).ConfigureAwait(false) != ReleaseAcknowledgement.Task) return;
                        if (ForeignAcknowledgementFirst) await subscription.SendAsync("notifications/subscriptions/acknowledged", new
                        {
                            notifications = new { toolsListChanged = false },
                            _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/subscriptionId"] = "unrelated-application-listen" }
                        }, _shutdown.Token).ConfigureAwait(false);
                        if (ChangeBeforeAcknowledgement) await subscription.SendAsync("notifications/tools/list_changed", new
                        { _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/subscriptionId"] = subscription.Id } }, _shutdown.Token).ConfigureAwait(false);
                        var acknowledgement = new Dictionary<string, object> { ["notifications"] = MalformedAcknowledgement ? "malformed" : new { toolsListChanged = Grant } };
                        if (!OmitAcknowledgementMetadata) acknowledgement["_meta"] = new Dictionary<string, object> { ["io.modelcontextprotocol/subscriptionId"] = NumericAcknowledgementId ? 42 : subscription.Id };
                        await subscription.SendAsync("notifications/subscriptions/acknowledged", acknowledgement, _shutdown.Token).ConfigureAwait(false);
                        while (await peerClosed.ConfigureAwait(false) != 0)
                            peerClosed = stream.ReadAsync(one, 0, one.Length, _shutdown.Token);
                    }
                    finally
                    {
                        lock (_gate) _subscriptions.Remove(subscription);
                        var drain = subscription.CloseAsync();
                        ListenDrainEntered.TrySetResult(true);
                        await drain.ConfigureAwait(false);
                        ListenClosed.TrySetResult(true);
                    }
                    return;
                }
                object result;
                if (rpc == "server/discover") result = new
                {
                    supportedVersions = new[] { "2026-07-28" },
                    _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/serverInfo"] = new { name = "tool-changes-fixture", version = "1" } },
                    capabilities = new { tools = new { listChanged = true } },
                    ttlMs = 0,
                    cacheScope = "private"
                };
                else if (rpc == "tools/list")
                {
                    var number = Interlocked.Increment(ref _listCount);
                    if (BeforeList is { } before) await before(number, _shutdown.Token).ConfigureAwait(false);
                    if (FailList?.Invoke(number) == true)
                    {
                        await WriteJsonAsync(stream, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32603, message = "Fixture listing failure." } })).ConfigureAwait(false);
                        return;
                    }
                    result = new { tools = new[] { new { name = ToolName, description = "A watched tool.", inputSchema = new { type = "object" } } }, ttlMs = 0, cacheScope = "private" };
                }
                else throw new InvalidDataException("Unexpected fixture request: " + rpc);
                await WriteJsonAsync(stream, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result })).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is EndOfStreamException || error is IOException { InnerException: SocketException } || _shutdown.IsCancellationRequested && error is IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception error) { lock (_gate) _errors.Add(error); }
        finally { if (listening) ListenFinished.TrySetResult(true); }
    }

    private async Task WriteJsonAsync(NetworkStream stream, string body, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, 0, header.Length, _shutdown.Token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
    {
        if (length < 0 || length > 1024 * 1024) throw new InvalidDataException("Fixture request exceeds its byte bound.");
        var bytes = new byte[length];
        var offset = 0;
        while (offset != bytes.Length)
        {
            var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, _shutdown.Token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return bytes;
    }

    private async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        while (bytes.Count != 65536)
        {
            bytes.Add((await ReadExactlyAsync(stream, 1).ConfigureAwait(false))[0]);
            if (bytes.Count >= 2 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
        }
        throw new InvalidDataException("Fixture line exceeds its byte bound.");
    }

    private async Task<byte[]> ReadChunkedAsync(NetworkStream stream)
    {
        using var output = new MemoryStream();
        while (true)
        {
            var size = int.Parse((await ReadLineAsync(stream).ConfigureAwait(false)).Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (size == 0) { while ((await ReadLineAsync(stream).ConfigureAwait(false)).Length != 0) { } return output.ToArray(); }
            if (output.Length + size > 1024 * 1024) throw new InvalidDataException("Fixture body exceeds its byte bound.");
            var bytes = await ReadExactlyAsync(stream, size).ConfigureAwait(false);
            output.Write(bytes, 0, bytes.Length);
            if ((await ReadLineAsync(stream).ConfigureAwait(false)).Length != 0) throw new InvalidDataException("Malformed chunk terminator.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        await _accept.ConfigureAwait(false);
        Task[] requests;
        lock (_gate) { foreach (var client in _clients) client.Dispose(); requests = _requests.ToArray(); }
        try
        {
            await Task.WhenAll(requests).ConfigureAwait(false);
            lock (_gate) if (_errors.Count != 0) throw new AggregateException(_errors);
        }
        finally { _shutdown.Dispose(); }
    }

    private sealed class Subscription(NetworkStream stream, Socket socket, string id, Func<Task> beforeErrorWrite, Func<Task> afterErrorWrite)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly object _admissionGate = new();
        private int _writers;
        private bool _closing;
        private TaskCompletionSource<bool>? _drained;
        internal string Id { get; } = id;
        internal Socket Socket { get; } = socket;
        internal async Task SendAsync(string method, object parameters, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }) + "\n\n");
            await SendCoreAsync(bytes, false, token).ConfigureAwait(false);
        }
        internal async Task SendErrorAsync(string message, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Id, error = new { code = -32000, message } }) + "\n\n");
            await SendCoreAsync(bytes, true, token).ConfigureAwait(false);
        }

        private async Task SendCoreAsync(byte[] bytes, bool error, CancellationToken token)
        {
            lock (_admissionGate)
            {
                if (_closing) throw new InvalidOperationException("The fixture subscription is closing.");
                _writers++;
            }
            var entered = false;
            try
            {
                await _write.WaitAsync(token).ConfigureAwait(false);
                entered = true;
                if (error) await beforeErrorWrite().ConfigureAwait(false);
                await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                if (error) await afterErrorWrite().ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                if (entered) _write.Release();
                lock (_admissionGate)
                {
                    if (--_writers == 0) _drained?.TrySetResult(true);
                }
            }
        }

        internal Task CloseAsync()
        {
            lock (_admissionGate)
            {
                _closing = true;
                return _writers == 0 ? Task.CompletedTask : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }
}
