using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using Xunit;

namespace Strands.Agents.Tests.Models;

public sealed class GoogleExplicitEmptyToolResultTests
{
    [Theory]
    [InlineData("stream", false)]
    [InlineData("count", false)]
    [InlineData("structured", false)]
    [InlineData("stream", true)]
    [InlineData("count", true)]
    [InlineData("structured", true)]
    public async Task ExplicitEmptyContentDoesNotInventTextOnAnyRequestPath(string path, bool error)
    {
        var result = new ToolResultBlock("call", "unrelated-display-fallback", error ? ToolResultStatus.Error : ToolResultStatus.Success)
        { HasExplicitContent = true, Metadata = JsonSerializer.SerializeToElement(new { privateMetadata = true }) };

        var response = await CaptureAsync(path, result);

        Assert.Empty(response.GetProperty("response").GetProperty(error ? "error" : "output").EnumerateArray());
        Assert.Equal("call", response.GetProperty("id").GetString());
        Assert.Equal("lookup", response.GetProperty("name").GetString());
        Assert.False(response.TryGetProperty("parts", out _));
        Assert.DoesNotContain("unrelated-display-fallback", response.GetRawText());
        Assert.DoesNotContain("privateMetadata", response.GetRawText());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"value\":7}")]
    public async Task ExplicitEmptyContentRetainsSeparateStructuredContent(string json)
    {
        ToolResultBlock result;
        using (var document = JsonDocument.Parse(json))
            result = new ToolResultBlock("call", "unrelated-display-fallback")
            { HasExplicitContent = true, StructuredContent = document.RootElement.Clone() };

        var response = await CaptureAsync("stream", result);

        var output = Assert.Single(response.GetProperty("response").GetProperty("output").EnumerateArray());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(json), output.GetProperty("json")));
        Assert.DoesNotContain("unrelated-display-fallback", response.GetRawText());
    }

    [Fact]
    public async Task LegacyTextFallbackRemainsAvailableWhenContentWasNotExplicit()
    {
        var response = await CaptureAsync("stream", new ToolResultBlock("call", "legacy-display"));

        var output = Assert.Single(response.GetProperty("response").GetProperty("output").EnumerateArray());
        Assert.Equal("legacy-display", output.GetProperty("text").GetString());
    }

    [Fact]
    public async Task NonemptyRichContentKeepsItsExistingPrecedence()
    {
        var result = new ToolResultBlock("call", "unrelated-display-fallback")
        { ContentBlocks = [new TextBlock("rich-output")] };

        var response = await CaptureAsync("stream", result);

        var output = Assert.Single(response.GetProperty("response").GetProperty("output").EnumerateArray());
        Assert.Equal("rich-output", output.GetProperty("text").GetString());
    }

    private static async Task<JsonElement> CaptureAsync(string path, ToolResultBlock result)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key") { UseNativeTokenCount = true });
        var request = new ModelRequest([
            Message.User("start"),
            new(Role.Assistant, [new ToolUseBlock("call", "lookup", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [result])]);
        if (path == "stream")
        {
            await foreach (var unused in model.StreamAsync(request)) { }
        }
        else if (path == "count") Assert.Equal(7, await model.CountTokensAsync(request));
        else
        {
            var options = new StructuredOutputOptions<Answer>
            {
                Schema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { value = new { type = "integer" } },
                    required = new[] { "value" },
                    additionalProperties = false
                })
            };
            Assert.Equal(42, (await model.InvokeStructuredAsync(request, options)).Value);
        }
        var body = Assert.Single(handler.Bodies);
        var contents = body.GetProperty("contents");
        return contents[contents.GetArrayLength() - 1].GetProperty("parts")[0].GetProperty("functionResponse").Clone();
    }

    private sealed record Answer(int Value);

    private sealed class Capture : HttpMessageHandler
    {
        internal List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Bodies.Add(body.RootElement.Clone());
            var stream = request.Headers.Accept.Any(value => value.MediaType == "text/event-stream");
            var count = request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal);
            var text = stream
                ? "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n"
                : count ? "{\"totalTokens\":7}"
                : "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"value\\\":42}\"}]},\"finishReason\":\"STOP\"}]}";
            return new(HttpStatusCode.OK)
            { Content = new StringContent(text, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
}
