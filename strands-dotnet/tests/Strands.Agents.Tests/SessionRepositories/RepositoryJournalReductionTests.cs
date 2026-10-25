using System.Text.Json;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;
using ClassicSliding = Strands.Agents.Conversation.SlidingWindowConversationManager;
using ClassicSummarizing = Strands.Agents.Conversation.SummarizingConversationManager;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryJournalReductionTests
{
    [Fact]
    public async Task SupportedSuffixReductionKeepsItsArchiveAndRestoresTheSameProjection()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        var manager = new ClassicSliding(new() { WindowSize = 2 });
        var messages = Enumerable.Range(0, 8).Select(index => index % 2 == 0 ? Message.User("message-" + index) : Message.Assistant("message-" + index)).ToArray();
        await using (var agent = Create(repository, manager, messages))
        {
            await agent.InitializeAsync();
            await manager.ReduceContextAsync(agent);
            Assert.Equal(6, manager.RemovedMessageCount);
            Assert.Equal(8, (await repository.ListMessagesAsync("session", "agent")).Count);
        }
        await using var restored = Create(repository, new ClassicSliding(new() { WindowSize = 2 }));
        await restored.InitializeAsync();
        Assert.Equal(new[] { "message-6", "message-7" }, restored.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task RepeatedSummarizationReplacesItsPreviousSyntheticSummaryWithoutAppendingArchiveRows()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        var manager = new ClassicSummarizing(new() { SummaryRatio = 0.5, PreserveRecentMessages = 1 });
        var initial = Enumerable.Range(0, 6).Select(index => index % 2 == 0 ? Message.User("message-" + index) : Message.Assistant("message-" + index)).ToArray();
        string[] expected;
        string tracking;
        await using (var agent = Create(repository, manager, initial))
        {
            await agent.InitializeAsync();
            await manager.ReduceContextAsync(agent);
            var previous = agent.Messages[0].TrackingId;
            await manager.ReduceContextAsync(agent);
            expected = agent.Messages.Select(message => message.Text).ToArray(); tracking = agent.Messages[0].TrackingId;
            Assert.NotEqual(previous, tracking);
            Assert.Equal(4, manager.RemovedMessageCount);
            Assert.Equal(6, (await repository.ListMessagesAsync("session", "agent")).Count);
        }
        await using var restored = Create(repository, new ClassicSummarizing());
        await restored.InitializeAsync();
        Assert.Equal(expected, restored.Messages.Select(message => message.Text));
        Assert.Equal(tracking, restored.Messages[0].TrackingId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonPrefixPinsAndRichRewritesFailBeforeHistoryOrManagerStateChanges(bool rich)
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        var manager = new ClassicSliding(rich ? new() { WindowSize = 40 } : new() { WindowSize = 2, PinFirst = 1 });
        Message[] initial = rich
            ? [Message.User("question"), new(Role.Assistant, [RepositoryJournalRestoreTests.Call("call")]), new(Role.User, [new ToolResultBlock("call", new string('x', 1000))]), Message.Assistant("answer")]
            : [Message.User("pinned"), Message.Assistant("middle"), Message.User("recent"), Message.Assistant("answer")];
        await using var agent = Create(repository, manager, initial);
        await agent.InitializeAsync();
        var before = JsonSerializer.Serialize(agent.Messages);
        var state = manager.GetState().GetRawText();
        await Assert.ThrowsAsync<NotSupportedException>(async () => await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow")));
        Assert.Equal(before, JsonSerializer.Serialize(agent.Messages));
        Assert.Equal(state, manager.GetState().GetRawText());
    }

    [Fact]
    public async Task RepairAndRemovedOffsetCannotBeCombinedBySilentlyCountingSyntheticMessages()
    {
        var state = JsonSerializer.SerializeToElement(new { __name__ = "SlidingWindowConversationManager", removed_message_count = 0, model_call_count = 0 });
        var repository = await RepositoryJournalRestoreTests.SeedAsync(state, new long[] { 0, 1 }, new(Role.Assistant, [RepositoryJournalRestoreTests.Call("call")]), Message.User("question"));
        var manager = new ClassicSliding(new() { WindowSize = 0 });
        await using var agent = Create(repository, manager);
        await agent.InitializeAsync();
        var before = JsonSerializer.Serialize(agent.Messages);
        var managerBefore = manager.GetState().GetRawText();
        await Assert.ThrowsAsync<NotSupportedException>(async () => await manager.ReduceContextAsync(agent));
        Assert.Equal(before, JsonSerializer.Serialize(agent.Messages));
        Assert.Equal(managerBefore, manager.GetState().GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternallyReplacedCountersOrSummaryCannotPublishAnUnrelatedProjection(bool summary)
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        Strands.Agents.Conversation.ConversationManager manager = summary ? new ClassicSummarizing() : new ClassicSliding();
        await using var agent = Create(repository, manager, [Message.User("kept")]);
        await agent.InitializeAsync();
        var state = summary
            ? JsonSerializer.SerializeToElement(new { __name__ = "SummarizingConversationManager", removed_message_count = 0, summary_message = Message.User("not the live summary") }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : JsonSerializer.SerializeToElement(new { __name__ = "SlidingWindowConversationManager", removed_message_count = 1, model_call_count = 0 });
        manager.RestoreFromSession(state);
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InvokeAsync(null));
        var stored = (await repository.ReadAgentAsync("session", "agent"))!;
        Assert.Equal(0, stored.ConversationManagerState.GetProperty("removed_message_count").GetInt64());
        if (summary) Assert.Equal(JsonValueKind.Null, stored.ConversationManagerState.GetProperty("summary_message").ValueKind);
        Assert.Equal("kept", Assert.Single(agent.Messages).Text);
    }

    private static Agent Create(ISessionRepository repository, IConversationManager manager, Message[]? messages = null)
        => new(new() { Id = "agent", Model = new JournalTestModel(), Messages = messages ?? [], ConversationManager = manager, Plugins = [new RepositorySessionManager("session", repository)] });
}
