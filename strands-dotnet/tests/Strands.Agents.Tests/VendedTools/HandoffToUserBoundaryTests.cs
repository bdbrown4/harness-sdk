using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class HandoffToUserBoundaryTests
{
    [Theory]
    [InlineData(HandoffToUserBehavior.TypeScript, "image")]
    [InlineData(HandoffToUserBehavior.TypeScript, "video")]
    [InlineData(HandoffToUserBehavior.TypeScript, "document")]
    [InlineData(HandoffToUserBehavior.Python, "image")]
    [InlineData(HandoffToUserBehavior.Python, "video")]
    [InlineData(HandoffToUserBehavior.Python, "document")]
    public async Task SourceShapedTypedMediaRetainsItsValuesWithoutFetching(HandoffToUserBehavior behavior, string kind)
    {
        var content = Json(kind switch
        {
            "image" => "{\"image\":{\"format\":\"png\",\"source\":{\"url\":\"https://example.invalid/p.png\"}}}",
            "video" => "{\"video\":{\"format\":\"mp4\",\"source\":{\"bytes\":\"AAEC\"}}}",
            _ => "{\"document\":{\"format\":\"txt\",\"name\":\"note\",\"source\":{\"text\":\"h\u00e9llo\"},\"context\":\"reference\",\"citations\":{\"enabled\":true}}}",
        });
        var reply = behavior == HandoffToUserBehavior.TypeScript ? content : JsonSerializer.SerializeToElement(new { status = "success", content = new[] { content } });
        var result = await HandoffToUser.Create(new() { Behavior = behavior }).InvokeResultAsync(Input(), Context(reply));
        var block = Assert.Single(result.ContentBlocks);
        Assert.Equal("handoff-use", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, result.Status);
        switch (kind)
        {
            case "image":
                var image = Assert.IsType<ImageBlock>(block);
                Assert.Equal("image/png", image.MediaType);
                Assert.Equal("https://example.invalid/p.png", image.Url!.AbsoluteUri);
                Assert.Equal("", image.Data);
                break;
            case "video":
                var video = Assert.IsType<VideoBlock>(block);
                Assert.Equal("video/mp4", video.MediaType);
                Assert.Equal("AAEC", video.Data);
                break;
            default:
                var document = Assert.IsType<DocumentBlock>(block);
                Assert.Equal("text/plain", document.MediaType);
                Assert.Equal("h\u00e9llo", document.Text);
                Assert.Equal("note", document.Name);
                Assert.Equal("reference", document.Context);
                Assert.True(document.CitationsEnabled);
                break;
        }
    }

    [Fact]
    public async Task PythonEmptyEnvelopeRemainsAnEmptyAnthropicContentArray()
    {
        var result = await HandoffToUser.Create(new() { Behavior = HandoffToUserBehavior.Python }).InvokeResultAsync(Input(), Context(Json("{\"status\":\"success\",\"content\":[]}")));
        Assert.Empty(result.ContentBlocks);
        using var handler = new CaptureOnlyHandler();
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-fixture", "fixed-dummy-key") { Endpoint = new Uri("https://capture.invalid/messages") });
        var request = new ModelRequest([
            Message.User("go"),
            new Message(Role.Assistant, [new ToolUseBlock("handoff-use", "handoff_to_user", Input())]),
            new Message(Role.User, [result]),
        ]);
        await Assert.ThrowsAnyAsync<ModelException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(request)) { }
        });
        Assert.Equal(1, handler.Calls);
        var content = handler.Body!.Value.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Empty(content.EnumerateArray());
    }

    [Theory]
    [InlineData("{\"text\":\"existing control\"}", "text")]
    [InlineData("{\"json\":{\"ok\":true}}", "json")]
    public async Task ExistingTextAndJsonTypedProjectionRemainControls(string raw, string kind)
    {
        var result = await HandoffToUser.Create().InvokeResultAsync(Input(), Context(Json(raw)));
        var block = Assert.Single(result.ContentBlocks);
        if (kind == "text") Assert.Equal("existing control", Assert.IsType<TextBlock>(block).Text);
        else Assert.True(Assert.IsType<JsonBlock>(block).Json.GetProperty("ok").GetBoolean());
    }

    private static JsonElement Input() => Json("{\"message\":\"Question?\"}");
    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private static ToolContext Context(JsonElement reply) => new(null!, new("handoff-use", "handoff_to_user", Input()), new Dictionary<string, object?>()) { InterruptHandler = (_, _, _) => reply };
    private sealed class CaptureOnlyHandler : HttpMessageHandler
    {
        internal int Calls;
        internal JsonElement? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Body = Json(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"controlled request capture\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
