using System.Text.Json;
using Strands.Agents.Mcp;

namespace Strands.Agents.Tests.Mcp;

public sealed class McpToolTests
{
    [Fact]
    public async Task RichResultRetainsMediaResourcesStructuredContentAndCorrelation()
    {
        using var server = ResultServer("""
            {"content":[
              {"type":"text","text":"hello"},
              {"type":"image","mimeType":"image/png","data":"aGVsbG8="},
              {"type":"audio","mimeType":"audio/wav","data":"aGVsbG8="},
              {"type":"resource","resource":{"uri":"file:///note","mimeType":"text/plain","text":"embedded"}},
              {"type":"resource_link","uri":"https://example.test/a","name":"a","mimeType":"text/plain","description":"reference"},
              {"type":"resource","resource":{"uri":"file:///photo","mimeType":"image/jpeg","blob":"aGVsbG8="}}
            ],"structuredContent":{"count":2}}
            """);
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);
        var tool = Assert.Single(await client.ListToolsAsync(new McpToolListOptions { Prefix = "remote" }));
        var input = JsonSerializer.SerializeToElement(new { });

        var result = await tool.InvokeResultAsync(input, Context(tool, input));

        Assert.Equal("original-id", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal(2, result.StructuredContent!.Value.GetProperty("count").GetInt32());
        Assert.Collection(result.ContentBlocks,
            block => Assert.Equal("hello", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal(new ImageBlock("image/png", "aGVsbG8="), Assert.IsType<ImageBlock>(block)),
            block => Assert.Equal(new AudioBlock("audio/wav", "aGVsbG8="), Assert.IsType<AudioBlock>(block)),
            block => Assert.Equal("embedded", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal(new ResourceLinkBlock("https://example.test/a", "a", "text/plain", "reference"), Assert.IsType<ResourceLinkBlock>(block)),
            block => Assert.Equal(new ImageBlock("image/jpeg", "aGVsbG8="), Assert.IsType<ImageBlock>(block)));
        Assert.Equal("lookup", server.Requests.Last(request => request.GetProperty("method").GetString() == "tools/call").GetProperty("params").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ServerErrorStatusIsPreservedAndDoesNotRequireSuccessfulOutputSchema()
    {
        using var server = ResultServer("""{"isError":true,"content":[{"type":"text","text":"denied"}]}""");
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);
        var tool = Assert.Single(await client.ListToolsAsync());
        var input = JsonSerializer.SerializeToElement(new { });

        var result = await tool.InvokeResultAsync(input, Context(tool, input));

        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal("denied", result.Content);
    }

    [Theory]
    [InlineData("{\"content\":[]}")]
    [InlineData("{\"content\":[],\"structuredContent\":[]}")]
    public async Task SuccessfulResultsMustMatchTheDiscoveredOutputSchema(string response)
    {
        using var server = ResultServer(response);
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);
        await client.ListToolsAsync();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.CallToolAsync("lookup", JsonSerializer.SerializeToElement(new { })).AsTask());

        Assert.Contains("output schema", error.Message);
    }

    [Fact]
    public async Task UnsupportedBinaryResourcesRemainAvailableThroughTheRawResult()
    {
        using var server = ResultServer("""{"content":[{"type":"resource","resource":{"uri":"file:///binary","mimeType":"application/octet-stream","blob":"AQID"}}],"structuredContent":{}}""");
        using var http = new HttpClient(server);
        await using var client = await McpClientTests.ConnectAsync(http);
        var tool = Assert.Single(await client.ListToolsAsync());
        var input = JsonSerializer.SerializeToElement(new { });

        await Assert.ThrowsAsync<NotSupportedException>(() => tool.InvokeResultAsync(input, Context(tool, input)).AsTask());
        var raw = await client.CallToolAsync(tool.ServerName, input);

        Assert.IsType<ModelContextProtocol.Protocol.BlobResourceContents>(Assert.IsType<ModelContextProtocol.Protocol.EmbeddedResourceBlock>(Assert.Single(raw.Content)).Resource);
    }

    private static McpClientTests.FixtureServer ResultServer(string result) => new()
    {
        Handle = root => root.GetProperty("method").GetString() == "tools/call" ? JsonSerializer.Deserialize<JsonElement>(result) : null
    };

    private static ToolContext Context(McpTool tool, JsonElement input) => new(
        new Agent(new AgentOptions { Model = new AgentTests.ScriptedModel(AgentTests.Text("unused")) }),
        new ToolUseBlock("original-id", tool.Spec.Name, input), new Dictionary<string, object?>());
}
