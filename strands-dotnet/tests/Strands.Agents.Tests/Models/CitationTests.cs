using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.Models;

public sealed class CitationTests
{
    [Fact]
    public async Task StreamingCitationsAggregateWithGeneratedTextAndSurviveSnapshots()
    {
        var sources = new List<Citation> { new(new WebCitationLocation("https://example.test/source", "example.test"), "https://example.test/source", [new("evidence")], "Title") };
        var delta = new ModelContentBlockDeltaEvent(0) { Citations = sources };
        sources.Clear();
        await using var agent = new Agent(new()
        {
            Model = new EventsModel(new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "Answer"), delta, new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn))
        });

        var result = await agent.InvokeAsync("question");

        var block = Assert.IsType<CitationsBlock>(Assert.Single(result.Message!.Content));
        Assert.Equal("Answer", result.Message.Text);
        Assert.Equal("evidence", Assert.Single(Assert.Single(block.Citations).SourceContent).Text);
        var restored = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(result.Message))!;
        var citation = Assert.Single(Assert.IsType<CitationsBlock>(Assert.Single(restored.Content)).Citations);
        Assert.Equal("example.test", Assert.IsType<WebCitationLocation>(citation.Location).Domain);
        Assert.Equal("Answer", restored.Text);
    }

    [Fact]
    public async Task CitationDeltasCannotBeAttachedToReasoning()
    {
        await using var agent = new Agent(new()
        {
            Model = new EventsModel(new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Reasoning: "private"), new ModelContentBlockDeltaEvent(0) { Citations = [new(new WebCitationLocation("source"))] }, new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn))
        });

        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("question"));
        Assert.DoesNotContain(agent.Messages, message => message.Role == Role.Assistant);
    }

    [Theory]
    [InlineData("documentChar")]
    [InlineData("documentPage")]
    [InlineData("documentChunk")]
    [InlineData("searchResult")]
    [InlineData("web")]
    public void LocationVariantsRoundTripThroughThePublicPolymorphicContract(string type)
    {
        CitationLocation location = type switch
        {
            "documentChar" => new DocumentCharCitationLocation(1, 2, 3),
            "documentPage" => new DocumentPageCitationLocation(1, 2, 3),
            "documentChunk" => new DocumentChunkCitationLocation(1, 2, 3),
            "searchResult" => new SearchResultCitationLocation(1, 2, 3),
            _ => new WebCitationLocation("https://example.test")
        };
        Assert.Equal(location, JsonSerializer.Deserialize<CitationLocation>(JsonSerializer.Serialize(location)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ProviderNeutralCitationTextCanBeForwardedToEveryHttpModel(int provider)
    {
        using var handler = new Capture();
        using var client = new HttpClient(handler);
        IModel model = provider switch
        {
            0 => new OpenAIModel(client, new("model", "key")),
            1 => new OpenAIResponsesModel(client, new("model", "key")),
            2 => new GoogleModel(client, new("model", "key")),
            3 => new OllamaModel(client, new("model")),
            _ => new AnthropicModel(client, new("model", "key"))
        };
        var block = new CitationsBlock([new(new WebCitationLocation("https://example.test/source"), "private-source")], [new("Answer")]);

        var error = await Assert.ThrowsAsync<ModelException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new([new Message(Role.Assistant, [block]), Message.User("next")]))) { }
        });

        Assert.Same(handler.Error, error.InnerException);
        Assert.Contains("Answer", handler.Body);
        Assert.DoesNotContain("private-source", handler.Body);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestException Error { get; } = new("Captured request");
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync();
            throw Error;
        }
    }

    private sealed class EventsModel(params ModelStreamEvent[] events) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
}
