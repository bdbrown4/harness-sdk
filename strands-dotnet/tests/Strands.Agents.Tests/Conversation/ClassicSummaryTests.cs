using System.Runtime.CompilerServices;
using System.Text.Json;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.Conversation;

public sealed class ClassicSummaryTests
{
    [Fact]
    public async Task ProviderThoughtWrappersCannotBecomeVisibleUserSummaryText()
    {
        var model = new NativeReplyModel([new global::Strands.Agents.Models.GoogleContentBlock(JsonSerializer.Deserialize<JsonElement>("""{"text":"private thought","thought":true,"thoughtSignature":"signed"}""")), new ReasoningBlock("private reasoning", "signature"), new TextBlock("visible summary")]);
        await using var owner = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), AuxModel = model, Messages = ClassicConversationManagerTests.History(8) });
        var manager = new Classic.SummarizingConversationManager(new() { PreserveRecentMessages = 2 });
        await manager.ReduceContextAsync(owner, new ContextWindowOverflowException("overflow"));
        Assert.Equal("visible summary", owner.Messages[0].Text);
        Assert.IsType<TextBlock>(Assert.Single(owner.Messages[0].Content));
    }
    [Fact]
    public async Task DefaultSummariesUseTheBorrowedAuxiliaryModelAndActualTranscript()
    {
        var main = new ClassicConversationManagerTests.TextModel();
        var auxiliary = new ClassicConversationManagerTests.TextModel { Output = "auxiliary facts" };
        await using var owner = new Agent(new() { Model = main, AuxModel = auxiliary, Messages = ClassicConversationManagerTests.History(8) });
        var manager = new Classic.SummarizingConversationManager(new() { SummaryRatio = 0.5, PreserveRecentMessages = 2 });
        await manager.ReduceContextAsync(owner, new ContextWindowOverflowException("overflow"));
        Assert.Empty(main.Requests);
        var request = Assert.Single(auxiliary.Requests);
        Assert.Equal(["0", "1", "2", "3", "Please summarize this conversation."], request.Messages.Select(message => message.Text));
        Assert.Equal(Classic.SummarizingConversationManager.DefaultSummarizationPrompt, request.SystemPrompt);
        Assert.Equal("auxiliary facts", owner.Messages[0].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelAndIteratorFailuresLeaveHistoryAndCountersUnchanged(bool bodyFailure)
    {
        var primary = new IOException("read failed");
        var cleanup = new IOException("cleanup failed");
        var model = new CleanupModel(bodyFailure ? primary : null, cleanup);
        await using var owner = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), AuxModel = model, Messages = ClassicConversationManagerTests.History(8) });
        var original = owner.Messages;
        var manager = new Classic.SummarizingConversationManager(new() { PinFirst = 1, PreserveRecentMessages = 2 });
        var error = await Assert.ThrowsAsync<IOException>(async () => await manager.ReduceContextAsync(owner, new ContextWindowOverflowException("overflow")));
        Assert.Same(bodyFailure ? primary : cleanup, error);
        if (bodyFailure) Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(original, owner.Messages);
        Assert.Equal(0, manager.RemovedMessageCount);
        Assert.Equal(1, model.IteratorDisposals);
        await owner.DisposeAsync();
        Assert.Equal(0, model.ClientDisposals);
    }

    [Fact]
    public async Task RoutineSlidingDoesNotTruncateAndPythonToolPairFallbackRemovesWholePairs()
    {
        var input = JsonSerializer.Deserialize<JsonElement>("{}");
        var messages = new Message[] { new(Role.Assistant, [new ToolUseBlock("first", "read", input)]), new(Role.User, [new ToolResultBlock("first", new string('x', 1000))]), new(Role.Assistant, [new ToolUseBlock("second", "read", input)]), new(Role.User, [new ToolResultBlock("second", "kept")]) };
        await using var owner = ClassicConversationManagerTests.Create(messages);
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 2 });
        await manager.ApplyManagementAsync(owner);
        Assert.Equal(2, owner.Messages.Count);
        Assert.Equal("second", Assert.Single(owner.Messages[0].Content.OfType<ToolUseBlock>()).ToolUseId);
        Assert.Equal(2, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task PythonTextTruncationCountsUnicodeCharactersAndPreservesStructuredJson()
    {
        var emoji = char.ConvertFromUtf32(0x1f600);
        var original = string.Concat(Enumerable.Repeat(emoji, 500));
        var structured = JsonSerializer.SerializeToElement(new { original = new string('j', 2000) });
        await using var owner = ClassicConversationManagerTests.Create([Message.User("task"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), new(Role.User, [new ToolResultBlock("call", [new TextBlock(original)], structuredContent: structured)])]);
        var manager = new Classic.SlidingWindowConversationManager();
        await manager.ReduceContextAsync(owner, new ContextWindowOverflowException("overflow"));
        var result = Assert.Single(owner.Messages[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 200)) + "...\n\n... [truncated: 100 chars removed] ...\n\n..." + string.Concat(Enumerable.Repeat(emoji, 200)), result.Content);
        Assert.True(JsonElement.DeepEquals(structured, result.StructuredContent!.Value));
    }
    [Fact]
    public async Task DedicatedSummarizerRestoresItsDurableSessionAsWellAsLiveHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), "strands-classic-summary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(path);
            var model = new ClassicConversationManagerTests.TextModel();
            await using var summarizer = new Agent(new() { Model = model, SessionStore = store, SessionId = "summary", SystemPrompt = "dedicated instructions" });
            await summarizer.InvokeAsync("existing dedicated conversation");
            var original = summarizer.Messages.Select(message => message.TrackingId).ToArray();
            await using var owner = ClassicConversationManagerTests.Create(ClassicConversationManagerTests.History(8));
            var manager = new Classic.SummarizingConversationManager(new() { SummarizationAgent = summarizer, PreserveRecentMessages = 2 });
            await manager.ReduceContextAsync(owner, new ContextWindowOverflowException("overflow"));
            Assert.Equal(original, summarizer.Messages.Select(message => message.TrackingId));
            var persisted = (await new FileSessionStore(path).LoadAsync("summary"))!;
            Assert.Equal(original, persisted.Messages.Select(message => message.TrackingId));
            Assert.Equal("dedicated instructions", summarizer.SystemPrompt);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    private sealed class CleanupModel(Exception? bodyError, Exception cleanup) : IModel, IDisposable, IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        private int _index = -1;
        private static readonly ModelStreamEvent[] Events = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "summary"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
        internal int IteratorDisposals;
        internal int ClientDisposals;
        public ModelStreamEvent Current => Events[_index];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => this;
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { if (bodyError is not null) throw bodyError; return new(++_index < Events.Length); }
        public ValueTask DisposeAsync() { IteratorDisposals++; throw cleanup; }
        public void Dispose() => ClientDisposals++;
    }
    private sealed class NativeReplyModel(IReadOnlyList<ContentBlock> content) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            for (var index = 0; index < content.Count; index++)
            {
                if (content[index] is TextBlock text)
                { yield return new ModelContentBlockStartEvent(index); yield return new ModelContentBlockDeltaEvent(index, Text: text.Text); }
                else yield return new ModelContentBlockStartEvent(index) { ContentBlock = content[index] };
                yield return new ModelContentBlockStopEvent(index);
            }
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
