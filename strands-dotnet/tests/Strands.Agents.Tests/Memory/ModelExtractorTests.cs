using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Memory;

namespace Strands.Agents.Tests.Memory;

public sealed class ModelExtractorTests
{
    [Fact]
    public async Task ExplicitModelAndPromptWinAndOnlyVisibleTextAppearsInTheRoleLabeledTranscript()
    {
        var model = new Model("before\n```json\n[{\"content\":\" Prefers C# \",\"metadata\":{\"source\":\"user\"}},{\"content\":\" \"},{\"wrong\":true},null,{\"content\":\"Native NuGet\",\"metadata\":42}]\n```\nafter");
        var fallback = new Model("[]");
        var extractor = new ModelExtractor(new() { Model = model, SystemPrompt = "Durable preferences only." });
        var messages = new[]
        {
            Message.User("I use C#"),
            new Message(Role.Assistant, [new TextBlock("Noted."), new ReasoningBlock("hidden"), new TextBlock("NuGet works."), new ToolUseBlock("call", "ignored", JsonSerializer.SerializeToElement(new { secret = "tool-input" }))]),
            new Message(Role.User, [new ToolResultBlock("call", "tool-result")])
        };

        var entries = await extractor.ExtractAsync(messages, new(fallback));

        Assert.Equal(["Prefers C#", "Native NuGet"], entries.Select(entry => entry.Content));
        Assert.Equal("user", entries[0].Metadata!.Value.GetProperty("source").GetString());
        Assert.Null(entries[1].Metadata);
        var request = Assert.Single(model.Requests);
        Assert.Equal("Durable preferences only.", request.SystemPrompt);
        Assert.Equal("Extract facts from the following transcript:\n\nuser: I use C#\nassistant: Noted.\nNuGet works.\nuser: ", Assert.Single(request.Messages).Text);
        Assert.Empty(fallback.Requests);
    }

    [Theory]
    [InlineData("no array")]
    [InlineData("[invalid]")]
    [InlineData("[]")]
    [InlineData("[{\"content\":false}]")]
    public async Task MalformedOrEmptyOutputProducesNoFacts(string output)
    {
        var extractor = new ModelExtractor(new() { Model = new Model(output) });
        Assert.Empty(await extractor.ExtractAsync([Message.User("hello")]));
    }

    [Fact]
    public async Task AuxiliaryFallbackAndEmptyInputAvoidUnnecessaryRequests()
    {
        var model = new Model("[{\"content\":\"remembered\"}]");
        var extractor = new ModelExtractor();
        Assert.Equal("remembered", Assert.Single(await extractor.ExtractAsync([Message.User("hello")], new(model))).Content);
        Assert.Contains("durable facts", Assert.Single(model.Requests).SystemPrompt!);
        Assert.Empty(await extractor.ExtractAsync([], new(model)));
        Assert.Single(model.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => extractor.ExtractAsync([Message.User("hello")]).AsTask());
    }

    [Fact]
    public async Task ModelAndCancellationErrorsRetainTheirIdentityWhenIteratorCleanupAlsoFails()
    {
        var primary = new IOException("read failed");
        var cleanup = new IOException("close failed");
        var extractor = new ModelExtractor(new() { Model = new FailedModel(primary, cleanup) });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => extractor.ExtractAsync([Message.User("hello")]).AsTask()));
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelExtractor(new() { Model = new Model("[]") }).ExtractAsync([Message.User("hello")], cancellationToken: canceled.Token).AsTask());
    }

    private sealed class FailedModel(Exception failure, Exception cleanup) : IModel, IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        public ModelStreamEvent Current => throw new InvalidOperationException();
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => this;
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => throw failure;
        public ValueTask DisposeAsync() => throw cleanup;
    }

    private sealed class Model(string output) : IModel
    {
        internal readonly List<ModelRequest> Requests = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: output);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
