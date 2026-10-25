using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class MediaSourceTests
{
    public static IEnumerable<object[]> UnsupportedRemoteSources()
    {
        foreach (var provider in new[] { "openai", "responses", "anthropic", "google", "ollama", "mistral", "writer", "llamacpp" })
            foreach (var media in new[] { "image", "audio", "document" })
                foreach (var toolResult in new[] { false, true }) yield return [provider, media, toolResult];
    }

    [Theory]
    [MemberData(nameof(UnsupportedRemoteSources))]
    public async Task UnsupportedS3SourcesFailBeforeHttpInsteadOfSendingEmptyBytes(string provider, string media, bool toolResult)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var model = Create(provider, client);
        var location = new S3Location("s3://media-bucket/original");
        ContentBlock source = media switch { "image" => new ImageBlock("image/png", location), "audio" => new AudioBlock("audio/wav", location), _ => new DocumentBlock("application/pdf", location) };
        var content = toolResult ? new ToolResultBlock("call", [source]) : source;
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Collect(model, new([new(Role.User, [content])])));
            Assert.Null(handler.Body);
        }
        finally { (model as IDisposable)?.Dispose(); }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("responses")]
    [InlineData("anthropic")]
    [InlineData("mistral")]
    [InlineData("writer")]
    [InlineData("llamacpp")]
    public async Task SupportedImageUrlsReachProviderRequests(string provider)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var model = Create(provider, client);
        var url = new Uri("https://images.example/photo.png?signature=original%2Fbytes");
        try
        {
            await Assert.ThrowsAsync<ModelException>(() => Collect(model, new([new(Role.User, [new ImageBlock("image/png", url)])])));
            Assert.Contains(url.AbsoluteUri, handler.Body!.Value.GetRawText().Replace("\\u0026", "&"));
            Assert.DoesNotContain("data:image/png", handler.Body.Value.GetRawText());
        }
        finally { (model as IDisposable)?.Dispose(); }
    }

    [Fact]
    public async Task GoogleInlineVideoReachesGenerateContentAsNativeMedia()
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var model = new GoogleModel(client, new("model", "key"));

        await Assert.ThrowsAsync<ModelException>(() => Collect(model, new([new(Role.User, [new VideoBlock("video/mp4", "AQID")])])));

        var data = handler.Body!.Value.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("inlineData");
        Assert.Equal("video/mp4", data.GetProperty("mimeType").GetString());
        Assert.Equal("AQID", data.GetProperty("data").GetString());
    }

    [Theory]
    [InlineData("mistral", false)]
    [InlineData("mistral", true)]
    [InlineData("writer", false)]
    [InlineData("writer", true)]
    [InlineData("writer-text", false)]
    [InlineData("writer-text", true)]
    public async Task GuardSelectionCannotBeSilentlyDroppedByTextOnlyProviderProjections(string provider, bool rich)
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var model = provider == "writer-text" ? new WriterModel(client, new("model", "key")) : Create(provider, client);
        ContentBlock[] selected = [new TextBlock("visible"), new GuardContentBlock("screened")];
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Collect(model, new([new(Role.User, rich ? [new ToolResultBlock("call", selected)] : selected)])));
            Assert.Null(handler.Body);
        }
        finally { (model as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void AmbiguousMediaSourcesAndInvalidImageUrlsAreRejectedAtMessageBoundary()
    {
        var location = new S3Location("s3://media-bucket/key");
        ContentBlock[] invalid = [new ImageBlock("image/png", "AQID") { Location = location }, new ImageBlock("image/png", location) { Url = new("https://example.com/image") }, new AudioBlock("audio/wav", "AQID") { Location = location }, new DocumentBlock("application/pdf", "AQID") { Location = location }, new VideoBlock("video/mp4", "AQID") { Location = location }, new ImageBlock("image/png", new Uri("file:///private.png"))];
        Assert.All(invalid, block => Assert.Throws<ArgumentException>(() => new Message(Role.User, [block])));
        Assert.Equal("", Assert.IsType<VideoBlock>(Assert.Single(new Message(Role.User, [new VideoBlock("video/mp4", "")]).Content)).Data);
    }

    internal static IModel Create(string provider, HttpClient client) => provider switch
    {
        "openai" => new OpenAIModel(client, new("model", "key")),
        "responses" => new OpenAIResponsesModel(client, new("model", "key")),
        "anthropic" => new AnthropicModel(client, new("model", "key")),
        "google" => new GoogleModel(client, new("model", "key")),
        "ollama" => new OllamaModel(client, new("model")),
        "mistral" => new MistralModel(client, new("model", "key")),
        "writer" => new WriterModel(client, new("palmyra-x5", "key")),
        _ => new LlamaCppModel(client, new("model"))
    };

    internal static async Task Collect(IModel model, ModelRequest request)
    {
        await foreach (var _ in model.StreamAsync(request)) { }
    }

    internal sealed class CaptureHandler : HttpMessageHandler
    {
        public JsonElement? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"fixture response\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
