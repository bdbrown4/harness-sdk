using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AAgentTests
{
    [Fact]
    public async Task InvokingAggregatesAppendedAndReplacedArtifactsWithoutTransmittingCallerState()
    {
        JsonElement sent = default;
        var handler = new ProtocolHandler((request, id) =>
        {
            sent = request.Clone();
            return string.Concat(
                Frame(id, new { kind = "task", id = "task", contextId = "context", status = new { state = "working" } }),
                Frame(id, Artifact("a", "obsolete", false)),
                Frame(id, Artifact("a", "Hello", false)),
                Frame(id, Artifact("a", " world", true)),
                Frame(id, Artifact("b", "Second", false)),
                Frame(id, new { kind = "status-update", taskId = "task", contextId = "context", status = new { state = "completed" }, final = true }));
        });
        using var http = new HttpClient(handler);
        var callerState = new Dictionary<string, object?> { ["secret-local-object"] = new object() };
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));

        var result = await agent.InvokeAsync("Research", new() { InvocationState = callerState });

        Assert.Equal("Hello world\nSecond", result.ToString());
        Assert.Same(callerState, result.InvocationState);
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("user", sent.GetProperty("params").GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Research", sent.GetProperty("params").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("secret-local-object", sent.GetRawText());
    }

    [Theory]
    [InlineData("completed", StopReasons.EndTurn)]
    [InlineData("failed", StopReasons.EndTurn)]
    [InlineData("canceled", StopReasons.EndTurn)]
    [InlineData("rejected", StopReasons.EndTurn)]
    [InlineData("input-required", StopReasons.Interrupt)]
    [InlineData("auth-required", StopReasons.Interrupt)]
    public async Task ProtocolStatesAndRemoteCorrelationRemainAvailableWhenInvokingWithoutStreamingEvents(string state, string stopReason)
    {
        using var http = new HttpClient(new ProtocolHandler((_, id) => Frame(id, new
        {
            kind = "status-update",
            taskId = "remote-task",
            contextId = "remote-context",
            final = true,
            status = new { state, message = new { kind = "message", messageId = "status", role = "agent", parts = new[] { new { kind = "text", text = "status detail" } } } }
        })));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));

        var result = await agent.InvokeAsync("Question");

        Assert.Equal(stopReason, result.StopReason);
        Assert.Equal("status detail", result.ToString());
        Assert.NotNull(result.Message!.Metadata);
        var remote = result.Message.Metadata.Value.GetProperty("a2a");
        Assert.Equal(state, remote.GetProperty("taskState").GetString());
        Assert.Equal("remote-task", remote.GetProperty("taskId").GetString());
        Assert.Equal("remote-context", remote.GetProperty("contextId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeScriptTextProjectionJoinsPartsWithinChunksWithoutSeparatingAppendedChunks(bool artifact)
    {
        var parts = new[] { new { kind = "text", text = "First" }, new { kind = "text", text = "Second" } };
        using var http = new HttpClient(new ProtocolHandler((_, id) => artifact ?
            Frame(id, new { kind = "artifact-update", taskId = "task", contextId = "context", artifact = new { artifactId = "a", parts }, append = false }) +
            Frame(id, Artifact("a", " appended", true)) +
            Frame(id, new { kind = "status-update", taskId = "task", contextId = "context", status = new { state = "completed" }, final = true }) :
            Frame(id, new { kind = "message", messageId = "reply", role = "agent", parts })));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))), new() { ContentMode = A2AContentMode.TextOnly });

        Assert.Equal("First\nSecond" + (artifact ? " appended" : ""), (await agent.InvokeAsync("Question")).ToString());
    }

    [Fact]
    public async Task EmptyArtifactIdentifiersCannotBypassTheRetainedResultBudget()
    {
        using var http = new HttpClient(new ProtocolHandler((_, id) =>
            string.Concat(Enumerable.Range(0, 20).Select(index => Frame(id, new { kind = "artifact-update", taskId = "task", contextId = "context", artifact = new { artifactId = index.ToString(System.Globalization.CultureInfo.InvariantCulture), parts = Array.Empty<object>() }, append = false }))) +
            Frame(id, new { kind = "status-update", taskId = "task", contextId = "context", status = new { state = "completed" }, final = true })));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))), new() { MaxResultBytes = 128 });

        var error = await Assert.ThrowsAsync<A2AProtocolException>(() => agent.InvokeAsync("Question"));

        Assert.Contains("MaxResultBytes", error.Message);
    }

    [Fact]
    public async Task RichPartsAndProtocolExtensionsAreDetachedAtCallTimeWithoutFetchingResourceUris()
    {
        JsonElement sent = default;
        using var http = new HttpClient(new ProtocolHandler((request, id) =>
        {
            sent = request.Clone();
            return Frame(id, new
            {
                kind = "message",
                messageId = "response",
                role = "agent",
                parts = new object[]
                {
                    new { kind = "text", text = "read" }, new { kind = "data", data = new { answer = 42 } },
                    new { kind = "file", file = new { mimeType = "image/png", bytes = "QQ==" } },
                    new { kind = "file", file = new { mimeType = "audio/wav", bytes = "Qg==" } },
                    new { kind = "file", file = new { mimeType = "application/pdf", bytes = "Qw==", name = "paper" } },
                    new { kind = "file", file = new { uri = "https://resource.test/private", name = "reference", mimeType = "text/plain" } }
                }
            });
        }));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));
        var document = JsonDocument.Parse("{\"original\":true}");
        ContentBlock[] content = [new JsonBlock(document.RootElement), new ImageBlock("image/png", "QQ=="), new ResourceLinkBlock("https://resource.test/input", "reference")];
        var stream = agent.StreamContentAsync(content, new() { Metadata = document.RootElement });
        content[0] = new TextBlock("changed"); document.Dispose();
        A2AResultEvent? final = null;

        await foreach (var item in stream) if (item is A2AResultEvent result) final = result;

        Assert.NotNull(final);
        var blocks = final.Result.Message!.Content;
        Assert.Collection(blocks, block => Assert.IsType<TextBlock>(block), block => Assert.Equal(42, Assert.IsType<JsonBlock>(block).Json.GetProperty("answer").GetInt32()),
            block => Assert.IsType<ImageBlock>(block), block => Assert.IsType<AudioBlock>(block), block => Assert.Equal("paper", Assert.IsType<DocumentBlock>(block).Name),
            block => Assert.Equal("https://resource.test/private", Assert.IsType<ResourceLinkBlock>(block).Uri));
        var parameters = sent.GetProperty("params");
        Assert.True(parameters.GetProperty("metadata").GetProperty("original").GetBoolean());
        Assert.True(parameters.GetProperty("message").GetProperty("parts")[0].GetProperty("data").GetProperty("original").GetBoolean());
        Assert.Equal("QQ==", parameters.GetProperty("message").GetProperty("parts")[1].GetProperty("file").GetProperty("bytes").GetString());
    }

    [Fact]
    public async Task UnsupportedInputFailsBeforeDiscoveryAndUnknownRemotePartsRemainExplicitErrors()
    {
        var calls = 0;
        using var http = new HttpClient(new ProtocolHandler((_, id) => { calls++; return Frame(id, new { kind = "message", messageId = "response", role = "agent", parts = new[] { new { kind = "future-part" } } }); }));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));
        Assert.Throws<NotSupportedException>(() => agent.StreamContentAsync([new ToolUseBlock("id", "tool", JsonSerializer.SerializeToElement(new { }))]));
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<NotSupportedException>(() => agent.InvokeAsync("Question"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationObservedAfterTheFinalRawUpdateWinsBeforeTerminalProjection()
    {
        using var http = new HttpClient(new ProtocolHandler((_, id) => Frame(id, new { kind = "message", messageId = "response", role = "agent", parts = new[] { new { kind = "text", text = "completed" } } })));
        using var cancellation = new CancellationTokenSource();
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))));
        await using var iterator = agent.StreamAsync("Question", cancellationToken: cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<A2AStreamUpdateEvent>(iterator.Current);

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ContinuingATaskRejectsAnInternallyConsistentResponseForAnotherTaskOrContext(bool wrongTask, bool streaming)
    {
        using var http = new HttpClient(new A2AErrorTests.Handler(id =>
        {
            var result = new { kind = "task", id = wrongTask ? "different-task" : "requested-task", contextId = wrongTask ? "requested-context" : "different-context", status = new { state = "completed" } };
            return streaming ? Frame(id, result) : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        }, streaming));
        var agent = new A2AAgent(new(http, new(new("https://agent.test/"))), new() { Streaming = streaming });

        await Assert.ThrowsAsync<A2AProtocolException>(() => agent.InvokeAsync("Answer", new() { TaskId = "requested-task", ContextId = "requested-context" }));
    }

    [Fact]
    public async Task MessageOnlyContinuationRetainsItsExplicitTaskAndContextIdentifiers()
    {
        using var http = new HttpClient(new ProtocolHandler((_, id) => Frame(id, new { kind = "message", messageId = "response", taskId = "requested-task", contextId = "requested-context", role = "agent", parts = new[] { new { kind = "text", text = "complete" } } })));
        var result = await new A2AAgent(new(http, new(new("https://agent.test/")))).InvokeAsync("Answer", new() { TaskId = "requested-task", ContextId = "requested-context" });
        Assert.Equal("requested-task", result.Message!.Metadata!.Value.GetProperty("a2a").GetProperty("taskId").GetString());
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("input-required")]
    public async Task FinalStatusMessagesTakePrecedenceOverEarlierArtifactsLikeBothPinnedAdapters(string state)
    {
        using var http = new HttpClient(new ProtocolHandler((_, id) => Frame(id, Artifact("output", "Earlier partial output", false)) +
            Frame(id, new { kind = "status-update", taskId = "task", contextId = "context", status = new { state, message = new { kind = "message", messageId = "status", role = "agent", parts = new object[] { new { kind = "text", text = "Final answer" }, new { kind = "data", data = new { question = "opaque" } } } } }, final = true })));

        var result = await new A2AAgent(new(http, new(new("https://agent.test/")))).InvokeAsync("Question");

        Assert.Equal("Final answer", result.ToString());
        Assert.Equal("opaque", Assert.Single(result.Message!.Content.OfType<JsonBlock>()).Json.GetProperty("question").GetString());
    }

    private static object Artifact(string id, string text, bool append) => new { kind = "artifact-update", taskId = "task", contextId = "context", artifact = new { artifactId = id, parts = new[] { new { kind = "text", text } } }, append };
    internal static string Frame(string id, object result) => "data: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }) + "\n\n";
    internal sealed class ProtocolHandler(Func<JsonElement, string, string> stream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(A2AClientTests.Card, Encoding.UTF8, "application/json") };
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var body = document.RootElement;
            return new(HttpStatusCode.OK) { Content = new StringContent(stream(body, body.GetProperty("id").GetString()!), Encoding.UTF8, "text/event-stream") };
        }
    }
}
