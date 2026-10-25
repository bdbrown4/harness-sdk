using System.Text.Json;
using Strands.Agents.Conversation;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;
using ClassicSliding = Strands.Agents.Conversation.SlidingWindowConversationManager;
using ClassicSummarizing = Strands.Agents.Conversation.SummarizingConversationManager;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryJournalRestoreTests
{
    [Fact]
    public async Task ARepairedExistingResultRetainsItsExplicitRowBindingForLaterRedaction()
    {
        var calls = new Message(Role.Assistant, [Call("a"), Call("b")]);
        var incomplete = new Message(Role.User, [new ToolResultBlock("a", "original")]);
        var repository = await SeedAsync(null, new long[] { 0, 1 }, calls, incomplete);
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalRedactingModel(), Plugins = [new RepositorySessionManager("session", repository)] });
        await agent.InvokeAsync(null);
        var effective = (await repository.ReadMessageAsync("session", "agent", 1))!;
        Assert.Single(effective.Message.Content);
        Assert.Equal(new[] { "a", "b" }, effective.RedactMessage!.Content.OfType<ToolResultBlock>().Select(result => result.ToolUseId));
        Assert.All(effective.RedactMessage.Content.OfType<ToolResultBlock>(), result => Assert.Equal("redacted", result.Content));
    }
    [Fact]
    public async Task ClassicOffsetCountsRowsAndNeverReusesAnArchivedNumericIdentity()
    {
        var manager = new ClassicSliding();
        var state = JsonSerializer.SerializeToElement(new { __name__ = nameof(SlidingWindowConversationManager), removed_message_count = 2, per_turn = 0 });
        var repository = await SeedAsync(state, new long[] { -20, 4, 70 }, Message.User("removed one"), Message.Assistant("removed two"), Message.User("retained"));
        await using var agent = Create(repository, manager);
        await agent.InitializeAsync();
        Assert.Equal("retained", Assert.Single(agent.Messages).Text);
        Assert.Equal(2, manager.RemovedMessageCount);
        await agent.InvokeAsync("new");
        Assert.Equal(new long[] { -20, 4, 70, 71, 72 }, (await repository.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
    }

    [Fact]
    public async Task AnEmptyRetainedTailStillUsesTheLastArchivedIdentity()
    {
        var state = JsonSerializer.SerializeToElement(new { __name__ = nameof(SlidingWindowConversationManager), removed_message_count = 2, per_turn = 0 });
        var repository = await SeedAsync(state, new long[] { 3, 900 }, Message.User("old"), Message.Assistant("old answer"));
        await using var agent = Create(repository, new ClassicSliding());
        await agent.InitializeAsync();
        Assert.Empty(agent.Messages);
        await agent.InvokeAsync("new");
        Assert.Equal(new long[] { 3, 900, 901, 902 }, (await repository.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
    }

    [Fact]
    public async Task PreviousSummaryIsPrependedWithoutCreatingAnIndexedSummaryRow()
    {
        var summary = new Message(Role.User, [new TextBlock("previous summary")], "summary-id");
        var state = JsonSerializer.SerializeToElement(new { __name__ = nameof(SummarizingConversationManager), removed_message_count = 2, summary_message = summary }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var repository = await SeedAsync(state, new long[] { 0, 1, 2 }, Message.User("removed"), Message.Assistant("removed answer"), Message.User("recent"));
        await using var agent = Create(repository, new ClassicSummarizing());
        await agent.InitializeAsync();
        Assert.Equal(new[] { "previous summary", "recent" }, agent.Messages.Select(message => message.Text));
        Assert.Equal("summary-id", agent.Messages[0].TrackingId);
        Assert.Equal(3, (await repository.ListMessagesAsync("session", "agent")).Count);
    }

    [Fact]
    public async Task OrphanedResultsAreRemovedWhileOrdinaryUserContentIsRetained()
    {
        var orphan = new Message(Role.User, [new TextBlock("keep"), new ToolResultBlock("orphan", "discard")]);
        var repository = await SeedAsync(null, new long[] { 0, 1 }, orphan, Message.Assistant("later"));
        await using var agent = Create(repository);
        await agent.InitializeAsync();
        Assert.Equal(new[] { "keep", "later" }, agent.Messages.Select(message => message.Text));
        Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content), block => block is ToolResultBlock);
        Assert.IsType<ToolResultBlock>((await repository.ReadMessageAsync("session", "agent", 0))!.Message.Content[1]);
    }

    [Fact]
    public async Task MismatchedBatchGetsExactlyOneResultPerCallWithoutExecutingAnyBody()
    {
        var calls = new Message(Role.Assistant, [Call("a"), Call("b")]);
        var results = new Message(Role.User, [new ToolResultBlock("b", "retained"), new ToolResultBlock("stale", "discard"), new TextBlock("extra")]);
        var repository = await SeedAsync(null, new long[] { 0, 1, 2 }, calls, results, Message.Assistant("later"));
        await using var agent = Create(repository);
        await agent.InitializeAsync();
        var repaired = agent.Messages[1].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "a", "b" }, repaired.Select(result => result.ToolUseId));
        Assert.Equal(ToolResultStatus.Error, repaired[0].Status);
        Assert.Equal("retained", repaired[1].Content);
        Assert.Equal("extra", agent.Messages[1].Text);
        Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task AGapBeforeOrdinaryContentGetsASyntheticResultMessageAndLeavesFinalPendingCallsAlone()
    {
        var repository = await SeedAsync(null, new long[] { 0, 1, 2 }, new(Role.Assistant, [Call("old")]), Message.User("question"), new(Role.Assistant, [Call("pending")]));
        await using var agent = Create(repository);
        await agent.InitializeAsync();
        Assert.Equal(4, agent.Messages.Count);
        Assert.Equal("old", Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[1].Content)).ToolUseId);
        Assert.Equal("question", agent.Messages[2].Text);
        Assert.Equal("pending", Assert.IsType<ToolUseBlock>(Assert.Single(agent.Messages[3].Content)).ToolUseId);
        Assert.False(string.IsNullOrWhiteSpace(agent.Messages[1].TrackingId));
    }

    [Fact]
    public async Task InvalidSummaryIsRejectedBeforeAnyConfiguredStateOrHistoryChanges()
    {
        using var state = JsonDocument.Parse("{\"__name__\":\"SummarizingConversationManager\",\"removed_message_count\":1,\"summary_message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":null}],\"trackingId\":\"bad\"}}");
        var repository = await SeedAsync(state.RootElement, new long[] { 0 }, Message.User("stored"));
        await using var agent = new Agent(new() { Id = "agent", Model = new JournalTestModel(), Messages = [Message.User("configured")], State = new Dictionary<string, JsonElement> { ["configured"] = JsonSerializer.SerializeToElement(true) }, ConversationManager = new ClassicSummarizing(), Plugins = [new RepositorySessionManager("session", repository)] });
        await Assert.ThrowsAnyAsync<Exception>(() => agent.InitializeAsync());
        Assert.True(agent.State.Get("configured")?.GetBoolean());
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
    }

    private static Agent Create(ISessionRepository repository, IConversationManager? manager = null) => new(new() { Id = "agent", Model = new JournalTestModel(), ConversationManager = manager, Plugins = [new RepositorySessionManager("session", repository)] });
    internal static ToolUseBlock Call(string id) => new(id, "never-run", JsonSerializer.SerializeToElement(new { }));
    internal static async Task<StorageSessionRepository> SeedAsync(JsonElement? managerState, long[] ids, params Message[] messages)
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        await repository.CreateSessionAsync(new("session"));
        await repository.CreateAgentAsync("session", new("agent", JsonSerializer.SerializeToElement(new { }), managerState ?? JsonSerializer.SerializeToElement(new { })));
        for (var index = 0; index < messages.Length; index++) await repository.CreateMessageAsync("session", "agent", new(messages[index], ids[index]));
        return repository;
    }
}
