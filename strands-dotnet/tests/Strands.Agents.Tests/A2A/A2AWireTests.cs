using System.Net.Http;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeHttpHandlerSupportsRealLengthAndChunkedResponsesWithSplitUnicode(bool chunked)
    {
        Uri? baseUri = null;
        await using var server = new A2ATestServer(request =>
        {
            if (request.Method == "GET") return new(A2AClientTests.Card.Replace("https://agent.test/rpc", new Uri(baseUri!, "rpc").AbsoluteUri), Chunked: chunked);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("/rpc", request.Path);
            Assert.Equal("message/stream", body.RootElement.GetProperty("method").GetString());
            var id = body.RootElement.GetProperty("id").GetString()!;
            return new(A2AAgentTests.Frame(id, new { kind = "message", messageId = "response", role = "agent", parts = new[] { new { kind = "text", text = "Résumé 🐈" } } }), "text/event-stream", chunked, 1);
        });
        baseUri = server.BaseUri;
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var agent = new A2AAgent(new(http, new(server.BaseUri)));

        var result = await agent.InvokeAsync("Hello", cancellationToken: timeout.Token);

        Assert.Equal("Résumé 🐈", result.ToString());
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("Remote researcher", agent.Name);
    }

    [Fact]
    public async Task CancellationClosesABlockedNativeBodyWithoutImplicitRemoteCancellation()
    {
        Uri? baseUri = null;
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new A2ATestServer(request => request.Method == "GET" ?
            new(A2AClientTests.Card.Replace("https://agent.test/rpc", new Uri(baseUri!, "rpc").AbsoluteUri)) :
            new("pending body", "text/event-stream", WaitBeforeBody: never.Task));
        baseUri = server.BaseUri;
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = new A2AAgent(new(http, new(server.BaseUri))).InvokeAsync("Question", cancellationToken: cancellation.Token);
        await WithDeadline(server.RpcHeadersSent.Task);

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithDeadline(operation));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(2, server.Requests.Count);
        using var stillBorrowed = await http.GetAsync(new Uri(server.BaseUri, ".well-known/agent-card.json"));
        Assert.True(stillBorrowed.IsSuccessStatusCode);
    }

    [Fact]
    public async Task InputRequiredTaskCanBeResumedOverRealHttpUsingExplicitCorrelationAndStructuredAnswers()
    {
        Uri? baseUri = null;
        var calls = 0;
        await using var server = new A2ATestServer(request =>
        {
            if (request.Method == "GET") return new(A2AClientTests.Card.Replace("https://agent.test/rpc", new Uri(baseUri!, "rpc").AbsoluteUri));
            using var body = JsonDocument.Parse(request.Body);
            var root = body.RootElement;
            var id = root.GetProperty("id").GetString()!;
            if (Interlocked.Increment(ref calls) == 1)
                return new(A2AAgentTests.Frame(id, new { kind = "status-update", taskId = "task", contextId = "context", final = true, status = new { state = "input-required", message = new { kind = "message", messageId = "question", role = "agent", parts = new object[] { new { kind = "text", text = "Approve?" }, new { kind = "data", data = new { interrupts = new[] { new { interruptId = "opaque-question", name = "approval" } } } } } } } }), "text/event-stream");
            var message = root.GetProperty("params").GetProperty("message");
            Assert.Equal("task", message.GetProperty("taskId").GetString());
            Assert.Equal("context", message.GetProperty("contextId").GetString());
            var answer = message.GetProperty("parts")[0].GetProperty("data").GetProperty("interruptResponse");
            Assert.Equal("opaque-question", answer.GetProperty("interruptId").GetString());
            Assert.True(answer.GetProperty("response").GetBoolean());
            return new(A2AAgentTests.Frame(id, new { kind = "status-update", taskId = "task", contextId = "context", final = true, status = new { state = "completed", message = new { kind = "message", messageId = "answer", role = "agent", parts = new[] { new { kind = "text", text = "Approved" } } } } }), "text/event-stream");
        });
        baseUri = server.BaseUri;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var agent = new A2AAgent(new(http, new(server.BaseUri)));

        var paused = await agent.InvokeAsync("Run", cancellationToken: timeout.Token);
        var completed = await agent.InvokeContentAsync([new JsonBlock(JsonSerializer.SerializeToElement(new { interruptResponse = new { interruptId = "opaque-question", response = true } }))], new() { TaskId = "task", ContextId = "context" }, timeout.Token);

        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        Assert.Equal("opaque-question", Assert.Single(paused.Message!.Content.OfType<JsonBlock>()).Json.GetProperty("interrupts")[0].GetProperty("interruptId").GetString());
        Assert.Equal("Approved", completed.ToString());
        Assert.Equal(2, calls);
    }

    internal static async Task<T> WithDeadline<T>(Task<T> operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(15))));
        return await operation;
    }
}
