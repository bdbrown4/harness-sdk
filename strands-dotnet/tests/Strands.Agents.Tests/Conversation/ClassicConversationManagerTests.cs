using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.Conversation;

public sealed class ClassicConversationManagerTests
{
    [Fact]
    public async Task RestoringManagerStateCannotRewriteAnAwaitingSummary()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new TextModel { BeforeStart = async () => { entered.TrySetResult(true); await release.Task; } };
        await using var agent = Create(History(8), model);
        var manager = new Classic.SummarizingConversationManager(new() { SummaryRatio = 0.5, PreserveRecentMessages = 2 });
        var reducing = manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow")).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            Assert.Throws<InvalidOperationException>(() => manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SummarizingConversationManager","removed_message_count":100,"summary_message":null}""")));
        }
        finally { release.TrySetResult(true); await reducing; }
        Assert.Equal(4, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task RequestedSummaryCancellationKeepsTheExactCallerTokenAndBackendCause()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("cancelled backend");
        var model = new TextModel { BeforeStart = () => { cancellation.Cancel(); throw failure; } };
        await using var agent = Create(History(8), model);
        var manager = new Classic.SummarizingConversationManager(new() { PreserveRecentMessages = 2 });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(0, manager.RemovedMessageCount);
        Assert.Equal(8, agent.Messages.Count);
    }
    [Fact]
    public async Task RestoredPerTurnCadenceReducesBeforeTheNextMatchingModelCall()
    {
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 3, PerTurn = 3 });
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":0,"model_call_count":2}"""));
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, Messages = History(7), Plugins = [manager] });
        await agent.InvokeAsync(null);
        Assert.Equal(3, manager.ModelCallCount);
        Assert.Equal(["4", "5", "6"], Assert.Single(model.Requests).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ReactiveReductionTruncatesOldestRichToolTextAndImageBeforeRemovingHistory()
    {
        var text = new string('a', 1000);
        await using var agent = Create([Message.User("task"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), new(Role.User, [new ToolResultBlock("call", [new TextBlock(text), new ImageBlock("image/png", "AQI=")], ToolResultStatus.Error)])]);
        var manager = new Classic.SlidingWindowConversationManager();
        await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        var result = Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Equal(new string('a', 200) + "...\n\n... [truncated: 600 chars removed] ...\n\n..." + new string('a', 200), Assert.IsType<TextBlock>(result.ContentBlocks[0]).Text);
        Assert.Equal("[image: png, 2 bytes]", Assert.IsType<TextBlock>(result.ContentBlocks[1]).Text);
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task ActualSummariesReplacePreviousSummariesWithoutDoubleCountingOriginals()
    {
        var model = new TextModel();
        await using var agent = Create(History(8), model);
        var manager = new Classic.SummarizingConversationManager(new() { SummaryRatio = 0.5, PreserveRecentMessages = 2 });
        await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        Assert.Equal(4, manager.RemovedMessageCount);
        Assert.Equal(Role.User, agent.Messages[0].Role);
        Assert.Equal("summary", agent.Messages[0].Text);
        await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow again"));
        Assert.Equal(5, manager.RemovedMessageCount);
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(model.Requests[0].Tools ?? []);
        var restored = new Classic.SummarizingConversationManager();
        var prepend = Assert.Single(restored.RestoreFromSession(manager.GetState())!);
        Assert.Equal(agent.Messages[0].TrackingId, prepend.TrackingId);
        Assert.Equal(5, restored.RemovedMessageCount);
    }
    [Fact]
    public async Task RoutineSlidingReductionMutatesHistoryAndPersistsTheRealRemovalCount()
    {
        await using var agent = Create(History(7));
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 3 });
        await manager.ApplyManagementAsync(agent);
        Assert.Equal(["4", "5", "6"], agent.Messages.Select(message => message.Text));
        Assert.Equal(4, manager.RemovedMessageCount);
        Assert.Equal(4, manager.GetState().GetProperty("removed_message_count").GetInt64());
    }

    [Fact]
    public async Task ZeroWindowRetainsPinnedToolPartnersWithoutInventingRemovedMessages()
    {
        var call = MessagePins.Pin(new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]));
        await using var agent = Create([Message.User("discard"), call, new(Role.User, [new ToolResultBlock("call", "kept")]), Message.Assistant("discard")]);
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 0 });
        await manager.ApplyManagementAsync(agent);
        Assert.Equal(2, agent.Messages.Count);
        Assert.Same(call, agent.Messages[0]);
        Assert.Equal(2, manager.RemovedMessageCount);
    }

    [Fact]
    public void RestoredCadenceAndRemovalStateAreValidatedBeforeAnyCounterChanges()
    {
        var manager = new Classic.SlidingWindowConversationManager(new() { PerTurn = 3 });
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":7,"model_call_count":2}"""));
        Assert.Equal(7, manager.RemovedMessageCount);
        Assert.Equal(2, manager.ModelCallCount);
        Assert.Throws<InvalidDataException>(() => manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":99,"model_call_count":-1}""")));
        Assert.Equal(7, manager.RemovedMessageCount);
        Assert.Equal(2, manager.ModelCallCount);
    }

    [Fact]
    public async Task NullManagerPreservesTheOriginalOverflowException()
    {
        await using var agent = Create(History(1));
        var failure = new ContextWindowOverflowException("original overflow");
        var manager = new Classic.NullConversationManager();
        Assert.Same(failure, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, failure)));
    }

    internal static Agent Create(IReadOnlyList<Message> messages, IModel? model = null) => new(new() { Model = model ?? new TextModel(), Messages = messages });
    internal static Message[] History(int count) => Enumerable.Range(0, count).Select(index => new Message(index % 2 == 0 ? Role.User : Role.Assistant, [new TextBlock(index.ToString(System.Globalization.CultureInfo.InvariantCulture))])).ToArray();
    internal sealed class TextModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal Func<ValueTask>? BeforeStart { get; init; }
        internal string Output { get; init; } = "summary";
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (BeforeStart is { } before) await before();
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: Output); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
