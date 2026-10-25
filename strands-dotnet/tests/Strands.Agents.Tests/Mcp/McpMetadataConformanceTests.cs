using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Strands.Agents.Mcp;
using Xunit;
using Client = Strands.Agents.Mcp.McpClient;
using TaskServer = Strands.Agents.Tests.Mcp.McpTaskClientTests.TaskServer;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpMetadataConformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullDefaultDoesNotSynthesizeUserMetadata(bool optionsPresent)
    {
        using var handler = McpLegacyTaskTests.Server();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, "ordinary", null);
        var options = optionsPresent ? new RequestOptions { Meta = null } : null;

        await client.CallToolAsync("lookup", Input(), options: options);

        var meta = Metadata(Assert.Single(handler.Requests, value => value.Method == "tools/call").Parameters);
        Assert.False(meta.TryGetProperty("tenant", out _));
        Assert.False(meta.TryGetProperty("defaultOnly", out _));
        if (options is not null) Assert.Null(options.Meta);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"wrong\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("undefined")]
    public async Task NonObjectDefaultsRejectBeforeAnyRequest(string json)
    {
        using var handler = new TaskServer();
        using var http = new HttpClient(handler);
        var value = json == "undefined" ? default : JsonSerializer.Deserialize<JsonElement>(json);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => ConnectAsync(http,
            new Uri("https://metadata.fixture.invalid/mcp"), "ordinary", value));

        Assert.Equal(nameof(McpClientSettings.DefaultToolCallMetadata), error.ParamName);
        Assert.Empty(handler.Requests);
        Assert.False(handler.Disposed);
    }

    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("ordinary", true)]
    [InlineData("legacy", false)]
    [InlineData("legacy", true)]
    [InlineData("finalized", false)]
    [InlineData("finalized", true)]
    public async Task DefaultsAndOverridesReachAllAutomaticProfiles(string profile, bool explicitMeta)
    {
        using var handler = profile == "finalized" ? new TaskServer() : McpLegacyTaskTests.Server();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, profile, DefaultMetadata());
        var supplied = new JsonObject { ["tenant"] = "explicit" };
        var options = explicitMeta ? new RequestOptions { Meta = supplied } : new RequestOptions { Meta = null };

        var result = await client.CallToolAsync("lookup", Input(), options: options);

        Assert.NotEmpty(result.Content);
        var call = Assert.Single(handler.Requests, value => value.Method == "tools/call");
        var meta = Metadata(call.Parameters);
        Assert.Equal(explicitMeta ? "explicit" : "default", meta.GetProperty("tenant").GetString());
        Assert.Equal(!explicitMeta, meta.TryGetProperty("defaultOnly", out _));
        Assert.Equal(profile == "legacy", call.Parameters.TryGetProperty("task", out _));
        if (profile == "ordinary") Assert.DoesNotContain(handler.Requests, value => value.Method.StartsWith("tasks/", StringComparison.Ordinal));
        else Assert.Single(handler.Requests, value => value.Method == "tasks/get");
        if (profile == "legacy") Assert.Single(handler.Requests, value => value.Method == "tasks/result");
        Assert.Equal("explicit", supplied["tenant"]!.GetValue<string>());
        if (!explicitMeta) Assert.Null(options.Meta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallOptionsAndNestedMetaAreCapturedBeforeLegacyDiscovery(bool explicitMeta)
    {
        using var handler = McpLegacyTaskTests.Server();
        var entered = Signal();
        var release = Signal();
        var baseline = handler.Respond!;
        handler.Respond = async (request, token) =>
        {
            if (request.Method == "tools/list")
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }
            return await baseline(request, token).ConfigureAwait(false);
        };
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        var defaults = JsonSerializer.SerializeToElement(new { tenant = new { name = "default" }, defaultOnly = true });
        await using var client = await ConnectAsync(http, server.Endpoint, "legacy", defaults);
        var supplied = new JsonObject { ["tenant"] = new JsonObject { ["name"] = "explicit" } };
        var options = new RequestOptions { Meta = explicitMeta ? supplied : null, ProgressToken = new ProgressToken("original-progress") };
        var pending = client.CallToolAsync("lookup", Input(), options: options).AsTask();
        Exception? primary = null;
        try
        {
            await Bounded(entered.Task);
            supplied["tenant"]!["name"] = "mutated-nested";
            options.Meta = new JsonObject { ["tenant"] = new JsonObject { ["name"] = "replaced" } };
            options.ProgressToken = new ProgressToken("late-progress");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try { await Bounded(pending); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["McpMetadataFixtureCleanup"] = cleanup; }
        }

        var result = await pending;
        Assert.NotEmpty(result.Content);
        var call = Assert.Single(handler.Requests, value => value.Method == "tools/call");
        var meta = Metadata(call.Parameters);
        Assert.Equal(explicitMeta ? "explicit" : "default", meta.GetProperty("tenant").GetProperty("name").GetString());
        Assert.Equal(call.Envelope.GetProperty("id").GetString(), meta.GetProperty("progressToken").GetString());
        Assert.Equal<ProgressToken?>(new ProgressToken("late-progress"), options.ProgressToken);
        Assert.Equal("replaced", options.Meta!["tenant"]!["name"]!.GetValue<string>());
        Assert.Equal(!explicitMeta, meta.TryGetProperty("defaultOnly", out _));
    }

    [Fact]
    public async Task ExplicitProgressTokenOverridesMetadataTokenWithoutMutatingBorrowedMeta()
    {
        using var handler = McpLegacyTaskTests.Server();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, "ordinary", DefaultMetadata());
        var supplied = new JsonObject { ["tenant"] = "explicit", ["progressToken"] = "metadata-progress" };
        var before = supplied.ToJsonString();
        var serializer = new JsonSerializerOptions(JsonSerializerOptions.Default);
        var options = new RequestOptions { Meta = supplied, ProgressToken = new ProgressToken("explicit-progress"), JsonSerializerOptions = serializer };

        await client.CallToolAsync("lookup", Input(), options: options);

        var meta = Metadata(Assert.Single(handler.Requests, value => value.Method == "tools/call").Parameters);
        Assert.Equal("explicit-progress", meta.GetProperty("progressToken").GetString());
        Assert.Equal("explicit", meta.GetProperty("tenant").GetString());
        Assert.Equal(before, supplied.ToJsonString());
        Assert.Same(supplied, options.Meta);
        Assert.Same(serializer, options.JsonSerializerOptions);
    }

    [Fact]
    public async Task ExplicitSubmissionDoesNotInheritClientDefault()
    {
        using var handler = new TaskServer();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, "finalized", DefaultMetadata());

        var submission = await client.SubmitToolAsync("lookup", Input());

        Assert.NotNull(submission.Task);
        Assert.Null(submission.Result);
        var meta = Metadata(Assert.Single(handler.Requests, value => value.Method == "tools/call").Parameters);
        Assert.False(meta.TryGetProperty("tenant", out _));
        Assert.False(meta.TryGetProperty("defaultOnly", out _));
        Assert.DoesNotContain(handler.Requests, value => value.Method == "tasks/get");
    }

    [Fact]
    public async Task IndependentCallOverridesNeverLeakIntoLaterDefaults()
    {
        using var handler = McpLegacyTaskTests.Server();
        await using var server = new McpTaskLoopbackServer(handler);
        using var http = new HttpClient();
        await using var client = await ConnectAsync(http, server.Endpoint, "ordinary", DefaultMetadata());
        var supplied = new JsonObject { ["tenant"] = "explicit" };

        await client.CallToolAsync("lookup", Input());
        await client.CallToolAsync("lookup", Input(), options: new RequestOptions { Meta = supplied });
        supplied["tenant"] = "changed-after-call";
        await client.CallToolAsync("lookup", Input());

        Assert.Equal(new[] { "default", "explicit", "default" }, handler.Requests.Where(value => value.Method == "tools/call")
            .Select(value => Metadata(value.Parameters).GetProperty("tenant").GetString()).ToArray());
    }

    private static Task<Client> ConnectAsync(HttpClient http, Uri endpoint, string profile, JsonElement? metadata) => Client.CreateHttpAsync(
        new McpClientSettings
        {
            DefaultToolCallMetadata = metadata,
            TasksConfig = profile == "ordinary" ? null : new McpTasksConfig
            {
                ProtocolProfile = profile == "legacy" ? McpTasksProtocolProfile.Legacy20251125 : McpTasksProtocolProfile.Finalized20260728,
                PollInterval = TimeSpan.FromMilliseconds(10)
            }
        }, http, new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp, EnableStandaloneGetStream = false },
        new McpClientOptions { ProtocolVersion = profile == "finalized" ? "2026-07-28" : "2025-11-25", InitializationTimeout = TimeSpan.FromSeconds(5) });

    private static JsonElement Input() => JsonSerializer.SerializeToElement(new { query = "value" });
    private static JsonElement DefaultMetadata() => JsonSerializer.SerializeToElement(new { tenant = "default", defaultOnly = true });
    private static JsonElement Metadata(JsonElement parameters) => parameters.TryGetProperty("_meta", out var value) ? value : JsonSerializer.SerializeToElement(new { });
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Bounded(Task pending)
    {
        if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10))) != pending) throw new TimeoutException("MCP metadata fixture did not settle.");
        await pending;
    }
}
