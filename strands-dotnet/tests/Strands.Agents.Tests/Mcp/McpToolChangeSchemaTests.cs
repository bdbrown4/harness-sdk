using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Client;
using Strands.Agents.Mcp;
using Strands.Agents.Models;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolChangeSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExistingToolKeepsItsAcceptedOutputSchemaAfterLaterDiscovery(bool initiallyAbsent)
    {
        var fixture = new Fixture { Definitions = [Definition("echo", initiallyAbsent ? null : "string")] };
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        var old = Assert.Single(await client.ListToolsAsync());
        fixture.Definitions = [Definition("echo", "number")];
        var fresh = Assert.Single(await client.ListToolsAsync());
        await using var agent = NewAgent();

        var result = await old.InvokeResultAsync(Input(), Context(agent, old));

        Assert.Equal(ToolResultStatus.Success, result.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => fresh.InvokeResultAsync(Input(), Context(agent, fresh)).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInflightNameCallKeepsItsAdmissionSchema(bool initiallyAbsent)
    {
        var fixture = new Fixture { Definitions = [Definition("echo", initiallyAbsent ? null : "string")] };
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync();
        var entered = NewSignal(); var release = NewSignal();
        fixture.Handle = async (request, ignored) =>
        {
            if (request.Method != "tools/call") return null;
            entered.TrySetResult(true); await release.Task; return Result("old");
        };
        var call = client.CallToolAsync("echo", Input()).AsTask();
        try
        {
            await Deadline(entered.Task);
            fixture.Definitions = [Definition("echo", "number")];
            await client.ListToolsAsync();
        }
        finally { release.TrySetResult(true); }

        Assert.Equal("old", (await Deadline(call)).StructuredContent!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task AFailedLaterPageDoesNotPublishTheEarlierPagesOutputSchema()
    {
        var fixture = new Fixture();
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync();
        fixture.Handle = (request, ignored) => Task.FromResult<object?>(request.Method != "tools/list" ? null :
            request.Parameters.TryGetProperty("cursor", out _) ? new { tools = new[] { new { name = "broken" } } } :
            new { tools = new[] { Definition("echo", "number") }, nextCursor = "later" });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListToolsAsync());

        Assert.Equal("old", (await client.CallToolAsync("echo", Input())).StructuredContent!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task AThrowingFilterCannotPublishTheEarlierToolsOutputSchema()
    {
        var fixture = new Fixture();
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync();
        fixture.Definitions = [Definition("echo", "number"), Definition("other", "string")];
        var failure = new IOException("filter rejected after echo");
        var actual = await Assert.ThrowsAsync<IOException>(() => client.ListToolsAsync(new()
        { ToolFilter = name => name == "other" ? throw failure : true }));
        Assert.Same(failure, actual);

        Assert.Equal("old", (await client.CallToolAsync("echo", Input())).StructuredContent!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ExplicitSchemaAbsenceChangesFutureNameCallsButNotAnOlderTool()
    {
        var fixture = new Fixture();
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        var old = Assert.Single(await client.ListToolsAsync());
        fixture.Definitions = [Definition("echo", null)];
        await client.ListToolsAsync(); fixture.Value = 42;
        await using var agent = NewAgent();
        Assert.Equal(42, (await client.CallToolAsync("echo", Input())).StructuredContent!.Value.GetProperty("value").GetInt32());

        await Assert.ThrowsAsync<InvalidDataException>(() => old.InvokeResultAsync(Input(), Context(agent, old)).AsTask());
    }

    [Fact]
    public async Task DisjointAcceptedDiscoveriesDoNotDeleteEarlierNameSchemas()
    {
        var fixture = new Fixture();
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync(new() { ToolFilter = name => name == "echo", Prefix = "first" });
        fixture.Definitions = [Definition("other", "number")];
        await client.ListToolsAsync(new() { ToolFilter = name => name == "other", Prefix = "second" });
        fixture.Value = 42;

        await Assert.ThrowsAsync<InvalidDataException>(() => client.CallToolAsync("echo", Input()).AsTask());
        Assert.Equal(42, (await client.CallToolAsync("other", Input())).StructuredContent!.Value.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task FailedDiscoveryDoesNotPublishEarlierPagesProtocolHeaderMetadata()
    {
        var fixture = new Fixture { Definitions = [Definition("echo", null, "Query")] };
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync();
        fixture.Handle = (request, ignored) => Task.FromResult<object?>(request.Method != "tools/list" ? null :
            request.Parameters.TryGetProperty("cursor", out _) ? new { tools = new[] { new { name = "broken" } } } :
            new { tools = new[] { Definition("echo", null, "Changed") }, nextCursor = "later" });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListToolsAsync());
        await client.CallToolAsync("echo", Input());

        Assert.Equal("kept", fixture.Server.Requests.Last(request => request.Method == "tools/call").ParameterHeader);
    }

    [Fact]
    public async Task CancellationAtTheFinalPageLeavesAllAcceptedSchemasAndHeadersUnchanged()
    {
        var fixture = new Fixture { Definitions = [Definition("echo", "string", "Query")] };
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync();
        fixture.Handle = (request, ignored) => Task.FromResult<object?>(request.Method != "tools/list" ? null :
            request.Parameters.TryGetProperty("cursor", out _) ? new { tools = new[] { Definition("final", "string") } } :
            new { tools = new[] { Definition("echo", "number", "Changed") }, nextCursor = "last" });
        using var caller = new CancellationTokenSource();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListToolsAsync(new()
        { ToolFilter = name => { if (name == "final") caller.Cancel(); return true; } }, caller.Token));

        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.Equal("old", (await client.CallToolAsync("echo", Input())).StructuredContent!.Value.GetProperty("value").GetString());
        Assert.Equal("kept", fixture.Server.Requests.Last(request => request.Method == "tools/call").ParameterHeader);
    }

    [Fact]
    public async Task AnInvalidHeaderInTheFinalBatchLeavesEveryAcceptedEntryUnchanged()
    {
        var fixture = new Fixture { Definitions = [Definition("echo", "string", "Query")] };
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        await client.ListToolsAsync();
        fixture.Definitions = [Definition("echo", "number", "Changed"), Definition("bad", "string", "invalid header")];

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListToolsAsync());

        Assert.Equal("old", (await client.CallToolAsync("echo", Input())).StructuredContent!.Value.GetProperty("value").GetString());
        Assert.Equal("kept", fixture.Server.Requests.Last(request => request.Method == "tools/call").ParameterHeader);
    }

    [Fact]
    public async Task OverlappingScopesUseAcceptanceOrderAndKeepOlderBoundToolsAndDisjointNames()
    {
        var fixture = new Fixture();
        using var http = new HttpClient(fixture.Server);
        await using var client = await ConnectAsync(http);
        var old = Assert.Single(await client.ListToolsAsync());
        var waiting = NewSignal(); var release = NewSignal();
        var starts = 0;
        fixture.Handle = async (request, ignored) =>
        {
            if (request.Method != "tools/list") return null;
            if (request.Parameters.TryGetProperty("cursor", out _))
            {
                waiting.TrySetResult(true);
                await release.Task;
                return new { tools = new[] { Definition("left", "string") } };
            }
            return Interlocked.Increment(ref starts) == 1
                ? new { tools = new[] { Definition("echo", "string") }, nextCursor = "slow-final" }
                : (object)new { tools = new[] { Definition("echo", "number"), Definition("right", "number") } };
        };
        var slow = client.ListToolsAsync(new() { Prefix = "left", ToolFilter = name => name is "echo" or "left" });
        IReadOnlyList<McpTool> fast;
        try
        {
            await Deadline(waiting.Task);
            fast = await client.ListToolsAsync(new() { Prefix = "right", ToolFilter = name => name is "echo" or "right" });
        }
        finally { release.TrySetResult(true); }
        Assert.Equal(2, (await Deadline(slow)).Count);
        await using var agent = NewAgent();

        Assert.Equal(ToolResultStatus.Success, (await old.InvokeResultAsync(Input(), Context(agent, old))).Status);
        Assert.Equal("old", (await client.CallToolAsync("echo", Input())).StructuredContent!.Value.GetProperty("value").GetString());
        var fastEcho = Assert.Single(fast, tool => tool.ServerName == "echo");
        await Assert.ThrowsAsync<InvalidDataException>(() => fastEcho.InvokeResultAsync(Input(), Context(agent, fastEcho)).AsTask());
        fixture.Value = 42;
        Assert.Equal(42, (await client.CallToolAsync("right", Input())).StructuredContent!.Value.GetProperty("value").GetInt32());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CallToolAsync("echo", Input()).AsTask());
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("finalized")]
    [InlineData("legacy")]
    public async Task BoundAndNameCallsValidateOutputsInEveryExecutionProfile(string profile)
    {
        using var server = profile == "legacy" ? McpLegacyTaskTests.Server() : new McpTaskClientTests.TaskServer();
        var baseline = server.Respond;
        string? outputType = "string";
        server.Respond = async (request, token) =>
        {
            object? response = request.Method switch
            {
                "tools/list" => new { tools = new[] { WithLegacyTaskSupport(Definition("echo", outputType)) } },
                "tools/call" when profile == "ordinary" => Result("old"),
                "tasks/get" when profile == "finalized" => new
                {
                    resultType = "complete",
                    taskId = server.TaskId,
                    status = "completed",
                    createdAt = "2026-10-20T10:00:00Z",
                    lastUpdatedAt = "2026-10-20T10:00:01Z",
                    ttlMs = (int?)null,
                    result = Result("old")
                },
                "tasks/result" when profile == "legacy" => Result("old"),
                _ => null
            };
            return response is not null ? McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), response)
                : baseline is null ? null : await baseline(request, token);
        };
        using var http = new HttpClient(server);
        await using var client = profile == "ordinary" ? await ConnectAsync(http)
            : profile == "legacy" ? await McpLegacyTaskTests.ConnectAsync(http) : await McpTaskClientTests.ConnectAsync(http);
        var old = Assert.Single(await client.ListToolsAsync());
        outputType = "number";
        await client.ListToolsAsync();
        await using var agent = NewAgent();

        Assert.Equal(ToolResultStatus.Success, (await old.InvokeResultAsync(Input(), Context(agent, old))).Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CallToolAsync("echo", Input()).AsTask());

        Assert.Equal(2, server.Requests.Count(request => request.Method == "tools/call"));
        Assert.Equal(profile == "ordinary" ? 0 : 2, server.Requests.Count(request => request.Method == "tasks/get"));
        Assert.Equal(profile == "legacy" ? 2 : 0, server.Requests.Count(request => request.Method == "tasks/result"));
    }

    private static JsonElement WithLegacyTaskSupport(JsonElement definition)
    {
        var copy = definition.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone(), StringComparer.Ordinal);
        copy["execution"] = new { taskSupport = "optional" };
        return JsonSerializer.SerializeToElement(copy);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Deadline<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); return await task;
    }
    private static JsonElement Input() => JsonSerializer.SerializeToElement(new { query = "kept" });
    private static Agent NewAgent() => new(new AgentOptions { Model = new NeverModel() });
    private static ToolContext Context(Agent agent, McpTool tool) => new(agent, new("call", tool.Spec.Name, Input()), new Dictionary<string, object?>());
    private static Task<Client> ConnectAsync(HttpClient http) => Client.CreateHttpAsync(http,
        new() { Endpoint = new Uri("https://schema.test/mcp"), TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new() { ProtocolVersion = "2026-07-28", InitializationTimeout = TimeSpan.FromSeconds(5) });
    private static object Result(object? value) => new { content = new[] { new { type = "text", text = "valid" } }, structuredContent = new { value } };
    private static JsonElement Definition(string name, string? outputType, string? header = null)
    {
        var property = new Dictionary<string, object?> { ["type"] = "string" };
        if (header is not null) property["x-mcp-header"] = header;
        var definition = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = "Read " + name,
            ["inputSchema"] = new { type = "object", properties = new Dictionary<string, object> { ["query"] = property } }
        };
        if (outputType is not null) definition["outputSchema"] = new
        { type = "object", properties = new Dictionary<string, object> { ["value"] = new { type = outputType } }, required = new[] { "value" }, additionalProperties = false };
        return JsonSerializer.SerializeToElement(definition);
    }
    private sealed class NeverModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("These tests directly invoke MCP tools.");
    }
    private sealed class Fixture
    {
        internal McpTaskClientTests.TaskServer Server { get; } = new();
        internal JsonElement[] Definitions { get; set; } = [Definition("echo", "string")];
        internal object? Value { get; set; } = "old";
        internal Func<McpTaskClientTests.Request, CancellationToken, Task<object?>>? Handle { get; set; }
        internal Fixture()
        {
            Server.Respond = async (request, token) =>
            {
                var result = Handle is null ? null : await Handle(request, token);
                result ??= request.Method switch { "tools/list" => new { tools = Definitions }, "tools/call" => Result(Value), _ => null };
                return result is null ? null : McpTaskClientTests.TaskServer.JsonResponse(request.Envelope.GetProperty("id"), result);
            };
        }
    }
}
