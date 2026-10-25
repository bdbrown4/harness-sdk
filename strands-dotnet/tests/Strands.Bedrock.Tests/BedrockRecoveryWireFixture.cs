using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;

namespace Strands.Bedrock.Tests;

internal sealed class BedrockRecoveryWireFixture : IAsyncDisposable
{
    internal const string BindingError = "The block is bound to a different conversation.";
    internal const string TurnError = "Conversation blocks and tool result blocks cannot be provided in the same turn.";
    internal sealed record Reply(string? Error = null, string Code = "ValidationException", int Status = 400,
        bool Malformed = false, bool StreamFault = false);
    internal sealed record Request(string Target, JsonElement Body, bool Signed);

    private readonly object _gate = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(40));
    private readonly Reply[] _replies;
    private readonly List<Request> _requests = [];
    private TcpClient? _accepted;
    private bool _closed;
    private readonly string _url;
    internal Task Completion { get; }
    internal Func<int, Task>? BeforeReply { get; init; }
    internal Task? BeforeStreamFault { get; init; }
    internal Request[] Requests { get { lock (_gate) return _requests.ToArray(); } }

    internal BedrockRecoveryWireFixture(params Reply[] replies)
    {
        _replies = replies.ToArray();
        _listener.Start();
        _url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        Completion = ServeAsync();
    }

    internal AmazonBedrockRuntimeClient CreateClient() => new(new BasicAWSCredentials("fixture", "fixture-secret"), new AmazonBedrockRuntimeConfig
    {
        ServiceURL = _url,
        AuthenticationRegion = "us-east-1",
        MaxErrorRetry = 0,
        Timeout = TimeSpan.FromSeconds(15)
    });

    private async Task ServeAsync()
    {
        using var stop = _stop.Token.Register(_listener.Stop);
        try
        {
            for (var index = 0; index < _replies.Length; index++)
            {
                using var socket = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                lock (_gate) { if (_closed) return; _accepted = socket; }
                using var stream = socket.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(one, 0, 1, _stop.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException();
                    header.WriteByte(one[0]);
                    var bytes = header.GetBuffer();
                    var length = (int)header.Length;
                    if (length >= 4 && bytes[length - 4] == 13 && bytes[length - 3] == 10 && bytes[length - 2] == 13 && bytes[length - 1] == 10) break;
                    if (length > 65536) throw new InvalidDataException("The fixture header exceeded its bound.");
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                var target = lines[0].Split(' ')[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon >= 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                }
                var signed = headers.TryGetValue("Authorization", out var signature) && signature.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal);
                if (!signed || !headers.TryGetValue("Content-Length", out var rawLength) || !int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 0 or > 65536)
                    throw new InvalidDataException("The fixture requires a signed bounded AWS request.");
                if (headers.TryGetValue("Expect", out var expect) && string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
                    await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n")).ConfigureAwait(false);
                var body = new byte[count];
                for (var offset = 0; offset < count;)
                {
                    var read = await stream.ReadAsync(body, offset, count - offset, _stop.Token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                var parsed = JsonSerializer.Deserialize<JsonElement>(body);
                lock (_gate) _requests.Add(new(target, parsed, signed));
                if (BeforeReply is { } before) await before(index).ConfigureAwait(false);
                var reply = _replies[index];
                var streaming = target.EndsWith("converse-stream", StringComparison.Ordinal);
                var payload = reply.Error is { } error ? JsonSerializer.SerializeToUtf8Bytes(new { message = error })
                    : reply.Malformed ? streaming ? new byte[] { 1, 2, 3 } : Encoding.UTF8.GetBytes("{}")
                    : reply.StreamFault ? StreamResponse(true)
                    : target.EndsWith("count-tokens", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("{\"inputTokens\":9}")
                    : streaming ? StreamResponse(false)
                    : Encoding.UTF8.GetBytes("""{"output":{"message":{"role":"assistant","content":[{"text":"summary"}]}},"stopReason":"end_turn","usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5}}""");
                var fault = reply.StreamFault ? StreamFaultFrame() : Array.Empty<byte>();
                var contentType = streaming && reply.Error is null ? "application/vnd.amazon.eventstream" : "application/json";
                var status = reply.Error is null ? "200 OK" : reply.Status.ToString(CultureInfo.InvariantCulture) + " Rejected\r\nx-amzn-ErrorType: " + reply.Code;
                await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Type: " + contentType + "\r\nContent-Length: " + (payload.Length + fault.Length).ToString(CultureInfo.InvariantCulture) + "\r\n\r\n")).ConfigureAwait(false);
                await WriteAsync(stream, payload).ConfigureAwait(false);
                if (reply.StreamFault)
                {
                    await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                    if (BeforeStreamFault is { } witness)
                    {
                        var settled = await Task.WhenAny(witness, Task.Delay(Timeout.Infinite, _stop.Token)).ConfigureAwait(false);
                        _stop.Token.ThrowIfCancellationRequested();
                        await settled.ConfigureAwait(false);
                    }
                    await WriteAsync(stream, fault).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException) { }
    }

    private Task WriteAsync(Stream stream, byte[] bytes) => stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token);

    private static byte[] StreamResponse(bool fail)
    {
        using var output = new MemoryStream();
        var frames = new List<(string Type, string Json)>
        {
            ("messageStart", """{"role":"assistant"}"""),
            ("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"text\":\"" + (fail ? "partial" : "summary") + "\"}}")
        };
        if (!fail) frames.AddRange(new[]
        {
            ("contentBlockStop", """{"contentBlockIndex":0}"""),
            ("messageStop", """{"stopReason":"end_turn"}"""),
            ("metadata", """{"usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5}}""")
        });
        foreach (var (type, json) in frames)
        {
            var messageType = new EventStreamHeader(":message-type"); messageType.SetString("event");
            var eventType = new EventStreamHeader(":event-type"); eventType.SetString(type);
            var contentType = new EventStreamHeader(":content-type"); contentType.SetString("application/json");
            var frame = new EventStreamMessage(new List<IEventStreamHeader> { messageType, eventType, contentType }, Encoding.UTF8.GetBytes(json)).ToByteArray();
            output.Write(frame, 0, frame.Length);
        }
        return output.ToArray();
    }

    private static byte[] StreamFaultFrame()
    {
        var messageType = new EventStreamHeader(":message-type"); messageType.SetString("exception");
        var exceptionType = new EventStreamHeader(":exception-type"); exceptionType.SetString("validationException");
        return new EventStreamMessage(new List<IEventStreamHeader> { messageType, exceptionType }, JsonSerializer.SerializeToUtf8Bytes(new { message = BindingError })).ToByteArray();
    }

    public async ValueTask DisposeAsync()
    {
        TcpClient? accepted;
        lock (_gate) { _closed = true; accepted = _accepted; }
        _stop.Cancel(); _listener.Stop(); accepted?.Dispose();
        try { await Completion.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
