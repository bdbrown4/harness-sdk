using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class HttpRequestTests
{
    [Fact]
    public async Task PatchSendsExactBodyAndLateBorrowedAuthorizationOverRealHttp()
    {
        await using var server = new RawServer();
        using var client = new HttpClient();
        var tool = HttpRequest.Create(new() { HttpClient = client });
        client.DefaultRequestHeaders.Add("Authorization", "Bearer late-value");
        using var result = JsonDocument.Parse(await InvokeAsync(tool, new { method = "PATCH", url = server.Endpoint, body = "{\"literal\":true}", headers = new Dictionary<string, string> { ["X-Test"] = "request" } }));
        var request = await Wait(server.Request);
        Assert.StartsWith("PATCH /raw HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer late-value\r\n", request, StringComparison.Ordinal);
        Assert.EndsWith("{\"literal\":true}", request, StringComparison.Ordinal);
        Assert.Equal(200, result.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("actual response", result.RootElement.GetProperty("body").GetString());
        Assert.Equal("Fixture", result.RootElement.GetProperty("statusText").GetString());
    }

    [Fact]
    public async Task PythonPreservesRepeatedHeadersAndDeclaredCharset()
    {
        using var client = new HttpClient(new ResponseHandler(() => Response(200, new byte[] { 0xe9 })));
        var tool = HttpRequest.Create(new() { HttpClient = client, Behavior = HttpRequestBehavior.Python });
        using var result = JsonDocument.Parse(await InvokeAsync(tool, new { method = "GET", url = "http://fixture.test/raw" }));
        Assert.Equal("é", result.RootElement.GetProperty("body").GetString());
        Assert.Equal("first=1\nsecond=2", result.RootElement.GetProperty("headers").GetProperty("set-cookie").GetString());
        Assert.True(result.RootElement.TryGetProperty("status_text", out _));
        Assert.False(result.RootElement.TryGetProperty("statusText", out _));
    }

    [Fact]
    public async Task TypeScriptUsesUtf8AndLastSetCookieAsThePinnedNodeLoopDoes()
    {
        using var client = new HttpClient(new ResponseHandler(() => Response(200, new byte[] { 0xef, 0xbb, 0xbf, 0xc3, 0xa9 })));
        var tool = HttpRequest.Create(new() { HttpClient = client });
        using var result = JsonDocument.Parse(await InvokeAsync(tool, new { method = "GET", url = "http://fixture.test/raw" }));
        Assert.Equal("é", result.RootElement.GetProperty("body").GetString());
        Assert.Equal("second=2", result.RootElement.GetProperty("headers").GetProperty("set-cookie").GetString());
    }

    [Fact]
    public async Task TypeScriptReadsTheBodyBeforeRejectingAnUnfollowedRedirect()
    {
        var content = new CountingContent();
        using var client = new HttpClient(new ResponseHandler(() => new((HttpStatusCode)301) { Content = content }));
        var tool = HttpRequest.Create(new() { HttpClient = client });
        await Assert.ThrowsAsync<HttpRequestException>(() => InvokeAsync(tool, new { method = "GET", url = "http://fixture.test/raw" }));
        Assert.Equal(1, content.Opens);
        Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task PythonReturnsAnUnfollowedRedirect()
    {
        using var client = new HttpClient(new ResponseHandler(() => Response(301, Encoding.UTF8.GetBytes("redirect"))));
        using var result = JsonDocument.Parse(await InvokeAsync(HttpRequest.Create(new() { HttpClient = client, Behavior = HttpRequestBehavior.Python }), new { method = "GET", url = "http://fixture.test/raw" }));
        Assert.Equal(301, result.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("redirect", result.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task PythonRejectsServerErrorsWithoutOpeningTheBody()
    {
        var content = new CountingContent();
        using var client = new HttpClient(new ResponseHandler(() => new(HttpStatusCode.InternalServerError) { Content = content }));
        await Assert.ThrowsAsync<HttpRequestException>(() => InvokeAsync(HttpRequest.Create(new() { HttpClient = client, Behavior = HttpRequestBehavior.Python }), new { method = "GET", url = "http://fixture.test/raw" }));
        Assert.Equal(0, content.Opens);
        Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task CallerCancellationJoinsLateContentOpenAndClosesItsStreamOnce()
    {
        var release = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new CountingContent(release.Task);
        var stream = new DisposalStream();
        using var client = new HttpClient(new ResponseHandler(() => new(HttpStatusCode.OK) { Content = content }));
        using var cancellation = new CancellationTokenSource();
        var operation = InvokeAsync(HttpRequest.Create(new() { HttpClient = client }), new { method = "GET", url = "http://fixture.test/raw" }, cancellation.Token);
        Exception? primary = null;
        try
        {
            var first = await Wait(Task.WhenAny(content.Entered.Task, operation));
            if (ReferenceEquals(first, operation)) await operation;
            await Wait(content.Entered.Task);
            cancellation.Cancel();
            Assert.False(operation.IsCompleted);
            Assert.Equal(0, stream.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(stream);
            if (primary is not null)
            {
                try { await operation; }
                catch (Exception error) when (ReferenceEquals(error, primary)) { }
            }
        }
        var cancellationFailure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(operation));
        Assert.Equal(cancellation.Token, cancellationFailure.CancellationToken);
        Assert.Equal(1, stream.Disposals);
    }

    [Fact]
    public async Task AssembledHarnessExplicitSelectionReachesHttpWithoutChangingDefaultMembership()
    {
        var defaults = new[] { "shell", "read", "write", "edit", "web_fetch", "web_search", "programmatic_tool_caller", "subagent" };
        Assert.Equal(defaults, Harness.BuiltinToolNames);
        await using var server = new RawServer();
        await using var agent = await Harness.CreateAsync(new()
        {
            UseEnvironmentTelemetry = false,
            Model = new HttpModel(server.Endpoint),
            BuiltinTools = ["http_request"],
            BuiltinPlugins = [],
            BackgroundTasks = null,
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Session = new() { Enabled = false }
        });
        var result = await agent.InvokeAsync("Call the explicitly enabled HTTP tool.");
        Assert.Contains("actual response", result.ToString(), StringComparison.Ordinal);
        Assert.Single(agent.Tools);
    }

    private static HttpResponseMessage Response(int status, byte[] bytes)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=iso-8859-1");
        response.Headers.TryAddWithoutValidation("Set-Cookie", new[] { "first=1", "second=2" });
        return response;
    }

    internal static async Task<string> InvokeAsync(ITool tool, object input, CancellationToken cancellationToken = default)
    {
        await using var agent = new Agent(new() { Model = new HttpModel("http://unused.test/") });
        var json = JsonSerializer.SerializeToElement(input);
        return await tool.InvokeAsync(json, new(agent, new("direct", tool.Spec.Name, json), new Dictionary<string, object?>()), cancellationToken);
    }

    private static async Task Wait(Task operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10))));
        await operation;
    }
    private static async Task<T> Wait<T>(Task<T> operation) { await Wait((Task)operation); return await operation; }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }
    private sealed class CountingContent(Task<Stream>? opened = null) : HttpContent
    {
        internal int Opens, Disposals;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<Stream> CreateContentReadStreamAsync() { Opens++; Entered.TrySetResult(true); return opened ?? Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("body"))); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class DisposalStream : MemoryStream
    {
        internal int Disposals;
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref Disposals); base.Dispose(disposing); }
    }
    private sealed class HttpModel(string url) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            var result = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().LastOrDefault();
            yield return new ModelMessageStartEvent();
            if (result is null)
            {
                yield return new ModelContentBlockStartEvent(0, new("raw-http", "http_request", JsonSerializer.SerializeToElement(new { method = "GET", url })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: result.Content);
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }

    internal sealed class RawServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task<string> _request;
        private TcpClient? _client;
        internal string Endpoint { get; }
        internal Task<string> Request => _request;
        internal RawServer()
        {
            _listener.Start();
            Endpoint = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/raw";
            _request = ServeAsync();
        }
        private async Task<string> ServeAsync()
        {
            _client = await _listener.AcceptTcpClientAsync();
            using (_client)
            using (var stream = _client.GetStream())
            {
                var bytes = new List<byte>(); var one = new byte[1];
                while (bytes.Count < 65536)
                {
                    if (await stream.ReadAsync(one, 0, 1) == 0) throw new IOException("Incomplete request.");
                    bytes.Add(one[0]);
                    if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.Skip(bytes.Count - 4).ToArray()) == "\r\n\r\n") break;
                }
                var header = Encoding.ASCII.GetString(bytes.ToArray());
                var contentLength = header.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var remaining = contentLength is null ? 0 : int.Parse(contentLength.Substring(contentLength.IndexOf(':') + 1).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var body = new byte[remaining]; var offset = 0;
                while (offset < body.Length) { var read = await stream.ReadAsync(body, offset, body.Length - offset); if (read == 0) throw new IOException("Incomplete body."); offset += read; }
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 Fixture\r\nContent-Type: text/plain\r\nContent-Length: 15\r\nConnection: close\r\n\r\nactual response");
                await stream.WriteAsync(response, 0, response.Length);
                return header + Encoding.UTF8.GetString(body);
            }
        }
        public async ValueTask DisposeAsync()
        {
            _listener.Stop(); _client?.Dispose();
            try { await _request; }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }
    }
}
