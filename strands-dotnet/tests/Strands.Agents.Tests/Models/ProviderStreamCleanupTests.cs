using System.Net;
using System.Net.Http;
using System.Text;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class ProviderStreamCleanupTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task MalformedAcceptedContentKeepsItsParseFailureWhenClosingTheBodyAlsoFails(int provider)
    {
        var body = new ThrowingBody(provider == 4 ? "{broken\n" : "data: {broken\n\n");
        using var handler = new Handler(body, provider == 4 ? "application/x-ndjson" : "text/event-stream");
        using var client = new HttpClient(handler);
        IModel model = provider switch
        {
            0 => new OpenAIModel(client, new("model", "key")),
            1 => new AnthropicModel(client, new("model", "key")),
            2 => new GoogleModel(client, new("model", "key")),
            3 => new OpenAIResponsesModel(client, new("model", "key")),
            4 => new OllamaModel(client, new("model")),
            5 => new MistralModel(client, new("model", "key")),
            _ => new WriterModel(client, new("model", "key"))
        };

        var error = await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new([Message.User("question")]))) { }
        });

        Assert.Contains("JSON", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(body.Failure, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(1, handler.Calls);
        Assert.True(body.Disposed);
    }

    private sealed class Handler(Stream body, string mediaType) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var content = new StreamContent(body);
            content.Headers.ContentType = new(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class ThrowingBody(string value) : MemoryStream(Encoding.UTF8.GetBytes(value))
    {
        public IOException Failure { get; } = new("accepted content disposal failed");
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); throw Failure; }
    }
}
