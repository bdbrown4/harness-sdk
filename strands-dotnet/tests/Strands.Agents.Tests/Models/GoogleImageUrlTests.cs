using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class GoogleImageUrlTests
{
    [Theory]
    [InlineData("https://images.example.test/image.png?size=2&token=a%2Fb")]
    [InlineData("https://images.example.test/caf\u00e9/\u6c34.png?caption=\u00e9")]
    [InlineData("http://images.example.test/image.png#section")]
    public async Task OrdinaryTypedUrlReachesTheModelAsFileDataWithoutFetchingIt(string value)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var url = new Uri(value);
        await Collect(new GoogleModel(client, new("model", "key")), new([new(Role.User, [new ImageBlock("image/png", url)])]));

        var call = Assert.Single(handler.Calls);
        Assert.Equal("generativelanguage.googleapis.com", call.Endpoint.Host);
        var part = call.Body.GetProperty("contents")[0].GetProperty("parts")[0];
        Assert.Single(part.EnumerateObject());
        Assert.Equal(url.AbsoluteUri, part.GetProperty("fileData").GetProperty("fileUri").GetString());
        Assert.Equal("image/png", part.GetProperty("fileData").GetProperty("mimeType").GetString());
        Assert.Equal(1, handler.ResponseDisposals);
        Assert.False(handler.Disposed);
        Assert.False(client.DefaultRequestHeaders.Contains("x-goog-api-key"));
    }

    [Fact]
    public async Task TextInlineBytesAndUrlKeepTheirOriginalPartOrder()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        await Collect(new GoogleModel(client, new("model", "key")), new([new(Role.User,
            [new TextBlock("before"), new ImageBlock("image/jpeg", "AQID"), new ImageBlock("image/webp", new Uri("https://images.example.test/view.webp")), new TextBlock("after")])]));

        var parts = Assert.Single(handler.Calls).Body.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(4, parts.GetArrayLength());
        Assert.Equal("before", parts[0].GetProperty("text").GetString());
        Assert.Equal("AQID", parts[1].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("image/jpeg", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal("image/webp", parts[2].GetProperty("fileData").GetProperty("mimeType").GetString());
        Assert.Equal("after", parts[3].GetProperty("text").GetString());
    }

    [Fact]
    public async Task NativeTokenCountingUsesTheSameTypedUrlProjection()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key") { UseNativeTokenCount = true });
        var count = await model.CountTokensAsync(new([new(Role.User, [new ImageBlock("image/png", new Uri("https://images.example.test/count.png"))])]));

        Assert.Equal(7, count);
        var call = Assert.Single(handler.Calls);
        Assert.EndsWith(":countTokens", call.Endpoint.AbsolutePath);
        Assert.Equal("https://images.example.test/count.png", call.Body.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("fileData").GetProperty("fileUri").GetString());
    }

    [Fact]
    public async Task StructuredOutputUsesTheSameTypedUrlProjection()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var options = new StructuredOutputOptions<Answer>
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
        };
        var value = await new GoogleModel(client, new("model", "key")).InvokeStructuredAsync(new([new(Role.User, [new ImageBlock("image/png", new Uri("https://images.example.test/structured.png"))])]), options);

        Assert.Equal(42, value.Value);
        var call = Assert.Single(handler.Calls);
        Assert.EndsWith(":generateContent", call.Endpoint.AbsolutePath);
        Assert.Equal("https://images.example.test/structured.png", call.Body.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("fileData").GetProperty("fileUri").GetString());
    }

    [Fact]
    public async Task FunctionResponseUrlsRemainUnsupportedAndDoNotSendARequest()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        var result = new ToolResultBlock("call", [new ImageBlock("image/png", new Uri("https://images.example.test/result.png"))]);
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(new GoogleModel(client, new("model", "key")), new([new(Role.User, [result])])));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task S3ImagesRemainUnsupportedAndDoNotSendARequest()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() => Collect(new GoogleModel(client, new("model", "key")), new([new(Role.User, [new ImageBlock("image/png", new S3Location("s3://media-bucket/image.png"))])])));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void ContradictoryByteAndUrlSourcesFailAtTheMessageBoundary()
    {
        Assert.Throws<ArgumentException>(() => new Message(Role.User, [new ImageBlock("image/png", "AQID") { Url = new("https://images.example.test/image.png") }]));
    }

    [Fact]
    public async Task RawGoogleFileDataRemainsDetachedAndUnchanged()
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        using var document = JsonDocument.Parse("""{"fileData":{"fileUri":"gs://bucket/object","mimeType":"image/png"},"customMarker":17}""");
        var original = document.RootElement.Clone();
        var message = new Message(Role.User, [new GoogleContentBlock(document.RootElement)]);
        document.Dispose();
        await Collect(new GoogleModel(client, new("model", "key")), new([message]));
        Assert.True(JsonElement.DeepEquals(original, Assert.Single(handler.Calls).Body.GetProperty("contents")[0].GetProperty("parts")[0]));
    }

    private static async Task Collect(IModel model, ModelRequest request)
    {
        await foreach (var unused in model.StreamAsync(request)) { }
    }

    private sealed record Answer(int Value);
    private sealed record Call(Uri Endpoint, JsonElement Body);
    private sealed class Capture : HttpMessageHandler
    {
        internal List<Call> Calls { get; } = [];
        internal int ResponseDisposals;
        internal bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Calls.Add(new(request.RequestUri!, body.RootElement.Clone()));
            var count = request.RequestUri!.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal);
            var structured = request.RequestUri.AbsolutePath.EndsWith(":generateContent", StringComparison.Ordinal);
            var text = count ? "{\"totalTokens\":7}" : structured
                ? """{"candidates":[{"content":{"parts":[{"text":"{\"value\":42}"}]}}]}"""
                : "data: {\"candidates\":[{\"index\":0,\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n";
            return new(HttpStatusCode.OK) { Content = new Content(text, count || structured ? "application/json" : "text/event-stream", () => ResponseDisposals++) };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Content(string text, string mediaType, Action disposed) : StringContent(text, Encoding.UTF8, mediaType)
    {
        private bool _disposed;
        protected override void Dispose(bool disposing) { var first = !_disposed; _disposed = true; base.Dispose(disposing); if (first) disposed(); }
    }
}
