using System.Text.Json;
using Strands.Agents.Context;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.Conversation;

public sealed class ClassicToolPairAnchorTests
{
    [Fact]
    public async Task TypeScriptFallbackRetainsTheNearestPlainUserAndOriginalMessageFacts()
    {
        await using var agent = Create(History());
        var original = agent.Messages;
        var manager = Manager();
        await manager.ReduceContextAsync(agent);
        Assert.Equal(["u2", "a3", "r4"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(2, manager.RemovedMessageCount);
        Assert.Equal(original.Skip(2).Select(MessageJson), agent.Messages.Select(MessageJson));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PythonDefaultAndExplicitPolicyKeepTheExistingAssistantBoundary(bool explicitPolicy)
    {
        await using var agent = Create(History());
        var manager = new Classic.SlidingWindowConversationManager(explicitPolicy
            ? new() { WindowSize = 2, ShouldTruncateResults = false, ToolPairBoundaryPolicy = Classic.ClassicToolPairBoundaryPolicy.Python }
            : new() { WindowSize = 2, ShouldTruncateResults = false });
        await manager.ReduceContextAsync(agent);
        Assert.Equal(["a3", "r4"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(3, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task AValidPinnedUserPrefixSuppliesTheAnchor()
    {
        var history = History();
        history[0] = MessagePins.Pin(history[0]);
        await using var agent = Create(history);
        var original = agent.Messages;
        var manager = Manager();
        await manager.ReduceContextAsync(agent);
        Assert.Equal(["u0", "a3", "r4"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(original[0].Metadata!.Value.GetRawText(), agent.Messages[0].Metadata!.Value.GetRawText());
        Assert.Equal(2, manager.RemovedMessageCount);
    }

    [Theory]
    [InlineData("assistant-first")]
    [InlineData("assistant-last")]
    [InlineData("adjacent-users")]
    public async Task InvalidPinnedPrefixDeclinesWithoutChangingHistoryOrCounters(string kind)
    {
        var history = History();
        var pins = kind switch { "assistant-first" => new[] { 1 }, "assistant-last" => new[] { 0, 1 }, _ => new[] { 0, 2 } };
        foreach (var index in pins) history[index] = MessagePins.Pin(history[index]);
        await using var agent = Create(history);
        var original = agent.Messages.Select(MessageJson).ToArray();
        var manager = Manager();
        var state = manager.GetState().GetRawText();
        var overflow = new ContextWindowOverflowException("same overflow");
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
        Assert.Equal(original, agent.Messages.Select(MessageJson));
        Assert.Equal(state, manager.GetState().GetRawText());
    }

    [Fact]
    public async Task CompletePairsWithoutAPlainUserAnchorDeclineAtomically()
    {
        await using var agent = Create([Call("a0", "one"), Result("r1", "one"), Call("a2", "two"), Result("r3", "two")]);
        var original = agent.Messages.Select(MessageJson).ToArray();
        var manager = Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.ReduceContextAsync(agent));
        Assert.Equal(original, agent.Messages.Select(MessageJson));
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task KeepingTheOnlyAnchorCannotPretendToRemoveMessages()
    {
        await using var agent = Create([User("u0"), Call("a1", "one"), Result("r2", "one")]);
        var original = agent.Messages.Select(MessageJson).ToArray();
        var manager = Manager();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.ReduceContextAsync(agent));
        Assert.Equal(original, agent.Messages.Select(MessageJson));
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    [Theory]
    [InlineData(Classic.ClassicToolPairBoundaryPolicy.Python)]
    [InlineData(Classic.ClassicToolPairBoundaryPolicy.TypeScript)]
    public async Task OrdinaryPlainUserTrimPointsRetainTheirExistingPolicy(Classic.ClassicToolPairBoundaryPolicy policy)
    {
        await using var agent = Create([User("u0"), Assistant("a1"), User("u2"), Assistant("a3"), User("u4")]);
        var manager = Manager(policy);
        await manager.ReduceContextAsync(agent);
        Assert.Equal(["u4"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(4, manager.RemovedMessageCount);
    }

    [Theory]
    [InlineData(Classic.ClassicToolPairBoundaryPolicy.Python)]
    [InlineData(Classic.ClassicToolPairBoundaryPolicy.TypeScript)]
    public async Task ZeroWindowDoesNotRequireAnAnchorAndPreservesPinnedMessages(Classic.ClassicToolPairBoundaryPolicy policy)
    {
        var history = History();
        history[0] = MessagePins.Pin(history[0]);
        await using var agent = Create(history);
        var manager = Manager(policy, window: 0);
        await manager.ReduceContextAsync(agent);
        Assert.Equal(["u0"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(4, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task TheFirstCompletePairAtOrAfterTheTargetRetainsItsFullTail()
    {
        var history = History().Concat([Call("a5", "two"), Result("r6", "two")]).ToArray();
        await using var agent = Create(history);
        var manager = Manager(window: 4);
        await manager.ReduceContextAsync(agent);
        Assert.Equal(["u2", "a3", "r4", "a5", "r6"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(2, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task AValidatedAlternatingPinnedPrefixCanContainSeveralMessages()
    {
        var history = new[] { User("u0"), Assistant("a1"), User("u2"), Assistant("a3"), User("u4"), Call("a5", "one"), Result("r6", "one") };
        for (var index = 0; index < 3; index++) history[index] = MessagePins.Pin(history[index]);
        await using var agent = Create(history);
        var manager = Manager();
        await manager.ReduceContextAsync(agent);
        Assert.Equal(["u0", "a1", "u2", "a5", "r6"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(2, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task DefaultChildrenRetainTheExplicitBoundaryPolicy()
    {
        var calls = 0;
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = () => { if (Interlocked.Increment(ref calls) == 1) throw new ContextWindowOverflowException("overflow"); return default; } };
        await using var parent = new Agent(new() { Model = model, ConversationManager = Manager() });
        await using var child = parent.CreateChild();
        await child.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(History()) }));
        await child.InvokeAsync(null);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(["u2", "a3", "r4"], model.Requests[1].Messages.Select(message => message.TrackingId));
        Assert.Empty(parent.Messages);
    }

    [Fact]
    public void UnknownBoundaryPolicyIsRejectedBeforeAnyAgentOrModelWork()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Manager((Classic.ClassicToolPairBoundaryPolicy)999));

    [Fact]
    public async Task ADeclinedFirstReductionDoesNotPublishPinsOrConsumePinFirst()
    {
        await using var agent = Create([User("first"), Call("a1", "one"), Result("r2", "one")]);
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 2, ShouldTruncateResults = false, PinFirst = 1, ToolPairBoundaryPolicy = Classic.ClassicToolPairBoundaryPolicy.TypeScript });
        var original = agent.Messages.Select(MessageJson).ToArray();
        var overflow = new ContextWindowOverflowException("overflow");
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
        Assert.Equal(original, agent.Messages.Select(MessageJson));
        Assert.False(MessagePins.IsPinned(agent.Messages, 0));
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(History()) }));
        await manager.ReduceContextAsync(agent, overflow);
        Assert.Equal(["u0", "a3", "r4"], agent.Messages.Select(message => message.TrackingId));
        Assert.True(MessagePins.IsPinned(agent.Messages, 0));
        Assert.Equal(2, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task RepeatedReactiveReductionRetainsTheAcceptedCounterAndOriginalOverflow()
    {
        await using var agent = Create(History());
        var manager = Manager();
        await manager.ReduceContextAsync(agent);
        var original = agent.Messages.Select(MessageJson).ToArray();
        var overflow = new ContextWindowOverflowException("next overflow");
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
        Assert.Equal(original, agent.Messages.Select(MessageJson));
        Assert.Equal(2, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task BoundaryPolicyDoesNotSelectTheRichTruncationPolicy()
    {
        var text = new string('x', 1000);
        await using var agent = Create([User("u0"), Call("a1", "one"), new(Role.User, [new ToolResultBlock("one", text)], "r2")]);
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 2, ToolPairBoundaryPolicy = Classic.ClassicToolPairBoundaryPolicy.TypeScript });
        await manager.ReduceContextAsync(agent, new ContextWindowOverflowException("overflow"));
        Assert.Equal(["u0", "a1", "r2"], agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(new string('x', 200) + "...\n\n... [truncated: 600 chars removed] ...\n\n..." + new string('x', 200), Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task PinnedToolPartnersCannotBeCountedAsDeleted()
    {
        var history = new[] { User("u0"), Call("a1", "one"), Result("r2", "one"), Call("a3", "two"), Result("r4", "two") };
        history[0] = MessagePins.Pin(history[0]);
        history[1] = MessagePins.Pin(history[1]);
        await using var agent = Create(history);
        var original = agent.Messages.Select(MessageJson).ToArray();
        var manager = Manager();
        var overflow = new ContextWindowOverflowException("protected");
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
        Assert.Equal(original, agent.Messages.Select(MessageJson));
        Assert.True(MessagePins.IsPinned(agent.Messages, 2));
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task NoPairRetainsTheExistingOrdinaryNoProgressAndOverflowBehavior()
    {
        await using var agent = Create([User("u0"), Assistant("a1"), User("u2"), Assistant("a3")]);
        var original = agent.Messages.Select(MessageJson).ToArray();
        var manager = Manager(window: 1);
        await manager.ReduceContextAsync(agent);
        Assert.Equal(original, agent.Messages.Select(MessageJson));
        var overflow = new ContextWindowOverflowException("not reducible");
        Assert.Same(overflow, await Assert.ThrowsAsync<ContextWindowOverflowException>(async () => await manager.ReduceContextAsync(agent, overflow)));
        Assert.Equal(0, manager.RemovedMessageCount);
    }

    private static Classic.SlidingWindowConversationManager Manager(Classic.ClassicToolPairBoundaryPolicy policy = Classic.ClassicToolPairBoundaryPolicy.TypeScript, int window = 2)
        => new(new() { WindowSize = window, ShouldTruncateResults = false, ToolPairBoundaryPolicy = policy });
    private static Agent Create(IReadOnlyList<Message> messages) => new(new() { Model = new ClassicConversationManagerTests.TextModel(), Messages = messages });
    private static Message[] History() => [User("u0"), Assistant("a1"), User("u2"), Call("a3", "one"), Result("r4", "one")];
    private static Message User(string id) => new(Role.User, [new TextBlock(id)], id, JsonSerializer.SerializeToElement(new { source = id }));
    private static Message Assistant(string id) => new(Role.Assistant, [new TextBlock(id)], id);
    private static Message Call(string id, string call) => new(Role.Assistant, [new ToolUseBlock(call, "read", JsonSerializer.Deserialize<JsonElement>("{}"))], id);
    private static Message Result(string id, string call) => new(Role.User, [new ToolResultBlock(call, "result") { Metadata = JsonSerializer.SerializeToElement(new { receipt = id }) }], id);
    private static string MessageJson(Message message) => JsonSerializer.Serialize(message);
}
