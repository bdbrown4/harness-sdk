using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;
using TaskServer = Strands.Agents.Tests.Mcp.McpTaskClientTests.TaskServer;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpUrlElicitationConformanceTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("nested")]
    public async Task GuidanceProjectsUrlFieldsAndOnlyPresentMetadata(string metadata)
    {
        var elicitation = ValidElicitation();
        elicitation["message"] = "Café 雪\nconsent";
        elicitation["elicitationId"] = "authorization/雪\"";
        elicitation["url"] = "https://consent.example.test/authorize?q=%E9%9B%AA";
        elicitation["requestState"] = "unrelated-retry-state";
        elicitation["inputResponses"] = new { };
        if (metadata != "absent") elicitation["_meta"] = metadata == "empty" ? new JsonObject()
            : new JsonObject { ["opaque"] = new JsonObject { ["value"] = "原", ["nullValue"] = null } };
        using var handler = ErrorServer(-32042, new { elicitations = new[] { elicitation } });
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var tool = Assert.Single(await client.ListToolsAsync());
        await using var agent = NewAgent(tool);

        var result = await agent.InvokeToolAsync(Call(tool));

        Assert.Equal("conformance-call", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        var cause = Assert.IsType<UrlElicitationRequiredException>(result.Error);
        var original = Assert.Single(cause.Elicitations);
        Assert.Equal("unrelated-retry-state", original.RequestState);
        Assert.NotNull(original.InputResponses);
        Assert.Empty(original.InputResponses);
        var prefix = "MCP Elicitation required: [" + cause.Message + "] with data ";
        Assert.StartsWith(prefix, result.Content);
        using var document = JsonDocument.Parse(result.Content.Substring(prefix.Length));
        var projected = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("url", projected.GetProperty("mode").GetString());
        Assert.Equal("Café 雪\nconsent", projected.GetProperty("message").GetString());
        Assert.Equal("authorization/雪\"", projected.GetProperty("elicitationId").GetString());
        Assert.Equal("https://consent.example.test/authorize?q=%E9%9B%AA", projected.GetProperty("url").GetString());
        Assert.False(projected.TryGetProperty("requestedSchema", out _));
        Assert.False(projected.TryGetProperty("requestState", out _));
        Assert.False(projected.TryGetProperty("inputResponses", out _));
        Assert.Equal(metadata != "absent", projected.TryGetProperty("_meta", out var meta));
        Assert.Equal(metadata == "absent" ? 4 : 5, projected.EnumerateObject().Count());
        if (metadata == "empty") Assert.Empty(meta.EnumerateObject());
        if (metadata == "nested")
        {
            Assert.Equal("原", meta.GetProperty("opaque").GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, meta.GetProperty("opaque").GetProperty("nullValue").ValueKind);
            Assert.Equal("原", original.Meta!["opaque"]!["value"]!.GetValue<string>());
        }
        Assert.Single(handler.Requests, value => value.Method == "tools/call");
    }

    [Theory]
    [InlineData("wrong-code")]
    [InlineData("empty-list")]
    [InlineData("null-data")]
    [InlineData("missing-data")]
    [InlineData("missing-url")]
    [InlineData("missing-id")]
    [InlineData("form-mode")]
    public async Task NonmatchingOrSemanticallyInvalidUrlErrorsKeepNativeFallback(string kind)
    {
        var code = kind == "wrong-code" ? -32603 : -32042;
        var elicitation = ValidElicitation();
        if (kind == "missing-url") elicitation.Remove("url");
        if (kind == "missing-id") elicitation.Remove("elicitationId");
        if (kind == "form-mode") elicitation["mode"] = "form";
        object? data = kind == "null-data" ? null
            : new { elicitations = kind == "empty-list" ? Array.Empty<Dictionary<string, object?>>() : new[] { elicitation } };
        using var handler = ErrorServer(code, data, includeData: kind != "missing-data");
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var tool = Assert.Single(await client.ListToolsAsync());
        await using var agent = NewAgent(tool);

        var result = await agent.InvokeToolAsync(Call(tool));

        Assert.Equal("conformance-call", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        var cause = Assert.IsType<McpProtocolException>(result.Error);
        Assert.Equal(code, (int)cause.ErrorCode);
        Assert.Contains("authorization fixture", cause.Message);
        Assert.Equal(cause.Message, result.Content);
        Assert.DoesNotContain("MCP Elicitation required:", result.Content);
        Assert.Single(handler.Requests, value => value.Method == "tools/call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryResultsRetainRichChannelsAndTextErrorPolicy(bool isError)
    {
        using var handler = McpLegacyTaskTests.Server();
        var baseline = handler.Respond!;
        handler.Respond = (request, token) => request.Method == "tools/call"
            ? Task.FromResult<HttpResponseMessage?>(TaskServer.JsonResponse(request.Envelope.GetProperty("id"), new
            {
                isError,
                content = new[] { new { type = "text", text = "ordinary result" } },
                structuredContent = new { answer = 42 },
                _meta = new { vendor = "ordinary" }
            }))
            : baseline(request, token);
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var tool = Assert.Single(await client.ListToolsAsync());
        await using var agent = NewAgent(tool);

        var result = await agent.InvokeToolAsync(Call(tool));

        Assert.Equal(isError ? ToolResultStatus.Error : ToolResultStatus.Success, result.Status);
        Assert.Equal("conformance-call", result.ToolUseId);
        Assert.Equal("ordinary result", result.Content);
        Assert.Null(result.Error);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("answer").GetInt32());
        Assert.Equal("ordinary", result.Metadata!.Value.GetProperty("vendor").GetString());
        var context = new ToolContext(agent, Call(tool), new Dictionary<string, object?>());
        if (isError)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(Input(), context).AsTask());
            Assert.Equal("ordinary result", error.Message);
            Assert.Null(error.InnerException);
        }
        else Assert.Equal("ordinary result", await tool.InvokeAsync(Input(), context));
        Assert.Equal(2, handler.Requests.Count(value => value.Method == "tools/call"));
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoUrlGuidance()
    {
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        using var handler = McpLegacyTaskTests.Server();
        var baseline = handler.Respond!;
        handler.Respond = async (request, token) =>
        {
            if (request.Method == "tools/call")
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }
            return await baseline(request, token).ConfigureAwait(false);
        };
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint);
        var tool = Assert.Single(await client.ListToolsAsync());
        var hooks = new HookRegistry();
        ToolResultBlock? observed = null;
        hooks.Register<AfterToolCallEvent>((value, _) => { observed = value.Result; return default; });
        await using var agent = NewAgent(tool, hooks);
        using var cancellation = new CancellationTokenSource();
        var pending = agent.InvokeToolAsync(Call(tool), cancellationToken: cancellation.Token).AsTask();
        Exception? primary = null;
        try
        {
            await Within(entered.Task);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(pending));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(error.CancellationToken.IsCancellationRequested);
            Assert.NotNull(observed);
            Assert.Same(error, observed.Error);
            Assert.DoesNotContain("MCP Elicitation required:", observed.Content);
            Assert.Equal(1, Volatile.Read(ref calls));
            await Within(agent.ShutdownAsync());
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            if (!pending.IsCompleted) cancellation.Cancel();
            try { await Within(pending); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception cleanup) when (primary is not null) { primary.Data["McpUrlFixtureCleanup"] = cleanup; }
        }
    }

    private static TaskServer ErrorServer(int code, object? data, bool includeData = true)
    {
        var handler = McpLegacyTaskTests.Server();
        var baseline = handler.Respond!;
        handler.Respond = (request, token) =>
        {
            if (request.Method != "tools/call") return baseline(request, token);
            var error = new Dictionary<string, object?> { ["code"] = code, ["message"] = "authorization fixture" };
            if (includeData) error["data"] = data;
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.Envelope.GetProperty("id"), error }), Encoding.UTF8, "application/json")
            });
        };
        return handler;
    }

    private static Dictionary<string, object?> ValidElicitation() => new()
    {
        ["mode"] = "url",
        ["elicitationId"] = "authorization-1",
        ["url"] = "https://consent.example.test/authorize",
        ["message"] = "authorize"
    };
    private static Agent NewAgent(ITool tool, HookRegistry? hooks = null) => new(new AgentOptions { Model = new UnusedModel(), Tools = [tool], Hooks = hooks ?? new HookRegistry() });
    private static ToolUseBlock Call(McpTool tool) => new("conformance-call", tool.Spec.Name, Input());
    private static JsonElement Input() => JsonSerializer.SerializeToElement(new { });
    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint) => Client.CreateHttpAsync(new McpClientSettings(), http,
        new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) });
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task pending)
    {
        using var watchdog = new CancellationTokenSource();
        var timeout = Task.Delay(TimeSpan.FromSeconds(10), watchdog.Token);
        try
        {
            if (await Task.WhenAny(pending, timeout) != pending) throw new TimeoutException("MCP URL fixture did not settle.");
            await pending;
        }
        finally
        {
            watchdog.Cancel();
            try { await timeout; }
            catch (OperationCanceledException error) when (error.CancellationToken == watchdog.Token) { }
        }
    }
    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("A direct MCP conformance tool call must not invoke a model.");
    }
}
