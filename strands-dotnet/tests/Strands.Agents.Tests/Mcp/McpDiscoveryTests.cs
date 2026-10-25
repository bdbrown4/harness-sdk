using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Mcp;
using Strands.Agents.SchemaImplementation;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpDiscoveryTests
{
    [Fact]
    public async Task ContinueOnErrorWarnsAndSkipsOnlyAnOverlongPrefixedName()
    {
        var longName = new string('x', 60);
        using var server = new McpClientTests.FixtureServer
        {
            Handle = _ => new { tools = new[] { new { name = longName, inputSchema = new { type = "object" } }, new { name = "keep", inputSchema = new { type = "object" } } } }
        };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);
        using var warnings = new StringWriter();
        using var listener = new TextWriterTraceListener(warnings);
        Trace.Listeners.Add(listener);
        try
        {
            var tools = await client.ListToolsAsync(new McpToolListOptions { Prefix = "remote", ContinueOnError = true });

            Assert.Equal("remote_keep", Assert.Single(tools).Spec.Name);
            Assert.Contains(longName, warnings.ToString());
            Assert.Contains("skipping", warnings.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task ContinueOnErrorDoesNotHideInvalidOutputSchemaBehindAnOverlongName()
    {
        using var server = new McpClientTests.FixtureServer
        {
            Handle = _ => new { tools = new[] { new { name = new string('x', 60), inputSchema = new { type = "object" }, outputSchema = new { type = 42 } } } }
        };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);

        var error = await Assert.ThrowsAsync<JsonSchemaException>(() => client.ListToolsAsync(new McpToolListOptions { Prefix = "remote", ContinueOnError = true }));
        Assert.Contains("'type'", error.Message);
    }

    [Theory]
    [InlineData(null, 65, true, false)]
    [InlineData("remote", 60, false, false)]
    [InlineData("remote", 60, true, true)]
    public async Task UnprefixedOverlengthStrictModeAndInvalidCharactersRemainFatal(string? prefix, int length, bool continueOnError, bool invalidCharacter)
    {
        var name = new string('x', length) + (invalidCharacter ? " " : "");
        using var server = new McpClientTests.FixtureServer { Handle = _ => new { tools = new[] { new { name, inputSchema = new { type = "object" } } } } };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListToolsAsync(new McpToolListOptions { Prefix = prefix, ContinueOnError = continueOnError }));
    }

    [Fact]
    public async Task FilteringRunsBeforePrefixedLengthValidation()
    {
        using var server = new McpClientTests.FixtureServer
        {
            Handle = _ => new { tools = new[] { new { name = new string('x', 60), inputSchema = new { type = "object" } }, new { name = "keep", inputSchema = new { type = "object" } } } }
        };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);

        var tools = await client.ListToolsAsync(new McpToolListOptions { Prefix = "remote", ToolFilter = name => name == "keep" });

        Assert.Equal("remote_keep", Assert.Single(tools).Spec.Name);
    }

    [Fact]
    public async Task ContinueOnErrorDoesNotHideProtocolFailures()
    {
        var failure = new IOException("tools/list transport failed");
        using var server = new McpClientTests.FixtureServer { Handle = _ => throw failure };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);

        var error = await Assert.ThrowsAsync<IOException>(() => client.ListToolsAsync(new McpToolListOptions { Prefix = "remote", ContinueOnError = true }));

        Assert.Same(failure, error);
    }

    [Fact]
    public async Task NonStringPaginationCursorIsReportedAsMalformedProtocolData()
    {
        using var server = new McpClientTests.FixtureServer { Handle = _ => new { tools = Array.Empty<object>(), nextCursor = 3 } };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListToolsAsync());

        Assert.Contains("nextCursor", error.Message);
    }

    [Fact]
    public async Task PaginationPreservesOrderAndFiltersOriginalNamesBeforePrefixing()
    {
        var page = 0;
        using var server = new McpClientTests.FixtureServer
        {
            Handle = _ => JsonSerializer.Deserialize<JsonElement>(page++ == 0
                ? """{"tools":[{"name":"skip","inputSchema":{"type":"object"}}],"nextCursor":"page-2"}"""
                : """{"tools":[{"name":"keep","inputSchema":{"type":"object"}}]}""")
        };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);
        var namesSeenByFilter = new List<string>();

        var tools = await client.ListToolsAsync(new McpToolListOptions
        {
            Prefix = "remote",
            ToolFilter = name => { namesSeenByFilter.Add(name); return name == "keep"; }
        });

        Assert.Equal(["skip", "keep"], namesSeenByFilter);
        Assert.Equal("remote_keep", Assert.Single(tools).Spec.Name);
        var requests = server.Requests.Where(request => request.GetProperty("method").GetString() == "tools/list").ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("page-2", requests[1].GetProperty("params").GetProperty("cursor").GetString());
    }

    [Theory]
    [InlineData("{\"tools\":[],\"nextCursor\":\"again\"}", 3, 10, "cursor")]
    [InlineData("{\"tools\":[],\"nextCursor\":\"again\"}", 1, 10, "page limit")]
    [InlineData("{\"tools\":[{\"name\":\"a\",\"inputSchema\":{\"type\":\"object\"}},{\"name\":\"b\",\"inputSchema\":{\"type\":\"object\"}}]}", 2, 1, "tool-count")]
    [InlineData("{\"tools\":[{\"name\":\"a\",\"inputSchema\":{\"type\":\"object\"}},{\"name\":\"a\",\"inputSchema\":{\"type\":\"object\"}}]}", 2, 10, "duplicate")]
    [InlineData("{\"tools\":{}}", 2, 10, "tools array")]
    public async Task DiscoveryRejectsUnboundedOrMalformedResults(string response, int maximumPages, int maximumTools, string message)
    {
        using var server = new McpClientTests.FixtureServer { Handle = _ => JsonSerializer.Deserialize<JsonElement>(response) };
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListToolsAsync(new McpToolListOptions
        {
            MaximumPages = maximumPages,
            MaximumTools = maximumTools,
            ToolFilter = _ => false
        }));

        Assert.Contains(message, error.Message);
    }
}
