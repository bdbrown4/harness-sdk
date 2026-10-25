using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ARestClientTests
{
    [Fact]
    public async Task RestOnlyAgentUsesTheAdvertisedBaseAndProtobufJsonWithoutRpcEnvelopes()
    {
        Uri? endpoint = null;
        JsonElement sent = default;
        await using var server = new A2ATestServer(request =>
        {
            if (request.Method == "GET") return new(Card(endpoint!, streaming: false));
            Assert.Equal("POST", request.Method);
            Assert.Equal("/agents/rest/v1/message:send", request.Path);
            using var body = JsonDocument.Parse(request.Body);
            sent = body.RootElement.Clone();
            AssertRestMessage(sent, "typed request");
            Assert.Equal(42, sent.GetProperty("message").GetProperty("content")[1].GetProperty("data").GetProperty("data").GetProperty("answer").GetInt32());
            Assert.Equal("context/雪", sent.GetProperty("message").GetProperty("contextId").GetString());
            Assert.Equal("request-id", sent.GetProperty("message").GetProperty("messageId").GetString());
            Assert.Equal(2, sent.GetProperty("configuration").GetProperty("historyLength").GetInt32());
            Assert.True(sent.GetProperty("metadata").GetProperty("extension").GetBoolean());
            return new("""{"message":{"messageId":"response","contextId":"context/雪","role":"ROLE_AGENT","content":[{"text":"Résumé 🐈"},{"data":{"data":{"accepted":true}}}]}}""", Chunked: true, FragmentSize: 1);
        });
        endpoint = new(server.BaseUri, "agents/rest/");
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler, disposeHandler: false);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-credential");
        var state = new Dictionary<string, object?> { ["local-only"] = new object() };
        var agent = new A2AAgent(new(http, new(server.BaseUri)), new() { Streaming = false });
        using var cancellation = new CancellationTokenSource();

        var result = await CompleteAsync(agent.InvokeContentAsync(
            [new TextBlock("typed request"), new JsonBlock(JsonSerializer.SerializeToElement(new { answer = 42 }))],
            new()
            {
                ContextId = "context/雪",
                MessageId = "request-id",
                InvocationState = state,
                Configuration = JsonSerializer.SerializeToElement(new { historyLength = 2, blocking = true }),
                Metadata = JsonSerializer.SerializeToElement(new { extension = true })
            }, cancellation.Token), cancellation);

        Assert.Equal("Résumé 🐈", Assert.Single(result.Message!.Content.OfType<TextBlock>()).Text);
        Assert.True(Assert.Single(result.Message.Content.OfType<JsonBlock>()).Json.GetProperty("accepted").GetBoolean());
        Assert.Same(state, result.InvocationState);
        Assert.DoesNotContain("local-only", sent.GetRawText());
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.Equal("Bearer fixture-credential", request.Headers["Authorization"]));
        using var stillBorrowed = await http.GetAsync(new Uri(server.BaseUri, ".well-known/agent-card.json"));
        Assert.True(stillBorrowed.IsSuccessStatusCode);
    }

    [Fact]
    public async Task RestStreamingNormalizesTaskArtifactsAndFinalStatusForTheNativeAgent()
    {
        Uri? endpoint = null;
        await using var server = new A2ATestServer(request =>
        {
            if (request.Method == "GET") return new(Card(endpoint!, streaming: true));
            Assert.Equal("POST", request.Method);
            Assert.Equal("/agents/rest/v1/message:stream", request.Path);
            Assert.Equal("text/event-stream", request.Headers["Accept"]);
            using var body = JsonDocument.Parse(request.Body);
            AssertRestMessage(body.RootElement, "stream request");
            return new(string.Concat(
                Frame(new { task = new { id = "task/雪", contextId = "context", status = new { state = "TASK_STATE_WORKING" } } }),
                Frame(new { artifactUpdate = new { taskId = "task/雪", contextId = "context", artifact = new { artifactId = "answer", parts = new[] { new { text = "Café " } } }, append = false, lastChunk = false } }),
                Frame(new { artifactUpdate = new { taskId = "task/雪", contextId = "context", artifact = new { artifactId = "answer", parts = new[] { new { text = "雪" } } }, append = true, lastChunk = true } }),
                Frame(new { statusUpdate = new { taskId = "task/雪", contextId = "context", status = new { state = "TASK_STATE_COMPLETED" }, final = true, metadata = new { completion = "retained" } } })),
                "text/event-stream", Chunked: true, FragmentSize: 1);
        });
        endpoint = new(server.BaseUri, "agents/rest");
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler, disposeHandler: false);
        var agent = new A2AAgent(new(http, new(server.BaseUri)));
        var events = new List<IAgentStreamEvent>();
        using var cancellation = new CancellationTokenSource();

        await CompleteAsync(CollectAsync(agent.StreamAsync("stream request", cancellationToken: cancellation.Token), events), cancellation);

        var updates = events.OfType<A2AStreamUpdateEvent>().ToArray();
        Assert.Equal(new[] { "task", "artifact-update", "artifact-update", "status-update" }, updates.Select(update => update.Event.GetProperty("kind").GetString()).ToArray());
        Assert.True(updates[2].Event.GetProperty("append").GetBoolean());
        var final = Assert.Single(events.OfType<A2AResultEvent>());
        Assert.Same(final, events[events.Count - 1]);
        Assert.Equal("Café 雪", final.Result.ToString());
        Assert.Equal("completed", final.TaskState);
        Assert.Equal("task/雪", final.TaskId);
        Assert.Equal("context", final.ContextId);
        Assert.Equal("retained", final.FinalEvent.GetProperty("metadata").GetProperty("completion").GetString());
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestHttpAndSseErrorsRetainTheRemoteCodeMessageAndData(bool streaming)
    {
        const string taskId = " task/雪 ? ";
        TrackedContent? errorContent = null;
        using var handler = new Handler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/.well-known/agent-card.json")
                return Json(Card(new Uri("http://127.0.0.1/agents/rest"), streaming: true));
            if (streaming)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("http://127.0.0.1/agents/rest/v1/message:stream", request.RequestUri!.AbsoluteUri);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                AssertRestMessage(body.RootElement, "error request");
                errorContent = new("event: error\ndata: {\"code\":-32001,\"message\":\"remote task missing\",\"data\":{\"detail\":\"original\"}}\n\n", "text/event-stream");
                return new(HttpStatusCode.OK) { Content = errorContent };
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1/agents/rest/v1/tasks/" + Uri.EscapeDataString(taskId) + "?historyLength=2", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            errorContent = new("""{"code":-32001,"message":"remote task missing","data":{"detail":"original"}}""", "application/json");
            return new(HttpStatusCode.NotFound) { Content = errorContent };
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var client = new A2AClient(http, new(new Uri("http://127.0.0.1/")));
        using var cancellation = new CancellationTokenSource();
        var operation = streaming ? FirstAsync(client.StreamCallAsync("message/stream", Parameters("error request"), cancellation.Token)) : client.GetTaskAsync(taskId, historyLength: 2, cancellationToken: cancellation.Token);

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => CompleteAsync(operation, cancellation));

        Assert.Equal(-32001, error.Code);
        Assert.Equal("remote task missing", error.Message);
        Assert.Equal("original", error.ErrorData!.Value.GetProperty("detail").GetString());
        Assert.NotNull(errorContent);
        Assert.True(errorContent.Disposed);
        Assert.Equal(0, handler.Disposals);
        using var stillBorrowed = await http.GetAsync("http://127.0.0.1/.well-known/agent-card.json");
        Assert.True(stillBorrowed.IsSuccessStatusCode);
    }

    [Fact]
    public async Task CancellingARestStreamClosesItsOwnedReadAndRetainsTheCallerToken()
    {
        Uri? endpoint = null;
        var releaseBody = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new A2ATestServer(request =>
        {
            if (request.Method == "GET") return new(Card(endpoint!, streaming: true));
            Assert.Equal("POST", request.Method);
            Assert.Equal("/agents/rest/v1/message:stream", request.Path);
            return new("pending body", "text/event-stream", WaitBeforeBody: releaseBody.Task);
        });
        endpoint = new(server.BaseUri, "agents/rest");
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler, disposeHandler: false);
        using var cancellation = new CancellationTokenSource();
        var operation = new A2AAgent(new(http, new(server.BaseUri))).InvokeAsync("cancel request", cancellationToken: cancellation.Token);
        Exception? primary = null;
        try
        {
            await Within(Task.WhenAny(server.RpcHeadersSent.Task, operation));
            if (operation.IsCompleted) await operation;
            await Within(server.RpcHeadersSent.Task);

            cancellation.Cancel();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(operation));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(2, server.Requests.Count);
            using var stillBorrowed = await http.GetAsync(new Uri(server.BaseUri, ".well-known/agent-card.json"));
            Assert.True(stillBorrowed.IsSuccessStatusCode);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            releaseBody.TrySetResult(true);
            await FinishAsync(operation, cancellation, primary);
        }
    }

    private static void AssertRestMessage(JsonElement body, string text)
    {
        Assert.False(body.TryGetProperty("jsonrpc", out _));
        Assert.False(body.TryGetProperty("method", out _));
        Assert.False(body.TryGetProperty("params", out _));
        var message = body.GetProperty("message");
        Assert.Equal("ROLE_USER", message.GetProperty("role").GetString());
        Assert.False(message.TryGetProperty("kind", out _));
        Assert.False(message.TryGetProperty("parts", out _));
        Assert.Equal(text, message.GetProperty("content")[0].GetProperty("text").GetString());
    }

    private static string Card(Uri endpoint, bool streaming) => JsonSerializer.Serialize(new
    {
        name = "REST agent",
        description = "HTTP+JSON only",
        version = "1",
        protocolVersion = "0.3.0",
        url = endpoint.AbsoluteUri,
        preferredTransport = "HTTP+JSON",
        capabilities = new { streaming },
        defaultInputModes = new[] { "text/plain", "application/json" },
        defaultOutputModes = new[] { "text/plain", "application/json" },
        skills = Array.Empty<object>()
    });

    private static JsonElement Parameters(string text) => JsonSerializer.SerializeToElement(new
    {
        message = new { kind = "message", messageId = "request", role = "user", parts = new[] { new { kind = "text", text } } }
    });

    private static string Frame(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<bool> CollectAsync(IAsyncEnumerable<IAgentStreamEvent> stream, List<IAgentStreamEvent> events)
    {
        await foreach (var item in stream) events.Add(item);
        return true;
    }

    private static async Task<JsonElement> FirstAsync(IAsyncEnumerable<JsonElement> stream)
    {
        await using var iterator = stream.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        return iterator.Current;
    }

    private static async Task<T> Within<T>(Task<T> operation)
    {
        using var watchdog = new CancellationTokenSource();
        var winner = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10), watchdog.Token));
        watchdog.Cancel();
        Assert.Same(operation, winner);
        return await operation;
    }

    private static async Task<T> CompleteAsync<T>(Task<T> operation, CancellationTokenSource cancellation)
    {
        try { return await Within(operation); }
        catch (Exception error)
        {
            await FinishAsync(operation, cancellation, error);
            throw;
        }
    }

    private static async Task FinishAsync(Task operation, CancellationTokenSource cancellation, Exception? primary)
    {
        var failures = new List<Exception>();
        try { cancellation.Cancel(); } catch (Exception error) { failures.Add(error); }
        try { await operation; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (!ReferenceEquals(primary, error)) failures.Add(error); }
        if (failures.Count == 0) return;
        if (primary is not null) { primary.Data["Strands.TestCleanupErrors"] = failures.ToArray(); return; }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("A2A REST fixture cleanup failed.", failures);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class TrackedContent(string body, string contentType) : StringContent(body, Encoding.UTF8, contentType)
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
}
