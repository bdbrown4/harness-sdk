using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.Mcp;
using Strands.Agents.Models;
using Xunit;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpMetadataTests
{
    [Fact]
    public void MetadataIsDetachedAndTheExistingConstructorAndDeconstructionRemainValid()
    {
        using var document = JsonDocument.Parse("""{"vendor":{"receipt":"opaque"}}""");
        var result = new ToolResultBlock("call", "visible") { Metadata = document.RootElement };
        var (id, content, status) = result;
        document.Dispose();

        Assert.Equal("call", id); Assert.Equal("visible", content); Assert.Equal(ToolResultStatus.Success, status);
        Assert.Equal("opaque", result.Metadata!.Value.GetProperty("vendor").GetProperty("receipt").GetString());
        var copy = Assert.IsType<ToolResultBlock>(Assert.Single(new Message(Role.User, [result]).Content));
        Assert.Equal(result.Metadata.Value.GetRawText(), copy.Metadata!.Value.GetRawText());
        Assert.Null((result with { Metadata = JsonSerializer.SerializeToElement<object?>(null) }).Metadata);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    public void MetadataRejectsNonObjectValues(string value)
        => Assert.Throws<ArgumentException>(() => new ToolResultBlock("call", "visible") { Metadata = JsonSerializer.Deserialize<JsonElement>(value) });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task McpRichResultsSurviveAgentHistoryRealSessionsAndSnapshots(bool routed, bool redact)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands mcp metadata " + Guid.NewGuid().ToString("N"));
        try
        {
            using var server = new McpRouterTests.RouterServer
            {
                HandleAsync = (_, _) => Task.FromResult<object?>(new
                { content = new[] { new { type = "text", text = "visible" } }, structuredContent = 0, _meta = new { vendor = new { receipt = "opaque" } } })
            };
            ITool tool;
            IAsyncDisposable owner;
            if (routed)
            {
                var router = new McpRouter(new() { Servers = new Dictionary<string, Func<CancellationToken, Task<McpClient>>> { ["api"] = server.ConnectAsync } });
                tool = router; owner = router;
            }
            else
            {
                var client = await server.ConnectAsync(default);
                owner = client; tool = Assert.Single(await client.ListToolsAsync());
            }
            var session = new FileSessionStore(directory);
            var hooks = new HookRegistry();
            ToolResultBlock? observed = null;
            hooks.Register<AfterToolCallEvent>((value, _) => { if (value.Result.Metadata is not null) observed = value.Result; return default; });
            await using var agent = new Agent(new() { Model = new MetadataModel(routed, redact), Tools = [tool], OwnedResources = [owner], Hooks = hooks, SessionStore = session, SessionId = "session" });
            await agent.InvokeAsync("use MCP");
            Assert.NotNull(observed); AssertMetadata(observed);
            AssertMetadata(Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Metadata is not null), redact);
            var snapshot = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages });
            await using var fromSnapshot = McpRouterTests.NewAgent();
            await fromSnapshot.LoadSnapshotAsync(snapshot);
            AssertMetadata(Assert.Single(fromSnapshot.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Metadata is not null), redact);
            await using var resumed = new Agent(new() { Model = new NoModel(), SessionStore = new FileSessionStore(directory), SessionId = "session" });
            await resumed.InitializeAsync();
            AssertMetadata(Assert.Single(resumed.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Metadata is not null), redact);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("google")]
    [InlineData("responses")]
    public async Task ModelProviderRequestsExcludeOpaqueToolMetadata(string provider)
    {
        using var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        IModel model = provider switch
        {
            "openai" => new OpenAIModel(http, new("model", "key")),
            "anthropic" => new AnthropicModel(http, new("model", "key")),
            "google" => new GoogleModel(http, new("model", "key")),
            _ => new OpenAIResponsesModel(http, new("model", "key"))
        };
        var input = JsonSerializer.SerializeToElement(new { });
        var result = new ToolResultBlock("call", [new TextBlock("visible")], structuredContent: JsonSerializer.SerializeToElement(false))
        { Metadata = JsonSerializer.SerializeToElement(new { receipt = "private_vendor_receipt" }) };
        var request = new ModelRequest([Message.User("task"), new(Role.Assistant, [new ToolUseBlock("call", "echo", input)]), new(Role.User, [result])]);

        await Assert.ThrowsAsync<ModelException>(async () => { await foreach (var _ in model.StreamAsync(request)) { } });

        Assert.NotNull(handler.Body); Assert.Contains("visible", handler.Body);
        Assert.DoesNotContain("private_vendor_receipt", handler.Body);
    }

    [Fact]
    public async Task EmptyProtocolMetadataIsOmittedFollowingPythonTruthiness()
    {
        using var server = new McpRouterTests.RouterServer
        {
            HandleAsync = (_, _) => Task.FromResult<object?>(new
            { content = new[] { new { type = "text", text = "visible" } }, _meta = new { } })
        };
        await using var client = await server.ConnectAsync(default);
        var tool = Assert.Single(await client.ListToolsAsync());
        await using var agent = McpRouterTests.NewAgent();
        var input = JsonSerializer.SerializeToElement(new { });
        var result = await tool.InvokeResultAsync(input, new(agent, new("call", "echo", input), new Dictionary<string, object?>()));
        Assert.Null(result.Metadata);
        Assert.False(JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)).TryGetProperty("metadata", out _));
    }

    private static void AssertMetadata(ToolResultBlock result, bool redacted = false)
    {
        Assert.Equal("opaque", result.Metadata!.Value.GetProperty("vendor").GetProperty("receipt").GetString());
        if (redacted) { Assert.Null(result.StructuredContent); Assert.Equal("redacted", result.Content); }
        else Assert.Equal(0, result.StructuredContent!.Value.GetInt32());
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"capture complete\"}}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class NoModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model call is expected.");
    }
    private sealed class MetadataModel(bool routed, bool redact) : IModel
    {
        private int _turn;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var turn = _turn++;
            object? input = routed && turn == 0 ? new { command = "connect", server_name = "api", connection_id = "c" }
                : turn == (routed ? 1 : 0) ? routed ? (object)new { command = "call_tool", connection_id = "c", tool_name = "echo" } : new { }
                : null;
            yield return new ModelMessageStartEvent();
            if (input is not null) yield return new ModelContentBlockStartEvent(0, new("call-" + turn, routed ? "mcp_router" : "echo", JsonSerializer.SerializeToElement(input)));
            else
            {
                AssertMetadata(Assert.Single(request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Metadata is not null));
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "complete");
            }
            yield return new ModelContentBlockStopEvent(0);
            if (input is null && redact) yield return new ModelRedactionEvent(InputReplacement: "redacted");
            yield return new ModelMessageStopEvent(input is null ? StopReasons.EndTurn : StopReasons.ToolUse);
            await Task.CompletedTask;
        }
    }
}
