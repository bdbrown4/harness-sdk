using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents;
using Strands.Agents.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Compatibility.Tests;

public sealed class McpCompatibilityTests
{
    [Fact]
    public async Task HttpMcpToolCompletesNativeAgentLoop()
    {
        using var server = new FixtureServer();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        var tools = await client.ListToolsAsync(new McpToolListOptions { Prefix = "remote" });
        var model = new ToolModel();
        var agent = new Agent(new AgentOptions { Model = model, Tools = tools });

        var result = await WithDeadline(agent.InvokeAsync("Echo Café"));

        Assert.Equal("Café", result.Message!.Text);
        Assert.Equal(2, result.Turns);
        Assert.Equal("echo", server.CalledName);
        Assert.Equal("Café", server.Input);
        var toolResult = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[2].Content));
        Assert.Equal("request-1", toolResult.ToolUseId);
        Assert.Equal("Café", Assert.IsType<TextBlock>(Assert.Single(toolResult.ContentBlocks)).Text);
        Assert.Equal("Café", toolResult.StructuredContent!.Value.GetProperty("value").GetString());
        await client.DisposeAsync();
        Assert.False(client.IsConnected);
        Assert.False(server.Disposed);
        Assert.Equal(1, server.SessionDeletes);
    }

    [Fact]
    public async Task CancellationInterruptsAnInFlightHttpToolWithoutDisposingCallerClient()
    {
        using var server = new FixtureServer { BlockCalls = true };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        using var cancellation = new CancellationTokenSource();
        var call = client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "pending" }), cancellationToken: cancellation.Token).AsTask();
        await WithDeadline(server.CallStarted.Task);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithDeadline(call));
        Assert.True(client.IsConnected);
        await client.DisposeAsync();
        Assert.False(server.Disposed);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task NativeHttpHandlerCompletesSessionWithBodylessAcknowledgements()
    {
        using var server = new FixtureServer();
        await using var loopback = new LoopbackServer(server);
        using var transport = new NativeHttpHandler();
        using var http = new HttpClient(transport);
        await using var client = await ConnectAsync(http, loopback.Endpoint);
        var tools = await WithDeadline(client.ListToolsAsync());
        var result = await WithDeadline(client.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "Café" })).AsTask());

        Assert.Single(tools);
        Assert.Equal("Café", result.StructuredContent!.Value.GetProperty("value").GetString());
        Assert.Equal(1, transport.EmptyAcknowledgements);
        await client.DisposeAsync();
        Assert.False(transport.Disposed);
        Assert.Equal(1, server.SessionDeletes);
        Assert.Equal(1, transport.EmptySessionDeletes);
    }

    private static Task<Client> ConnectAsync(HttpClient http, Uri? endpoint = null) => Client.CreateHttpAsync(http,
        new HttpClientTransportOptions { Endpoint = endpoint ?? new Uri("https://mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) });

    private static async Task<T> WithDeadline<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task) throw new TimeoutException("MCP fixture operation did not complete.");
        return await task;
    }
    private static async Task WithDeadline(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task) throw new TimeoutException("MCP fixture operation did not complete.");
        await task;
    }

    private sealed class NativeHttpHandler() : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        public int EmptyAcknowledgements { get; private set; }
        public int EmptySessionDeletes { get; private set; }
        public bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Accepted || response.StatusCode == HttpStatusCode.NoContent)
            {
                Assert.NotNull(response.Content);
                Assert.NotNull(response.RequestMessage);
                Assert.Empty(await response.Content.ReadAsByteArrayAsync());
                if (response.StatusCode == HttpStatusCode.Accepted) EmptyAcknowledgements++;
                else EmptySessionDeletes++;
            }
            return response;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private const int MaximumHeaderBytes = 16 * 1024;
        private const int MaximumBodyBytes = 64 * 1024;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _requests;
        public Uri Endpoint { get; }
        public LoopbackServer(FixtureServer fixture)
        {
            // A socket avoids HTTP.sys URL reservations and remains bound until teardown.
            _listener.Start();
            var number = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri("http://127.0.0.1:" + number + "/mcp/");
            _requests = ServeAsync(fixture);
        }
        private async Task ServeAsync(FixtureServer fixture)
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    using var connection = await _listener.AcceptTcpClientAsync();
                    using var cancellation = _stopping.Token.Register(connection.Dispose);
                    using var stream = connection.GetStream();
                    using var request = await ReadRequestAsync(stream);
                    using var response = await fixture.RespondAsync(request, _stopping.Token);
                    var body = await response.Content.ReadAsByteArrayAsync();
                    if (body.Length > MaximumBodyBytes) throw new InvalidDataException("Fixture response exceeded its body limit.");
                    if (response.StatusCode == HttpStatusCode.NoContent && body.Length != 0) throw new InvalidDataException("A fixture 204 response must be bodyless.");
                    var headers = new StringBuilder("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ').Append(response.ReasonPhrase).Append("\r\n");
                    if (response.Content.Headers.ContentType is { } contentType) headers.Append("Content-Type: ").Append(contentType).Append("\r\n");
                    foreach (var header in response.Headers) headers.Append(header.Key).Append(": ").Append(string.Join(",", header.Value)).Append("\r\n");
                    if (response.StatusCode != HttpStatusCode.NoContent) headers.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                    headers.Append("Connection: close\r\n\r\n");
                    var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
                    await stream.WriteAsync(headerBytes, 0, headerBytes.Length, _stopping.Token);
                    await stream.WriteAsync(body, 0, body.Length, _stopping.Token);
                }
            }
            catch (SocketException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) when (_stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        }

        private async Task<HttpRequestMessage> ReadRequestAsync(NetworkStream stream)
        {
            var reader = new RequestReader(stream, _stopping.Token);
            var start = (await reader.ReadLineAsync()).Split(' ');
            if (start.Length != 3 || start[1] != Endpoint.AbsolutePath || (start[2] != "HTTP/1.1" && start[2] != "HTTP/1.0"))
                throw new InvalidDataException("Unexpected fixture HTTP request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line.Length == 0) break;
                var separator = line.IndexOf(':');
                if (separator <= 0) throw new InvalidDataException("Invalid fixture HTTP header.");
                var name = line.Substring(0, separator);
                if (headers.ContainsKey(name)) throw new InvalidDataException("Duplicate fixture HTTP header.");
                headers.Add(name, line.Substring(separator + 1).Trim());
            }
            if (headers.TryGetValue("Expect", out var expect))
            {
                if (!string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported fixture HTTP expectation.");
                var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await stream.WriteAsync(interim, 0, interim.Length, _stopping.Token);
            }
            byte[] body;
            if (headers.TryGetValue("Transfer-Encoding", out var transfer))
            {
                if (headers.ContainsKey("Content-Length") || !string.Equals(transfer, "chunked", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsupported fixture HTTP body framing.");
                using var chunks = new MemoryStream();
                for (var count = 0; ; count++)
                {
                    if (count >= 256) throw new InvalidDataException("Fixture request exceeded its chunk limit.");
                    var line = await reader.ReadLineAsync();
                    var extension = line.IndexOf(';');
                    var sizeText = extension < 0 ? line : line.Substring(0, extension);
                    if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0 || size > MaximumBodyBytes - chunks.Length)
                        throw new InvalidDataException("Invalid or oversized fixture HTTP chunk.");
                    if (size == 0)
                    {
                        while ((line = await reader.ReadLineAsync()).Length != 0)
                            if (line.IndexOf(':') <= 0) throw new InvalidDataException("Invalid fixture HTTP trailer.");
                        break;
                    }
                    var chunk = await reader.ReadBytesAsync(size);
                    chunks.Write(chunk, 0, chunk.Length);
                    if ((await reader.ReadLineAsync()).Length != 0) throw new InvalidDataException("Invalid fixture HTTP chunk terminator.");
                }
                body = chunks.ToArray();
            }
            else if (headers.TryGetValue("Content-Length", out var length))
            {
                if (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0 || size > MaximumBodyBytes)
                    throw new InvalidDataException("Invalid or oversized fixture HTTP body.");
                body = await reader.ReadBytesAsync(size);
            }
            else body = Array.Empty<byte>();
            return new HttpRequestMessage(new HttpMethod(start[0]), Endpoint) { Content = new ByteArrayContent(body) };
        }

        private sealed class RequestReader(NetworkStream stream, CancellationToken cancellationToken)
        {
            private readonly byte[] _single = new byte[1];
            private int _headerBytes;
            public async Task<string> ReadLineAsync()
            {
                using var line = new MemoryStream();
                while (true)
                {
                    if (++_headerBytes > MaximumHeaderBytes) throw new InvalidDataException("Fixture request exceeded its header limit.");
                    if (await stream.ReadAsync(_single, 0, 1, cancellationToken) != 1) throw new EndOfStreamException("Incomplete fixture HTTP line.");
                    if (_single[0] == '\r')
                    {
                        if (++_headerBytes > MaximumHeaderBytes || await stream.ReadAsync(_single, 0, 1, cancellationToken) != 1 || _single[0] != '\n')
                            throw new InvalidDataException("Invalid fixture HTTP line ending.");
                        return Encoding.ASCII.GetString(line.ToArray());
                    }
                    if (_single[0] < 32 || _single[0] > 126) throw new InvalidDataException("Invalid fixture HTTP header character.");
                    line.WriteByte(_single[0]);
                }
            }
            public async Task<byte[]> ReadBytesAsync(int count)
            {
                var bytes = new byte[count];
                for (var offset = 0; offset < count;)
                {
                    var received = await stream.ReadAsync(bytes, offset, count - offset, cancellationToken);
                    if (received == 0) throw new EndOfStreamException("Incomplete fixture HTTP body.");
                    offset += received;
                }
                return bytes;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            try { await WithDeadline(_requests); }
            finally { _stopping.Dispose(); }
        }
    }

    private sealed class FixtureServer : HttpMessageHandler
    {
        public string? CalledName { get; private set; }
        public string? Input { get; private set; }
        public bool Disposed { get; private set; }
        public int SessionDeletes { get; private set; }
        public bool BlockCalls { get; set; }
        public TaskCompletionSource<bool> CallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken) => SendAsync(request, cancellationToken);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) { SessionDeletes++; return EmptyResponse(HttpStatusCode.NoContent); }
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id)) return EmptyResponse(HttpStatusCode.Accepted);
            var method = root.GetProperty("method").GetString();
            object result;
            if (method == "initialize")
                result = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "compatibility-fixture", version = "1.0" } };
            else if (method == "tools/list")
                result = new { tools = new[] { new { name = "echo", description = "Echo an input", inputSchema = new { type = "object" }, outputSchema = new { type = "object", required = new[] { "value" } } } } };
            else if (method == "tools/call")
            {
                if (BlockCalls)
                {
                    CallStarted.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                CalledName = root.GetProperty("params").GetProperty("name").GetString();
                Input = root.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString();
                result = new { content = new[] { new { type = "text", text = Input } }, structuredContent = new { value = Input } };
            }
            else throw new InvalidOperationException("Unexpected MCP request: " + method);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json")
            };
            response.Headers.Add("Mcp-Session-Id", "compatibility-session");
            return response;
        }

        private static HttpResponseMessage EmptyResponse(HttpStatusCode status) => new(status)
        {
            // Real HTTP handlers supply empty content. Framework's synthetic response does not;
            // the official MCP transport inspects Content.Headers even on notification acknowledgements.
            Content = new ByteArrayContent(Array.Empty<byte>())
        };

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class ToolModel : IModel
    {
        private int _turn;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (_turn++ == 0)
            {
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("request-1", "remote_echo", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{\"value\":\"Café\"}");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                var result = Assert.IsType<ToolResultBlock>(Assert.Single(request.Messages[2].Content));
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: result.Content);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            await Task.CompletedTask;
        }
    }
}
