using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class HandoffToUserEnvelopeConformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoExtraEnvelopeKeepsNullMetadataAndOmitsItsJsonField(bool empty)
    {
        var result = await Invoke(Json(empty ? "{\"status\":\"success\",\"content\":[]}" : "{\"status\":\"success\",\"content\":[{\"text\":\"answer\"}]}"));
        Assert.Null(result.Metadata);
        Assert.True(result.HasExplicitContent);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(json.TryGetProperty("metadata", out _));
    }

    [Theory]
    [InlineData("{\"status\":\"success\",\"content\":[{\"text\":null}]}")]
    [InlineData("{\"status\":\"success\",\"content\":[{\"text\":\"first\",\"json\":false}]}")]
    [InlineData("{\"status\":\"success\",\"content\":[],\"extra\":1,\"extra\":2}")]
    public async Task MalformedOrAmbiguousNativeEnvelopeNeverReturnsAPartialResult(string raw)
        => await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Invoke(Json(raw)));

    [Theory]
    [InlineData("{\"status\":\"error\",\"status\":\"success\",\"content\":[{\"text\":\"last\"}]}")]
    [InlineData("{\"status\":\"success\",\"content\":[{\"text\":\"first\"}],\"content\":[{\"text\":\"last\"}]}")]
    public async Task NativeJsonKnownPropertyLookupUsesTheLastValue(string raw)
    {
        var result = await Invoke(Json(raw));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("last", result.Content);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public async Task ExtraKeyNamesRemainOrdinalAndOriginalMetadataStaysNested()
    {
        var result = await Invoke(Json("{\"status\":\"success\",\"content\":[],\"Metadata\":null,\"metadata\":{\"nested\":true},\"RAW\":[1,2]}"));
        var extra = result.Metadata!.Value;
        Assert.Equal(3, extra.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, extra.GetProperty("Metadata").ValueKind);
        Assert.True(extra.GetProperty("metadata").GetProperty("nested").GetBoolean());
        Assert.Equal(2, extra.GetProperty("RAW").GetArrayLength());
        Assert.False(extra.TryGetProperty("raw", out _));
    }

    [Fact]
    public async Task NestedExtrasAreDetachedBeforeTheAnswerDocumentIsDisposed()
    {
        ToolResultBlock result;
        using (var document = JsonDocument.Parse("{\"status\":\"success\",\"content\":[],\"metadata\":{\"array\":[1,null,{\"unicode\":\"h\u00e9llo\"}]}}"))
            result = await Invoke(document.RootElement);
        var array = result.Metadata!.Value.GetProperty("metadata").GetProperty("array");
        Assert.Equal(JsonValueKind.Null, array[1].ValueKind);
        Assert.Equal("h\u00e9llo", array[2].GetProperty("unicode").GetString());
        var copied = new Message(Role.User, [result]);
        Assert.Equal("h\u00e9llo", Assert.IsType<ToolResultBlock>(Assert.Single(copied.Content)).Metadata!.Value.GetProperty("metadata").GetProperty("array")[2].GetProperty("unicode").GetString());
    }

    [Fact]
    public async Task ProtocolLookingExtraNamesDoNotAcquireAutomaticProtocolSemantics()
    {
        var result = await Invoke(Json("{\"status\":\"success\",\"content\":[{\"text\":\"kept\"}],\"structuredContent\":{\"answer\":42},\"_meta\":{\"progressToken\":7}}"));
        Assert.Null(result.StructuredContent);
        Assert.Equal("kept", Assert.IsType<TextBlock>(Assert.Single(result.ContentBlocks)).Text);
        Assert.Equal(42, result.Metadata!.Value.GetProperty("structuredContent").GetProperty("answer").GetInt32());
        Assert.Equal(7, result.Metadata.Value.GetProperty("_meta").GetProperty("progressToken").GetInt32());
    }

    [Fact]
    public async Task FreshSnapshotFileRetainsNestedExtraJsonAndCurrentCorrelation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "handoff-extras-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await Invoke(Json("{\"status\":\"error\",\"content\":[],\"toolUseId\":\"foreign\",\"metadata\":{\"human\":true},\"extra\":[null,1.25]}"));
            await new FileSessionStore(directory).SaveAsync("session", new(1, History(result)));
            var restored = (await new FileSessionStore(directory).LoadAsync("session"))!;
            var saved = Assert.IsType<ToolResultBlock>(Assert.Single(restored.Messages[2].Content));
            Assert.Equal("current", saved.ToolUseId);
            Assert.Equal(ToolResultStatus.Error, saved.Status);
            Assert.True(saved.HasExplicitContent);
            Assert.Empty(saved.ContentBlocks);
            Assert.True(JsonElement.DeepEquals(result.Metadata!.Value, saved.Metadata!.Value));
            Assert.True(saved.Metadata.Value.GetProperty("metadata").GetProperty("human").GetBoolean());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ActualProviderProjectionExcludesOpaqueEnvelopeMetadata()
    {
        var result = await Invoke(Json("{\"status\":\"success\",\"content\":[{\"text\":\"kept\"}],\"metadata\":{\"private\":\"must-stay-local\"},\"structuredContent\":{\"private\":\"must-stay-local\"}}"));
        using var handler = new CaptureOnlyHandler();
        using var client = new HttpClient(handler);
        var model = new AnthropicModel(client, new("claude-fixture", "fixed-dummy-key") { Endpoint = new Uri("https://capture.invalid/messages") });
        await Assert.ThrowsAnyAsync<ModelException>(async () => { await foreach (var _ in model.StreamAsync(new(History(result)))) { } });
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("must-stay-local", handler.Body!);
        Assert.DoesNotContain("structuredContent", handler.Body!);
        var body = Json(handler.Body!);
        Assert.Equal("kept", body.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("content").GetString());
    }

    private static Message[] History(ToolResultBlock result) => [Message.User("go"), new(Role.Assistant, [new ToolUseBlock("current", "handoff_to_user", Json("{}"))]), new(Role.User, [result])];
    private static ValueTask<ToolResultBlock> Invoke(JsonElement reply)
        => HandoffToUser.Create(new() { Behavior = HandoffToUserBehavior.Python }).InvokeResultAsync(Json("{\"message\":\"Q\"}"), new(null!, new("current", "handoff_to_user", Json("{}")), new Dictionary<string, object?>()) { InterruptHandler = (_, _, _) => reply });
    private static JsonElement Json(string raw) { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
    private sealed class CaptureOnlyHandler : HttpMessageHandler
    {
        internal int Calls;
        internal string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"controlled request capture\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
