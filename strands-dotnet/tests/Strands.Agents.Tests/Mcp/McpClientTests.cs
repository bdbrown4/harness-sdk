using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpClientTests
{
    [Fact]
    public async Task DiscoveryPreservesSchemasOpaqueAnnotationsAndServerNames()
    {
        using var server = new FixtureServer();
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        var tools = await client.ListToolsAsync(new McpToolListOptions { Prefix = "remote" });

        var tool = Assert.Single(tools);
        Assert.Equal("remote_lookup", tool.Spec.Name);
        Assert.Equal("lookup", tool.ServerName);
        Assert.Equal("untrusted", tool.Annotations!.Value.GetProperty("vendorHint").GetString());
        Assert.Equal("object", tool.Spec.InputSchema.GetProperty("type").GetString());
        Assert.Equal("object", tool.OutputSchema!.Value.GetProperty("type").GetString());
        Assert.True(server.Initialized);
        Assert.False(server.Disposed);
    }

    [Fact]
    public async Task MalformedDefinitionFailsWithAnActionableProtocolError()
    {
        using var server = new FixtureServer { Handle = _ => new { tools = new[] { new { name = "broken" } } } };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListToolsAsync());

        Assert.Contains("inputSchema", error.Message);
    }

    [Fact]
    public async Task AdaptedToolsForwardProgressNotifications()
    {
        using var server = new FixtureServer();
        var progressSink = new ProgressSink();
        server.Respond = root =>
        {
            if (root.GetProperty("method").GetString() != "tools/call") return null;
            var metadata = root.GetProperty("params");
            var token = metadata.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("progressToken", out var value) ? value : JsonSerializer.SerializeToElement("absent");
            var progress = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = token, progress = 1, total = 2, message = "halfway" } });
            var result = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id"), result = new { content = new[] { new { type = "text", text = "found" } }, structuredContent = new { value = "found" } } });
            var content = new StreamContent(new ProgressStream("event: message\ndata: " + progress + "\n\n", "event: message\ndata: " + result + "\n\n", progressSink.Observed.Task));
            content.Headers.ContentType = new("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        using var http = new HttpClient(server);
        await using var client = await ConnectAsync(http);
        var tool = Assert.Single(await client.ListToolsAsync(new McpToolListOptions { Progress = progressSink }));
        var input = JsonSerializer.SerializeToElement(new { });
        var agent = new Agent(new AgentOptions { Model = new AgentTests.ScriptedModel(AgentTests.Text("unused")) });

        var result = await tool.InvokeResultAsync(input, new ToolContext(agent, new ToolUseBlock("call", tool.Spec.Name, input), new Dictionary<string, object?>())).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("found", result.Content);
        var observed = await progressSink.Observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("halfway", observed.Message);
        Assert.Equal(1, observed.Progress);
        Assert.Equal(2, observed.Total);
    }

    private sealed class ProgressSink : IProgress<ProgressNotificationValue>
    {
        public TaskCompletionSource<ProgressNotificationValue> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(ProgressNotificationValue value) => Observed.TrySetResult(value);
    }

    private sealed class ProgressStream(string first, string final, Task progressObserved) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
        private readonly byte[] _final = Encoding.UTF8.GetBytes(final);
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_position < _first.Length)
            {
                var length = Math.Min(count, _first.Length - _position);
                Array.Copy(_first, _position, buffer, offset, length);
                _position += length;
                return length;
            }
            await progressObserved.WaitAsync(cancellationToken);
            var finalPosition = _position - _first.Length;
            var remaining = Math.Min(count, _final.Length - finalPosition);
            Array.Copy(_final, finalPosition, buffer, offset, remaining);
            _position += remaining;
            return remaining;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal static Task<Client> ConnectAsync(HttpClient http) => Client.CreateHttpAsync(http,
        new HttpClientTransportOptions { Endpoint = new Uri("https://mcp.example.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) });

    internal sealed class FixtureServer : HttpMessageHandler
    {
        public bool Initialized { get; private set; }
        public bool Disposed { get; private set; }
        public int SessionDeletes { get; private set; }
        public List<JsonElement> Requests { get; } = [];
        public Func<JsonElement, object?>? Handle { get; set; }
        public Func<JsonElement, HttpResponseMessage?>? Respond { get; set; }
        public Func<JsonElement, CancellationToken, Task<HttpResponseMessage?>>? RespondAsync { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) { SessionDeletes++; return new(HttpStatusCode.NoContent); }
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement.Clone();
            Requests.Add(root);
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized") Initialized = true;
            if (!root.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            if (Respond?.Invoke(root) is { } customResponse) return customResponse;
            if (RespondAsync is not null && await RespondAsync(root, cancellationToken) is { } asyncResponse) return asyncResponse;
            object? result = method switch
            {
                "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { listChanged = true } }, serverInfo = new { name = "fixture", version = "1.0" } },
                "tools/list" => Handle?.Invoke(root) ?? new
                {
                    tools = new[] { new { name = "lookup", description = "Look up an item", inputSchema = new { type = "object" }, outputSchema = new { type = "object" }, annotations = new { readOnlyHint = true, vendorHint = "untrusted" } } }
                },
                "tools/call" => Handle?.Invoke(root) ?? new { content = new[] { new { type = "text", text = "found" } }, structuredContent = new { value = "found" } },
                "ping" => new { },
                _ => throw new InvalidOperationException("Unexpected method: " + method)
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), Encoding.UTF8, "application/json")
            };
            response.Headers.Add("Mcp-Session-Id", "fixture-session");
            return response;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
