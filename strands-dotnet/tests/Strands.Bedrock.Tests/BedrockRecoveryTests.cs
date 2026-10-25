using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Xunit.Abstractions;

namespace Strands.Bedrock.Tests;

public sealed class BedrockRecoveryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EligibleOfficialValidationRetriesOnceAndRemembersDropBlock(bool streaming)
    {
        await using var server = new RecoveryServer(streaming, error: true, requests: 3);
        using var client = server.CreateClient();
        var fields = JsonSerializer.Deserialize<JsonElement>("""{"thinking":{"type":"adaptive"},"anthropic_beta":["other-beta"],"kept":{"value":"snow 雪"}}""");
        using var model = new BedrockModel(client, new()
        {
            ModelId = "fixture-model",
            Streaming = streaming,
            RequestTimeout = TimeSpan.FromSeconds(15),
            AdditionalModelRequestFields = fields,
            RecoverStaleThinkingBlocks = true,
        });
        var request = new ModelRequest([Message.User("hello 雪")]);
        try
        {
            Assert.Equal("summary", await CollectAsync(model, request));
        }
        catch (ModelException failure)
        {
            var service = failure.InnerException as AmazonServiceException;
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Phase = "official-error-shape",
                Streaming = streaming,
                OuterType = failure.GetType().FullName,
                CauseType = failure.InnerException?.GetType().FullName,
                ErrorCode = service?.ErrorCode,
                Message = service?.Message,
                StatusCode = (int?)service?.StatusCode,
                WireRequests = server.Bodies.Count,
            }));
            var validation = Assert.IsType<Amazon.BedrockRuntime.Model.ValidationException>(failure.InnerException);
            Assert.Equal("ValidationException", validation.ErrorCode);
            throw;
        }
        Assert.Equal("summary", await CollectAsync(model, request));
        await server.Completion;
        Assert.Equal(3, server.Bodies.Count);
        var payloads = server.Bodies.Select(body => JsonSerializer.Deserialize<JsonElement>(body)).ToArray();
        var initial = payloads[0].GetProperty("additionalModelRequestFields");
        Assert.False(initial.GetProperty("thinking").TryGetProperty("block_binding", out _));
        Assert.Equal(new[] { "other-beta" }, initial.GetProperty("anthropic_beta").EnumerateArray().Select(value => value.GetString()));
        foreach (var payload in payloads.Skip(1))
        {
            var accepted = payload.GetProperty("additionalModelRequestFields");
            Assert.Equal("drop_block", accepted.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
            Assert.Equal(new[] { "other-beta", "thinking-binding-controls-2026-08-01" }, accepted.GetProperty("anthropic_beta").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("snow 雪", accepted.GetProperty("kept").GetProperty("value").GetString());
            Assert.Equal(payloads[0].GetProperty("messages").GetRawText(), payload.GetProperty("messages").GetRawText());
        }
        Assert.Equal(fields.GetRawText(), model.GetConfig().AdditionalModelRequestFields!.Value.GetRawText());
        Assert.Single(request.Messages);
        Assert.True(server.Signed);
        output.WriteLine(JsonSerializer.Serialize(new { Phase = "recovery-wire", Streaming = streaming, Requests = payloads, BorrowedClient = true }));
    }

    private static async Task<string> CollectAsync(IModel model, ModelRequest request)
    {
        var text = new StringBuilder();
        await foreach (var item in model.StreamAsync(request))
            if (item is ModelContentBlockDeltaEvent { Text: { } delta }) text.Append(delta);
        return text.ToString();
    }

    internal sealed class RecoveryServer : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly bool _streaming;
        private readonly bool _countTokens;
        private readonly bool _error;
        private readonly string _errorMessage;
        private readonly int _requests;
        private readonly string _url;
        private TcpClient? _accepted;
        private bool _closed;
        internal Task Completion { get; }
        internal string Target { get; private set; } = "";
        internal string Body { get; private set; } = "";
        internal bool Signed { get; private set; }
        internal List<string> Bodies { get; } = [];

        internal RecoveryServer(bool streaming, bool countTokens = false, bool error = false, int requests = 1, string errorMessage = "The block is bound to a different conversation.")
        {
            _streaming = streaming;
            _countTokens = countTokens;
            _error = error;
            _errorMessage = errorMessage;
            _requests = requests;
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
            using var registration = _stop.Token.Register(_listener.Stop);
            try
            {
                for (var iteration = 0; iteration < _requests; iteration++)
                {
                    using var socket = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_closed) return;
                        _accepted = socket;
                    }
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
                        if (length > 65536) throw new InvalidDataException("The loopback request header exceeded its bound.");
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var first = lines[0].Split(' ');
                    Target = first[1];
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines.Skip(1))
                    {
                        var colon = line.IndexOf(':');
                        if (colon >= 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                    }
                    Signed = headers.TryGetValue("Authorization", out var signature) && signature.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal);
                    if (!Signed || !headers.TryGetValue("Content-Length", out var rawLength) || !int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out var bodyLength) || bodyLength is < 0 or > 65536)
                        throw new InvalidDataException("The loopback fixture requires a signed bounded AWS request.");
                    if (headers.TryGetValue("Expect", out var expect) && string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
                        await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n")).ConfigureAwait(false);
                    var body = new byte[bodyLength];
                    for (var offset = 0; offset < body.Length;)
                    {
                        var count = await stream.ReadAsync(body, offset, body.Length - offset, _stop.Token).ConfigureAwait(false);
                        if (count == 0) throw new EndOfStreamException();
                        offset += count;
                    }
                    Body = Encoding.UTF8.GetString(body);
                    Bodies.Add(Body);
                    var failedRequest = _error && iteration == 0;
                    var response = failedRequest ? JsonSerializer.SerializeToUtf8Bytes(new { message = _errorMessage }) : _countTokens ? Encoding.UTF8.GetBytes("{\"inputTokens\":9}") : _streaming ? StreamResponse() : Encoding.UTF8.GetBytes("""{"output":{"message":{"role":"assistant","content":[{"text":"summary"}]}},"stopReason":"end_turn","usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5},"metrics":{"latencyMs":1}}""");
                    var contentType = _streaming && !failedRequest ? "application/vnd.amazon.eventstream" : "application/json";
                    var status = failedRequest ? "400 Bad Request\r\nx-amzn-ErrorType: ValidationException" : "200 OK";
                    await WriteAsync(stream, Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Type: " + contentType + "\r\nContent-Length: " + response.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n")).ConfigureAwait(false);
                    await WriteAsync(stream, response).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (_stop.IsCancellationRequested && error is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException) { }
        }

        private Task WriteAsync(Stream stream, byte[] bytes) => stream.WriteAsync(bytes, 0, bytes.Length, _stop.Token);

        private static byte[] StreamResponse()
        {
            using var output = new MemoryStream();
            foreach (var (type, json) in new[]
            {
                ("messageStart", """{"role":"assistant"}"""),
                ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"summary"}}"""),
                ("contentBlockStop", """{"contentBlockIndex":0}"""),
                ("messageStop", """{"stopReason":"end_turn"}"""),
                ("metadata", """{"usage":{"inputTokens":3,"outputTokens":2,"totalTokens":5},"metrics":{"latencyMs":1}}""")
            })
            {
                var messageType = new EventStreamHeader(":message-type"); messageType.SetString("event");
                var eventType = new EventStreamHeader(":event-type"); eventType.SetString(type);
                var contentType = new EventStreamHeader(":content-type"); contentType.SetString("application/json");
                var frame = new EventStreamMessage(new List<IEventStreamHeader> { messageType, eventType, contentType }, Encoding.UTF8.GetBytes(json)).ToByteArray();
                output.Write(frame, 0, frame.Length);
            }
            return output.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            TcpClient? accepted;
            lock (_gate) { _closed = true; accepted = _accepted; }
            _stop.Cancel();
            _listener.Stop();
            accepted?.Dispose();
            try { await Completion.ConfigureAwait(false); }
            finally { _stop.Dispose(); }
        }
    }
}
