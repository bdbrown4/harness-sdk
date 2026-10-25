using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AErrorTests
{
    [Theory]
    [InlineData(-32000)]
    [InlineData(-32001)]
    public async Task StreamingRequestsPreserveImmediateJsonRpcErrorsReturnedAsJson(int code)
    {
        var handler = new Handler(id => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message = "remote error", data = new { retryable = false } } }));
        using var http = new HttpClient(handler);
        var iterator = new A2AClient(http, new(new("https://agent.test/"))).StreamCallAsync("message/stream").GetAsyncEnumerator();
        try
        {
            var error = await Assert.ThrowsAsync<A2ARpcException>(async () => { await iterator.MoveNextAsync(); });
            Assert.Equal(code, error.Code); Assert.False(error.ErrorData!.Value.GetProperty("retryable").GetBoolean());
            Assert.True(handler.LastContent!.Disposed);
        }
        finally { await iterator.DisposeAsync(); }
    }
    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"other\",\"error\":{\"code\":-32000,\"message\":\"error\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"error\":{\"code\":\"bad\",\"message\":\"error\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{},\"error\":{\"code\":-32000,\"message\":\"error\"}}")]
    public async Task StreamingJsonFallbackOnlyAcceptsValidCorrelatedErrorEnvelopes(string payload)
    {
        var handler = new Handler(id => payload.Replace("$id", id)); using var http = new HttpClient(handler);
        var iterator = new A2AClient(http, new(new("https://agent.test/"))).StreamCallAsync("message/stream").GetAsyncEnumerator();
        try { await Assert.ThrowsAsync<A2AProtocolException>(async () => { await iterator.MoveNextAsync(); }); Assert.True(handler.LastContent!.Disposed); }
        finally { await iterator.DisposeAsync(); }
    }
    [Fact]
    public async Task ImmediateJsonRpcErrorRemainsPrimaryWhenResponseCleanupAlsoFails()
    {
        var cleanup = new IOException("dispose");
        var handler = new Handler(id => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32000, message = "remote" } }), disposeFailure: cleanup);
        using var http = new HttpClient(handler);
        var iterator = new A2AClient(http, new(new("https://agent.test/"))).StreamCallAsync("message/stream").GetAsyncEnumerator();
        try
        {
            var error = await Assert.ThrowsAsync<A2ARpcException>(async () => { await iterator.MoveNextAsync(); });
            Assert.Equal(-32000, error.Code); Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
        }
        finally { await iterator.DisposeAsync(); }
    }
    [Theory]
    [InlineData("\"bad\"")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task MalformedRpcErrorCodesRemainProtocolExceptions(string code)
    {
        using var http = new HttpClient(new Handler(id => "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"error\":{\"code\":" + code + ",\"message\":\"invalid\"}}"));
        var client = new A2AClient(http, new(new("https://agent.test/")));
        await Assert.ThrowsAsync<A2AProtocolException>(() => client.CallAsync("tasks/get", JsonSerializer.SerializeToElement(new { id = "task" })));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":\"$id\",\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"different\",\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":null,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{},\"error\":{\"code\":-32603,\"message\":\"error\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"$id\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{},\"result\":{}}")]
    public async Task MalformedOrAmbiguousJsonRpcEnvelopesFailAndCloseTheirResponse(string payload)
    {
        var handler = new Handler(id => payload.Replace("$id", id));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<A2AProtocolException>(() => new A2AClient(http, new(new("https://agent.test/"))).CallAsync("tasks/get"));
        Assert.True(handler.LastContent!.Disposed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("data: {invalid}\n\n")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{}}")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{\"kind\":\"task\",\"id\":\"task\",\"contextId\":\"context\",\"status\":{\"state\":\"working\"}}}\n\n")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{\"kind\":\"status-update\",\"taskId\":\"task\",\"contextId\":\"context\",\"status\":{\"state\":\"working\"},\"final\":true}}\n\n")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{\"kind\":\"artifact-update\",\"taskId\":\"task\",\"contextId\":\"context\",\"artifact\":{\"artifactId\":\"a\",\"parts\":[]},\"lastChunk\":true}}\n\n")]
    [InlineData("data: {\"jsonrpc\":\"2.0\",\"id\":\"$id\",\"result\":{\"kind\":\"message\",\"messageId\":\"reply\",\"role\":\"user\",\"parts\":[]}}\n\n")]
    public async Task IncompleteMalformedAndNonfinalStreamsCannotBecomeSuccessfulAgentResults(string payload)
    {
        var handler = new Handler(id => payload.Replace("$id", id), true);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<A2AProtocolException>(() => new A2AAgent(new(http, new(new("https://agent.test/")))).InvokeAsync("Question"));
        Assert.True(handler.LastContent!.Disposed);
    }

    [Fact]
    public async Task ArtifactAndStatusTaskIdentifiersCannotChangeWithinOneResponse()
    {
        var handler = new Handler(id => A2AAgentTests.Frame(id, new { kind = "task", id = "first", contextId = "context", status = new { state = "working" } }) +
            A2AAgentTests.Frame(id, new { kind = "status-update", taskId = "second", contextId = "context", status = new { state = "completed" }, final = true }), true);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<A2AProtocolException>(() => new A2AAgent(new(http, new(new("https://agent.test/")))).InvokeAsync("Question"));
        Assert.True(handler.LastContent!.Disposed);
    }

    [Theory]
    [InlineData(-32700)]
    [InlineData(-32600)]
    public async Task ProtocolPermittedNullIdsRetainParseAndInvalidRequestErrorCodes(int code)
    {
        using var http = new HttpClient(new Handler(_ => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = (string?)null, error = new { code, message = "invalid request" } })));
        var error = await Assert.ThrowsAsync<A2ARpcException>(() => new A2AClient(http, new(new("https://agent.test/"))).CallAsync("tasks/get"));
        Assert.Equal(code, error.Code);
    }

    internal sealed class Handler(Func<string, string> reply, bool streaming = false, Exception? disposeFailure = null) : HttpMessageHandler
    {
        internal TrackedContent? LastContent { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(A2AClientTests.Card, Encoding.UTF8, "application/json") };
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            LastContent = new(reply(document.RootElement.GetProperty("id").GetString()!), streaming ? "text/event-stream" : "application/json", disposeFailure);
            return new(HttpStatusCode.OK) { Content = LastContent };
        }
    }
    internal sealed class TrackedContent(string text, string mediaType, Exception? failure) : StringContent(text, Encoding.UTF8, mediaType)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        { var first = !Disposed; Disposed = true; base.Dispose(disposing); if (first && failure is not null) throw failure; }
    }
}
