using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AClientToolTests
{
    [Fact]
    public async Task EndpointAuthorizationUsesExactConfiguredKeysBeforeAnyNetworkAccess()
    {
        var handler = new Handler(); using var http = new HttpClient(handler);
        var client = new A2AClient(http, new(new Uri("http://allowed/")));
        var tool = new A2AClientTool(new() { AllowedEndpoints = new Dictionary<string, A2AClient> { ["http://allowed/"] = client } });
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() });
        var input = JsonSerializer.SerializeToElement(new { operation = "discover", endpoint = "http://allowed" });

        var error = await Assert.ThrowsAsync<A2AClientToolException>(async () => { await tool.InvokeResultAsync(input, Context(native, tool, input)); });

        Assert.Contains("allowed endpoints", error.Message); Assert.Equal(0, handler.Calls); Assert.Equal("a2a_client", tool.Spec.Name);
    }
    [Fact]
    public async Task DiscoveryRunsThroughTheNativeRichToolPathAndRetainsRawCardFields()
    {
        var card = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(A2AClientTests.Card)!;
        card["extension"] = JsonSerializer.SerializeToElement(new { custom = 42 });
        var handler = new Handler(JsonSerializer.Serialize(card)); using var http = new HttpClient(handler);
        var tool = new A2AClientTool(new() { AllowedEndpoints = new Dictionary<string, A2AClient> { ["http://allowed/"] = new(http, new(new Uri("http://allowed/"))) } });
        var native = new Agent(new() { Model = new A2AServerTests.EchoModel(), Tools = [tool] });
        var input = JsonSerializer.SerializeToElement(new { operation = "discover", endpoint = "http://allowed/" });
        try
        {
            var result = await native.InvokeToolAsync(new("call", tool.Spec.Name, input));

            Assert.Equal(ToolResultStatus.Success, result.Status); Assert.Equal("call", result.ToolUseId);
            Assert.Equal(42, result.StructuredContent!.Value.GetProperty("extension").GetProperty("custom").GetInt32());
            Assert.Equal("Remote researcher", Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json.GetProperty("name").GetString());
            Assert.Equal(1, handler.Calls);
        }
        finally { await native.DisposeAsync(); }
        Assert.False(handler.Disposed);
    }
    [Fact]
    public async Task SendMessageUsesRealHttpAndStartsAFreshRemoteTaskForEveryToolInvocation()
    {
        var agents = new Dictionary<string, Agent>();
        await using var executor = new A2AExecutor(new()
        {
            AgentFactory = (id, _) => { var agent = new Agent(new() { Model = new A2AServerTests.EchoModel() }); agents.Add(id, agent); return new(agent); }
        });
        await using var host = new A2AServerTcpFixture(uri => new(executor, A2AServerTests.Options() with { Url = new(uri, "rpc") }));
        using var handler = new HttpClientHandler { UseProxy = false }; using var http = new HttpClient(handler);
        var tool = new A2AClientTool(new() { AllowedEndpoints = new Dictionary<string, A2AClient> { [host.BaseUri.AbsoluteUri] = new(http, new(host.BaseUri)) } });
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() });
        foreach (var message in new[] { "one", "two" })
        {
            var input = JsonSerializer.SerializeToElement(new { operation = "send_message", endpoint = host.BaseUri.AbsoluteUri, message });

            var result = await tool.InvokeResultAsync(input, Context(native, tool, input));

            var output = result.StructuredContent!.Value.GetProperty("message");
            Assert.Equal("assistant", output.GetProperty("role").GetString());
            Assert.Equal(message, output.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.False(output.TryGetProperty("metadata", out _)); Assert.False(output.TryGetProperty("trackingId", out _));
        }
        Assert.Equal(2, agents.Count); Assert.All(agents.Values, agent => Assert.Equal(2, agent.Messages.Count));
    }
    [Theory]
    [InlineData("submitted")]
    [InlineData("working")]
    [InlineData("input-required")]
    [InlineData("auth-required")]
    [InlineData("canceled")]
    [InlineData("failed")]
    [InlineData("rejected")]
    [InlineData("unknown")]
    public async Task EveryNoncompletedRemoteTaskIsAToolFailureRatherThanSuccessfulAnswerText(string state)
    {
        using var http = new HttpClient(new TaskHandler(state)); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var input = Input("send_message", "http://allowed/", "run");
        var error = await Assert.ThrowsAsync<A2AClientToolException>(async () => { await tool.InvokeResultAsync(input, Context(native, tool, input)); });
        Assert.Contains(state, error.Message); Assert.Contains("detail", error.Message);
    }
    [Theory]
    [InlineData("{\"operation\":\"other\",\"endpoint\":\"http://allowed/\"}")]
    [InlineData("{\"operation\":\"send_message\",\"endpoint\":\"http://allowed/\"}")]
    [InlineData("{\"operation\":\"send_message\",\"endpoint\":\"http://allowed/\",\"message\":null}")]
    [InlineData("{\"operation\":\"send_message\",\"endpoint\":\"http://allowed/\",\"message\":\"\"}")]
    [InlineData("{\"operation\":\"send_message\",\"endpoint\":\"http://allowed/\",\"message\":42}")]
    [InlineData("{\"operation\":\"discover\",\"endpoint\":42}")]
    public async Task InvalidToolRequestsFailBeforeNetworkAccess(string json)
    {
        var handler = new Handler(); using var http = new HttpClient(handler); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel(), Tools = [tool] });
        var result = await native.InvokeToolAsync(new("call", tool.Spec.Name, JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task EndpointBindingsAreCopiedAndCustomMetadataDoesNotChangeAuthority()
    {
        var first = new Handler(); var second = new Handler(); using var firstHttp = new HttpClient(first); using var secondHttp = new HttpClient(second);
        var bindings = new Dictionary<string, A2AClient> { ["http://allowed/"] = new(firstHttp, new(new Uri("http://allowed/"))) };
        var tool = new A2AClientTool(new() { Name = "remote", Description = "Custom", AllowedEndpoints = bindings });
        bindings.Clear(); bindings["http://different/"] = new(secondHttp, new(new Uri("http://different/")));
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var allowed = Input("discover", "http://allowed/");
        await tool.InvokeResultAsync(allowed, Context(native, tool, allowed)); var denied = Input("discover", "http://different/");
        await Assert.ThrowsAsync<A2AClientToolException>(async () => { await tool.InvokeResultAsync(denied, Context(native, tool, denied)); });
        Assert.Equal(1, first.Calls); Assert.Equal(0, second.Calls); Assert.Equal("remote", tool.Spec.Name); Assert.Equal("Custom", tool.Spec.Description);
    }
    [Fact]
    public async Task CancellationBeforeAdmissionRetainsTheCallerTokenAndPerformsNoNetworkAccess()
    {
        var handler = new Handler(); using var http = new HttpClient(handler); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var input = Input("discover", "http://allowed/");
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await tool.InvokeResultAsync(input, Context(native, tool, input), caller.Token); });
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task ProtocolFailuresRemainAvailableAsTheToolErrorsOriginalCause()
    {
        using var http = new HttpClient(new TaskHandler("completed", error: true)); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var input = Input("send_message", "http://allowed/", "run");
        var error = await Assert.ThrowsAsync<A2AClientToolException>(async () => { await tool.InvokeResultAsync(input, Context(native, tool, input)); });
        Assert.Equal(-32001, Assert.IsType<A2ARpcException>(error.InnerException).Code);
    }
    [Fact]
    public async Task RichRemoteDataRemainsTypedJsonInsideTheReturnedNativeMessage()
    {
        using var http = new HttpClient(new TaskHandler("completed", data: true)); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var input = Input("send_message", "http://allowed/", "run");
        var output = JsonSerializer.Deserialize<JsonElement>(await tool.InvokeAsync(input, Context(native, tool, input)));
        Assert.Equal(42, output.GetProperty("message").GetProperty("content")[0].GetProperty("json").GetProperty("answer").GetInt32());
    }
    [Fact]
    public async Task EveryEndpointUsesItsOwnBorrowedHttpAuthenticationConfiguration()
    {
        var first = new AuthorizationHandler("first"); var second = new AuthorizationHandler("second");
        using var firstHttp = new HttpClient(first); using var secondHttp = new HttpClient(second);
        firstHttp.DefaultRequestHeaders.Authorization = new("Bearer", "first"); secondHttp.DefaultRequestHeaders.Authorization = new("Bearer", "second");
        var tool = new A2AClientTool(new()
        {
            AllowedEndpoints = new Dictionary<string, A2AClient>
            {
                ["http://first/"] = new(firstHttp, new(new Uri("http://first/"))),
                ["http://second/"] = new(secondHttp, new(new Uri("http://second/")))
            }
        });
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() });
        foreach (var endpoint in new[] { "http://first/", "http://second/" })
        {
            var input = Input("discover", endpoint);
            await tool.InvokeResultAsync(input, Context(native, tool, input));
        }
        Assert.Equal(1, first.Calls); Assert.Equal(1, second.Calls);
    }
    [Fact]
    public async Task BodyCancellationPreservesTheCallerTokenAndClosesTheBorrowedClientsOwnedResponse()
    {
        var body = new PausingBody(); using var http = new HttpClient(new BodyHandler(body)); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var input = Input("send_message", "http://allowed/", "run");
        using var caller = new CancellationTokenSource();
        var operation = tool.InvokeResultAsync(input, Context(native, tool, input), caller.Token).AsTask();
        await A2AWireTests.WithDeadline(body.ReadStarted.Task);

        caller.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => A2AWireTests.WithDeadline(operation));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.True(body.Disposed);
    }
    [Fact]
    public async Task WrappedRemoteFailureRetainsItsResponseCleanupDiagnostics()
    {
        var cleanup = new IOException("response cleanup");
        var handler = new A2AErrorTests.Handler(id => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32001, message = "gone" } }), disposeFailure: cleanup);
        using var http = new HttpClient(handler); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var input = Input("send_message", "http://allowed/", "run");

        var error = await Assert.ThrowsAsync<A2AClientToolException>(async () => { await tool.InvokeResultAsync(input, Context(native, tool, input)); });

        var cause = Assert.IsType<A2ARpcException>(error.InnerException);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(cause.Data["Strands.CleanupErrors"])));
        Assert.True(handler.LastContent!.Disposed);
    }
    [Fact]
    public async Task CancellationAcceptedDuringDiscoveryResponseCleanupWinsOverSuccessfulCardProjection()
    {
        using var caller = new CancellationTokenSource();
        using var http = new HttpClient(new CleanupCancellationHandler(caller)); var tool = Tool(http);
        await using var native = new Agent(new() { Model = new A2AServerTests.EchoModel() }); var input = Input("discover", "http://allowed/");

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await tool.InvokeResultAsync(input, Context(native, tool, input), caller.Token); });

        Assert.Equal(caller.Token, error.CancellationToken);
    }
    private static A2AClientTool Tool(HttpClient http) => new(new() { AllowedEndpoints = new Dictionary<string, A2AClient> { ["http://allowed/"] = new(http, new(new Uri("http://allowed/"))) } });
    private static JsonElement Input(string operation, string endpoint, string? message = null) => JsonSerializer.SerializeToElement(new { operation, endpoint, message });
    internal static ToolContext Context(Agent agent, A2AClientTool tool, JsonElement input) => new(agent, new("call", tool.Spec.Name, input), new Dictionary<string, object?>());
    internal sealed class Handler(string? card = null) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(card ?? A2AClientTests.Card, Encoding.UTF8, "application/json") }); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TaskHandler(string state, bool error = false, bool data = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(A2AClientTests.Card.Replace("\"streaming\":true", "\"streaming\":false"), Encoding.UTF8, "application/json") };
            var parameters = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            var id = parameters.GetProperty("id").GetString();
            object response = error ? new { jsonrpc = "2.0", id, error = new { code = -32001, message = "missing" } } : new
            {
                jsonrpc = "2.0",
                id,
                result = new
                {
                    kind = "task",
                    id = "task",
                    contextId = "context",
                    status = new { state, message = new { kind = "message", messageId = "reply", role = "agent", parts = new[] { new { kind = "text", text = "detail" } } } },
                    artifacts = data ? new[] { new { artifactId = "data", parts = new[] { new { kind = "data", data = new { answer = 42 } } } } } : []
                }
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class AuthorizationHandler(string expected) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(expected, request.Headers.Authorization.Parameter); Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(A2AClientTests.Card, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class CleanupCancellationHandler(CancellationTokenSource caller) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new CleanupCancellationContent(caller) });
    }
    private sealed class CleanupCancellationContent(CancellationTokenSource caller) : StringContent(A2AClientTests.Card, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing) { if (disposing) caller.Cancel(); base.Dispose(disposing); }
    }
    private sealed class BodyHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(A2AClientTests.Card, Encoding.UTF8, "application/json") });
            var content = new StreamContent(body); content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class PausingBody : Stream
    {
        internal TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { ReadStarted.TrySetResult(true); return _read.Task; }
        protected override void Dispose(bool disposing) { Disposed = true; _read.TrySetException(new ObjectDisposedException(nameof(PausingBody))); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
