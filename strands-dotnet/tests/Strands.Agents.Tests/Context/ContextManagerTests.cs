using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Context;

public sealed class ContextManagerTests
{
    [Fact]
    public async Task AgenticTruncateNeverAdvancesPastItsRequestedRecentTail()
    {
        var recentReply = Message.Assistant("recent reply");
        var latest = Message.User("latest");
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [] }),
            Messages = [Message.User("first"), Message.Assistant(new string('x', 2_000)), Message.User("tail boundary"), recentReply, latest]
        });
        await agent.InitializeAsync();
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":2}""");
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "truncate_context");

        await tool.InvokeAsync(input, new(agent, new("trim", "truncate_context", input), new Dictionary<string, object?>()));

        Assert.Equal([recentReply.TrackingId, latest.TrackingId], agent.Messages.Skip(agent.Messages.Count - 2).Select(message => message.TrackingId));
        Assert.DoesNotContain(agent.Messages, message => message.Text == new string('x', 2_000));
        Assert.Equal("first", agent.Messages[0].Text);
    }

    [Fact]
    public async Task LargeToolPresetUsesItsPinnedThresholdAndPreviewAllowance()
    {
        var boundary = new string('b', 10_000);
        var large = new string('l', 10_400);
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ContextManager = new(new() { Strategies = [ContextPresets.LargeToolOffloading], StashEnabled = false }),
            Messages = [Message.User("first"), new(Role.Assistant, [new ToolUseBlock("boundary", "read", JsonSerializer.Deserialize<JsonElement>("{}")), new ToolUseBlock("large", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), new(Role.User, [new ToolResultBlock("boundary", boundary), new ToolResultBlock("large", large)])]
        });

        await agent.InvokeAsync(null);

        var results = model.Requests[0].Messages[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(boundary, results[0].Content);
        Assert.StartsWith("[Truncated:", results[1].Content);
        Assert.Contains(new string('l', 2_400), results[1].Content);
        Assert.Contains(new string('l', 1_600), results[1].Content);
    }

    [Fact]
    public async Task StaleToolPresetKeepsTheLatestFiveMatchingResults()
    {
        var messages = new List<Message> { Message.User("first") };
        for (var index = 0; index < 6; index++)
        {
            var id = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            messages.Add(new(Role.Assistant, [new ToolUseBlock(id, "read", JsonSerializer.Deserialize<JsonElement>("{}"))]));
            messages.Add(new(Role.User, [new ToolResultBlock(id, new string((char)('a' + index), 1_000))]));
        }
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new() { Model = model, Messages = messages, ContextManager = new(new() { Strategies = [ContextPresets.StaleToolCleanup], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        var results = model.Requests[0].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal("[Dropped]", results[0].Content);
        for (var index = 1; index < 6; index++) Assert.Equal(new string((char)('a' + index), 1_000), results[index].Content);
    }

    [Fact]
    public async Task ProactiveSummaryPresetUsesTheAuxiliaryModelAndRetainsSeventyPercentOfMatches()
    {
        var original = Enumerable.Range(0, 9).Select(index => new Message(index % 2 == 0 ? Role.User : Role.Assistant, [new TextBlock(index == 0 || index == 8 ? "edge" : new string('x', 1_000))])).ToArray();
        var model = new TestModel(_ => Text("ready"));
        var auxiliary = new TestModel(_ => Text("old facts"));
        await using var agent = new Agent(new()
        {
            Model = model,
            AuxModel = auxiliary,
            Messages = original,
            ContextManager = new(new() { Strategies = [ContextPresets.ProactiveSummarization], ModelMetadata = new(ContextWindowLimit: 2_000), StashEnabled = false })
        });

        await agent.InvokeAsync(null);

        Assert.Single(auxiliary.Requests);
        Assert.DoesNotContain(model.Requests[0].Messages, message => message.TrackingId == original[1].TrackingId);
        foreach (var message in original.Skip(2)) Assert.Contains(model.Requests[0].Messages, retained => retained.TrackingId == message.TrackingId);
        Assert.Contains(model.Requests[0].Messages, message => message.Text.Contains("[Summarized:"));
    }

    [Fact]
    public async Task OverflowProtectionPresetRetainsFourLatestMatchesAndDoesNotCallTheSummarizer()
    {
        var original = Enumerable.Range(0, 9).Select(index => new Message(index % 2 == 0 ? Role.User : Role.Assistant, [new TextBlock(index == 0 || index == 8 ? "edge" : new string('x', 1_000))])).ToArray();
        var model = new TestModel(_ => Text("ready"));
        var auxiliary = new TestModel();
        await using var agent = new Agent(new()
        {
            Model = model,
            AuxModel = auxiliary,
            Messages = original,
            ContextManager = new(new() { Strategies = [ContextPresets.OverflowProtection], ModelMetadata = new(ContextWindowLimit: 1_500), StashEnabled = false })
        });

        await agent.InvokeAsync(null);

        Assert.Empty(auxiliary.Requests);
        Assert.Contains(model.Requests[0].Messages, message => message.Text.Contains("elided"));
        Assert.Equal(original[0].TrackingId, model.Requests[0].Messages[0].TrackingId);
        foreach (var message in original.Skip(5)) Assert.Contains(model.Requests[0].Messages, retained => retained.TrackingId == message.TrackingId);
    }

    [Fact]
    public async Task ConcurrentAgenticSummaryAndPinCallsCannotOverwriteOneAnothersChanges()
    {
        var auxiliary = new BlockingSummaryModel();
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            AuxModel = auxiliary,
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [] }),
            Messages = SummaryHistory()
        });
        await agent.InitializeAsync();
        var summarize = Assert.Single(agent.Tools, tool => tool.Spec.Name == "summarize_context");
        var pin = Assert.Single(agent.Tools, tool => tool.Spec.Name == "pin_context");
        var summaryInput = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":3,"summaryRatio":0.6}""");
        var pinInput = JsonSerializer.Deserialize<JsonElement>("""{"select":1}""");
        var summaryTask = summarize.InvokeAsync(summaryInput, new(agent, new("sum", "summarize_context", summaryInput), new Dictionary<string, object?>())).AsTask();
        Assert.Same(auxiliary.Entered.Task, await Task.WhenAny(auxiliary.Entered.Task, Task.Delay(TimeSpan.FromSeconds(5))));

        var pinTask = pin.InvokeAsync(pinInput, new(agent, new("pin", "pin_context", pinInput), new Dictionary<string, object?>())).AsTask();
        Assert.False(pinTask.IsCompleted);
        auxiliary.Release.TrySetResult(true);
        await Task.WhenAll(summaryTask, pinTask);

        Assert.Contains(agent.Messages, message => message.Text == "retained facts");
        Assert.True(MessagePins.IsPinned(agent.Messages, agent.Messages.Count - 1));
        Assert.True(auxiliary.Disposed);
    }

    [Fact]
    public async Task CancelledAgenticSummaryLeavesHistoryUntouchedAndReleasesItsMutationGate()
    {
        var auxiliary = new BlockingSummaryModel();
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            AuxModel = auxiliary,
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [] }),
            Messages = SummaryHistory()
        });
        await agent.InitializeAsync();
        var before = agent.Messages.Select(message => message.TrackingId).ToArray();
        using var cancellation = new CancellationTokenSource();
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":3,"summaryRatio":0.6}""");
        var summarize = Assert.Single(agent.Tools, tool => tool.Spec.Name == "summarize_context");
        var pending = summarize.InvokeAsync(input, new(agent, new("sum", "summarize_context", input), new Dictionary<string, object?>()), cancellation.Token).AsTask();
        Assert.Same(auxiliary.Entered.Task, await Task.WhenAny(auxiliary.Entered.Task, Task.Delay(TimeSpan.FromSeconds(5))));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(before, agent.Messages.Select(message => message.TrackingId));
        Assert.True(auxiliary.Disposed);
        var pin = Assert.Single(agent.Tools, tool => tool.Spec.Name == "pin_context");
        var pinInput = JsonSerializer.Deserialize<JsonElement>("""{"select":1}""");
        Assert.Equal("Pinned 1 message(s).", await pin.InvokeAsync(pinInput, new(agent, new("pin", "pin_context", pinInput), new Dictionary<string, object?>())));
    }

    [Fact]
    public async Task AgenticSummaryCannotExpandNativeJsonEvenWhenTheTokenHeuristicWouldShrink()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("[" + string.Join(",", Enumerable.Repeat("\"\"", 200)) + "]");
        var auxiliary = new TestModel(_ => Text(new string('x', 1_000)));
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            AuxModel = auxiliary,
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [] }),
            Messages = [Message.User("first"), new(Role.Assistant, [new ToolUseBlock("read", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), new(Role.User, [new ToolResultBlock("read", [new JsonBlock(json)])]), Message.Assistant("bridge"), Message.User("recent"), Message.Assistant("recent reply"), Message.User("latest")]
        });
        await agent.InitializeAsync();
        var original = agent.Messages.Select(message => message.TrackingId).ToArray();
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":3,"summaryRatio":0.5}""");
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "summarize_context");

        var result = await tool.InvokeAsync(input, new(agent, new("summary", "summarize_context", input), new Dictionary<string, object?>()));

        Assert.Contains("would not reduce", result);
        Assert.Equal(original, agent.Messages.Select(message => message.TrackingId));
        Assert.Single(auxiliary.Requests);
    }

    [Fact]
    public async Task AgenticCompressionPreservesThePendingToolBatchAndPinnedExchange()
    {
        var call = new ToolUseBlock("pending", "truncate_context", JsonSerializer.Deserialize<JsonElement>("{}"));
        var pinnedCall = MessagePins.Pin(new(Role.Assistant, [new ToolUseBlock("old", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]));
        var result = new Message(Role.User, [new ToolResultBlock("old", new string('p', 1_000))]);
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [] }),
            Messages = [Message.User("first"), Message.Assistant(new string('x', 2_000)), Message.User(new string('y', 2_000)), pinnedCall, result, Message.Assistant("bridge"), Message.User("latest"), new(Role.Assistant, [call])]
        });
        await agent.InitializeAsync();
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":2}""");
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "truncate_context");

        await tool.InvokeAsync(input, new(agent, call, new Dictionary<string, object?>()));

        Assert.Contains(agent.Messages, message => message.TrackingId == pinnedCall.TrackingId);
        Assert.Contains(agent.Messages, message => message.TrackingId == result.TrackingId);
        Assert.Equal("latest", agent.Messages[agent.Messages.Count - 2].Text);
        Assert.Equal("pending", Assert.IsType<ToolUseBlock>(agent.Messages[agent.Messages.Count - 1].Content[0]).ToolUseId);
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("agentic", false)]
    public async Task ModeDefaultsUseDistinctLargeResultThresholds(string mode, bool truncated)
    {
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ContextManager = ContextManager.From(mode),
            Messages = [Message.User("first"), new(Role.Assistant, [new ToolUseBlock("read", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), new(Role.User, [new ToolResultBlock("read", new string('x', 6_400))])]
        });

        await agent.InvokeAsync(null);

        Assert.Equal(truncated, Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]).Content.StartsWith("[Truncated:", StringComparison.Ordinal));
    }

    [Fact]
    public void ModeNamesAreExactAndNullDisablesManagement()
    {
        Assert.Null(ContextManager.From(null));
        Assert.Throws<ArgumentException>(() => ContextManager.From("AUTO"));
        Assert.Throws<ArgumentException>(() => ContextManager.From(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextManager(new() { Mode = (ContextMode)99 }));
        Assert.NotSame(ContextPresets.LargeToolOffloading, ContextPresets.LargeToolOffloading);
    }

    private static Message[] SummaryHistory() => [Message.User("first"), Message.Assistant(new string('a', 2_000)), Message.User(new string('b', 2_000)), Message.Assistant(new string('c', 2_000)), Message.User("recent user"), Message.Assistant("recent reply"), Message.User("latest")];

    private sealed class BlockingSummaryModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Release.TrySetCanceled());
            Entered.TrySetResult(true);
            try
            {
                await Release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in Text("retained facts")) yield return item;
            }
            finally { Disposed = true; }
        }
    }

    [Fact]
    public async Task AgenticModeAppendsEphemeralTokenStatusAfterContextReduction()
    {
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [], StashEnabled = false, ModelMetadata = new(ContextWindowLimit: 100) })
        });

        await agent.InvokeAsync("latest");

        Assert.Contains("<context-status>", model.Requests[0].Messages[0].Text);
        Assert.Contains("/ 100 tokens (", model.Requests[0].Messages[0].Text);
        Assert.Equal(1, model.Requests[0].DynamicTrailingBlocks);
        Assert.DoesNotContain(agent.Messages, message => message.Text.Contains("<context-status>"));
    }

    [Fact]
    public async Task AgenticTruncatePreservesHeadAndPinnedMessageIdentitiesAcrossEqualRoleGaps()
    {
        var head = Message.User("first");
        var pinned = MessagePins.Pin(Message.User("critical"));
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [], StashEnabled = false }),
            Messages = [head, Message.Assistant(new string('x', 2_000)), pinned, Message.Assistant(new string('y', 2_000)), Message.User("latest"), Message.Assistant("latest reply")]
        });
        await agent.InitializeAsync();
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":2}""");
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "truncate_context");

        await tool.InvokeAsync(input, new(agent, new("trim", "truncate_context", input), new Dictionary<string, object?>()));

        Assert.Equal("first", agent.Messages[0].Text);
        Assert.Contains(agent.Messages, message => message.TrackingId == pinned.TrackingId && message.Text == "critical");
        Assert.Contains(agent.Messages, message => message.TrackingId == head.TrackingId);
        Assert.Equal(["latest", "latest reply"], agent.Messages.Skip(agent.Messages.Count - 2).Select(message => message.Text));
    }

    [Fact]
    public async Task AgenticSummarizeUsesTheAuxiliaryModelAndKeepsItsRecentTailAndFirstUser()
    {
        var auxiliary = new TestModel(_ => Text("retained facts"));
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            AuxModel = auxiliary,
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [] }),
            Messages = [Message.User("first"), Message.Assistant(new string('a', 2_000)), Message.User(new string('b', 2_000)), Message.Assistant(new string('c', 2_000)), Message.User("recent user"), Message.Assistant("recent reply"), Message.User("latest")]
        });
        await agent.InitializeAsync();
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "summarize_context");
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":3,"summaryRatio":0.6}""");

        var result = await tool.InvokeAsync(input, new(agent, new("summary", "summarize_context", input), new Dictionary<string, object?>()));

        Assert.StartsWith("Summarized 3 message(s).", result);
        Assert.Equal("first", agent.Messages[0].Content.OfType<TextBlock>().First().Text);
        Assert.Equal("retained facts", agent.Messages[1].Text);
        Assert.Equal(Role.Assistant, agent.Messages[1].Role);
        Assert.Equal(["recent user", "recent reply", "latest"], agent.Messages.Skip(agent.Messages.Count - 3).Select(message => message.Text));
        Assert.Single(auxiliary.Requests);
        Assert.Contains("[assistant]", auxiliary.Requests[0].Messages[0].Text);
    }

    [Fact]
    public async Task AgenticTruncateKeepsTheFirstUserPinnedMessagesAndACompleteRecentBoundary()
    {
        var pinned = MessagePins.Pin(Message.Assistant("critical"));
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            ContextManager = new(new() { Mode = ContextMode.Agentic, Strategies = [] }),
            Messages = [Message.User("first"), Message.Assistant("old reply"), Message.User("old question"), pinned, Message.User("middle"), Message.Assistant("middle reply"), Message.User("latest")]
        });
        await agent.InitializeAsync();
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "truncate_context");
        var input = JsonSerializer.Deserialize<JsonElement>("""{"keepRecent":2}""");

        var result = await tool.InvokeAsync(input, new(agent, new("trim", "truncate_context", input), new Dictionary<string, object?>()));

        Assert.Equal("Dropped 2 message(s). 5 remaining.", result);
        Assert.Equal(["first", "critical", "middle", "middle reply", "latest"], agent.Messages.Select(message => message.Text));
        Assert.True(MessagePins.IsPinned(agent.Messages, 1));
    }

    [Fact]
    public async Task AgenticPinToolProtectsTheCurrentTurnWithoutMutatingEarlierSnapshots()
    {
        var latest = Message.User("latest");
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            ContextManager = ContextManager.From("agentic"),
            Messages = [Message.User("first"), Message.Assistant("reply"), latest]
        });
        await agent.InitializeAsync();
        var tool = Assert.Single(agent.Tools, tool => tool.Spec.Name == "pin_context");
        var input = JsonSerializer.Deserialize<JsonElement>("""{"select":"last_turn"}""");

        var result = await tool.InvokeAsync(input, new(agent, new("pin", "pin_context", input), new Dictionary<string, object?>()));

        Assert.Equal("Pinned 1 message(s).", result);
        Assert.True(MessagePins.IsPinned(agent.Messages, 2));
        Assert.False(MessagePins.IsPinned([latest], 0));
        Assert.False(MessagePins.IsPinned(agent.Messages, 0));
    }

    [Fact]
    public async Task DefaultAutoPolicyTruncatesLargeToolResultsAndStashesTheirOriginalContent()
    {
        var originalText = new string('x', 6_400);
        var original = new Message(Role.User, [new ToolResultBlock("call", originalText)]);
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ContextManager = new(),
            Messages = [Message.User("first"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), original]
        });

        await agent.InvokeAsync(null);

        var result = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        Assert.StartsWith("[Truncated:", result.Content);
        Assert.True(result.Content.Length < originalText.Length);
        var reference = Assert.Single(agent.ContextManager!.Stash!.GetReferences(original.Content[0], original, 0));
        Assert.Equal(originalText, Assert.IsType<TextBlock>(await agent.ContextManager.Stash.RetrieveAsync(reference)).Text);
    }

    [Fact]
    public async Task PipelineCommitsValidatedHistoryAndTheNextStrategySeesThePreviousResult()
    {
        var first = new ReplaceTextStrategy("large", "small");
        var second = new ReplaceTextStrategy("small", "tiny");
        var manager = new ContextManager(new() { Strategies = [first, second], StashEnabled = false });
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new() { Model = model, Messages = [Message.User("first"), Message.Assistant("large")], Plugins = [manager] });

        await agent.InvokeAsync("latest");

        Assert.Equal("tiny", model.Requests[0].Messages[1].Text);
        Assert.Equal("tiny", agent.Messages[1].Text);
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task DefaultStashExposesRetrievalAndStoresOriginalsBeforeStrategiesRun()
    {
        var manager = new ContextManager(new() { Strategies = [new ReplaceTextStrategy("large", "small")] });
        var original = Message.Assistant("large");
        await using var agent = new Agent(new() { Model = new TestModel(_ => Text("ready")), Messages = [Message.User("first"), original], ContextManager = manager });

        await agent.InvokeAsync("latest");

        var stash = Assert.IsType<Stash>(manager.Stash);
        Assert.False(stash.IsDurable);
        Assert.Contains(agent.Tools, tool => tool.Spec.Name == "retrieve_context");
        var reference = Assert.Single(stash.GetReferences(original.Content[0], original, 0));
        Assert.Equal("large", Assert.IsType<TextBlock>(await stash.RetrieveAsync(reference)).Text);
        Assert.Equal("small", agent.Messages[1].Text);
    }

    [Fact]
    public async Task AFailingStrategyCannotCommitAnOrphanedToolResultAndLaterStrategiesStillRun()
    {
        var manager = new ContextManager(new() { Strategies = [new InvalidStrategy(), new ReplaceTextStrategy("large", "small")], StashEnabled = false });
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new() { Model = model, ContextManager = manager, Messages = [Message.User("first"), Message.Assistant("large")] });
        await agent.InvokeAsync("latest");
        Assert.Equal("small", model.Requests[0].Messages[1].Text);
        Assert.DoesNotContain(agent.Messages.SelectMany(message => message.Content), block => block is ToolResultBlock);
    }

    [Fact]
    public async Task ContextManagerInstancesBindOnceAndChildrenReceiveIndependentManagersAndNamespaces()
    {
        var manager = new ContextManager(new() { Strategies = [] });
        var storage = new Strands.Agents.Storage.InMemoryStorage();
        await using var parent = new Agent(new() { Model = new TestModel(_ => Text("ready")), ContextManager = manager, Storage = storage });
        await parent.InitializeAsync();
        await using var child = parent.CreateChild();
        await child.InitializeAsync();
        Assert.NotSame(parent.ContextManager, child.ContextManager);
        Assert.NotEqual(parent.Id, child.Id);
        Assert.Same(storage, child.Storage);
        Assert.NotSame(parent.ContextManager!.Stash, child.ContextManager!.Stash);
        await using var other = new Agent(new() { Model = new TestModel(_ => Text("ready")), ContextManager = manager });
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.InitializeAsync());
    }

    [Fact]
    public async Task EphemeralStashAndAgentIdentitySurviveSessionRestorationAfterReduction()
    {
        var store = new SnapshotStore();
        var original = Message.Assistant("large");
        string identity;
        string reference;
        await using (var first = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            SessionStore = store,
            SessionId = "opaque/session",
            ContextManager = new(new() { Strategies = [new ReplaceTextStrategy("large", "small")] }),
            Messages = [Message.User("first"), original]
        }))
        {
            await first.InvokeAsync("latest");
            identity = first.Id;
            reference = Assert.Single(first.ContextManager!.Stash!.GetReferences(original.Content[0], original, 0));
            Assert.Equal("small", first.Messages[1].Text);
        }
        Assert.NotEmpty(store.Snapshot!.ContextStash!);
        await using var restored = new Agent(new()
        {
            Model = new TestModel(_ => Text("again")),
            SessionStore = store,
            SessionId = "opaque/session",
            ContextManager = new(new() { Strategies = [] })
        });
        await restored.InitializeAsync();
        Assert.Equal(identity, restored.Id);
        Assert.Equal("large", Assert.IsType<TextBlock>(await restored.ContextManager!.Stash!.RetrieveAsync(reference)).Text);
    }

    [Fact]
    public async Task RetrievalResolvesTheStashAfterOriginalContentLeavesTheConversation()
    {
        var original = Message.Assistant("large");
        await using var agent = new Agent(new()
        {
            Model = new TestModel(_ => Text("ready")),
            Messages = [Message.User("first"), original],
            ContextManager = new(new() { Strategies = [new ReplaceTextStrategy("large", "small")] })
        });
        await agent.InvokeAsync("latest");
        var reference = Assert.Single(agent.ContextManager!.Stash!.GetReferences(original.Content[0], original, 0));
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(agent.Tools, tool => tool.Spec.Name == "retrieve_context"));
        var input = JsonSerializer.SerializeToElement(new { reference });
        var result = await tool.InvokeResultAsync(input, new(agent, new("retrieve", "retrieve_context", input), new Dictionary<string, object?>()));
        Assert.Equal("large", result.Content);
        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task AfterModelHookCanRetryARejectedAttemptWithoutDuplicatingUserHistory()
    {
        var hooks = new HookRegistry();
        var observed = 0;
        hooks.Register<AfterModelCallEvent>((value, _) =>
        { observed++; if (value.Error is ContextWindowOverflowException) value.Retry = true; return default; });
        var model = new TestModel(_ => throw new ContextWindowOverflowException("window full"), _ => Text("recovered"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, MaxTurns = 1 });

        var result = await agent.InvokeAsync("hello");

        Assert.Equal("recovered", result.ToString());
        Assert.Equal(1, result.Turns);
        Assert.Equal(2, observed);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(["hello", "recovered"], agent.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ContextManagerRecoversFromProviderOverflowEvenWhenItsEstimateIsBelowTheWindow()
    {
        var strategy = new OverflowStrategy();
        var model = new TestModel(_ => throw new ContextWindowOverflowException("provider window"), _ => Text("recovered"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }),
            Messages = [Message.User("first"), Message.Assistant("old context")]
        });
        var result = await agent.InvokeAsync("latest");
        Assert.Equal("recovered", result.ToString());
        Assert.Equal(1, strategy.OverflowCalls);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("old contex", model.Requests[1].Messages[1].Text);
    }

    [Fact]
    public async Task OverflowRecoveryStopsAfterThreeRetriesAndNeverRetriesWithoutProgress()
    {
        var strategy = new OverflowStrategy();
        var failure = new ContextWindowOverflowException("still full");
        var model = new TestModel(Enumerable.Repeat<Func<ModelRequest, IEnumerable<ModelStreamEvent>>>(_ => throw failure, 4).ToArray());
        await using var agent = new Agent(new()
        {
            Model = model,
            ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }),
            Messages = [Message.User("first"), Message.Assistant("old context")]
        });
        Assert.Same(failure, await Assert.ThrowsAsync<ContextWindowOverflowException>(() => agent.InvokeAsync("latest")));
        Assert.Equal(4, model.Requests.Count);
        Assert.Equal(3, strategy.OverflowCalls);

        var noProgress = new TestModel(_ => throw failure);
        await using var other = new Agent(new() { Model = noProgress, ContextManager = new(new() { Strategies = [], StashEnabled = false }) });
        Assert.Same(failure, await Assert.ThrowsAsync<ContextWindowOverflowException>(() => other.InvokeAsync("only message")));
        Assert.Single(noProgress.Requests);
    }

    [Fact]
    public async Task APolicyThatReportsSuccessWithoutChangingHistoryCannotCauseAnOverflowRetry()
    {
        var failure = new ContextWindowOverflowException("unchanged input");
        var model = new TestModel(Enumerable.Repeat<Func<ModelRequest, IEnumerable<ModelStreamEvent>>>(_ => throw failure, 4).ToArray());
        await using var agent = new Agent(new() { Model = model, ContextManager = new(new() { Strategies = [new NoOpStrategy()], StashEnabled = false }) });
        Assert.Same(failure, await Assert.ThrowsAsync<ContextWindowOverflowException>(() => agent.InvokeAsync("hello")));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task FirstClassContextManagementTakesPrecedenceOverTheLegacyHistoryProjection()
    {
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new()
        {
            Model = model,
            ContextManager = new(new() { Strategies = [], StashEnabled = false }),
            ConversationManager = new SlidingWindowConversationManager(1),
            Messages = [Message.User("first"), Message.Assistant("old")]
        });
        await agent.InvokeAsync("latest");
        Assert.Equal(["first", "old", "latest"], model.Requests[0].Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ContextReductionRunsBeforeApplicationRequestInjectionWithoutDiscardingItsProjection()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            var messages = value.Request.Messages.ToArray();
            var last = messages[messages.Length - 1];
            messages[messages.Length - 1] = new(last.Role, last.Content.Concat(new[] { new TextBlock("injected context") }).ToArray(), last.TrackingId, last.Metadata);
            value.Request = value.Request with { Messages = messages };
            return default;
        });
        var model = new TestModel(_ => Text("ready"));
        await using var agent = new Agent(new()
        {
            Model = model,
            Hooks = hooks,
            ContextManager = new(new() { Strategies = [new ReplaceTextStrategy("large", "small")], StashEnabled = false }),
            Messages = [Message.User("first"), Message.Assistant("large")]
        });

        await agent.InvokeAsync("latest");

        Assert.Equal("small", model.Requests[0].Messages[1].Text);
        Assert.Contains("injected context", model.Requests[0].Messages[2].Text);
        Assert.DoesNotContain(agent.Messages, message => message.Text.Contains("injected context"));
    }

    private sealed class SnapshotStore : ISessionStore
    {
        internal SessionSnapshot? Snapshot;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Snapshot = snapshot; return default; }
    }

    private sealed class InvalidStrategy : ContextStrategy
    {
        public override string Name => "test:invalid";
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
        { context.Messages.Add(new(Role.User, [new ToolResultBlock("orphan", "bad")])); return new(true); }
    }

    private sealed class OverflowStrategy : ContextStrategy
    {
        internal int OverflowCalls;
        public override string Name => "test:overflow";
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
        {
            if (!context.Overflow) return new(false);
            OverflowCalls++;
            var previous = context.Messages[1];
            context.Messages[1] = new(previous.Role, [new TextBlock(previous.Text.Substring(0, previous.Text.Length - 1))], previous.TrackingId, previous.Metadata);
            return new(true);
        }
    }

    private sealed class NoOpStrategy : ContextStrategy
    {
        public override string Name => "test:incorrect-success";
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default) => new(true);
    }

    private static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];

    private sealed class TestModel(params Func<ModelRequest, IEnumerable<ModelStreamEvent>>[] turns) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1](request))
            { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }
    }

    private sealed class ReplaceTextStrategy(string from, string to) : ContextStrategy
    {
        internal int Calls;
        public override string Name => "test:replace-" + from;
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
        {
            Calls++;
            var index = context.Messages.FindIndex(message => message.Text == from);
            if (index < 0) return new(false);
            var previous = context.Messages[index];
            context.Messages[index] = new(previous.Role, [new TextBlock(to)], previous.TrackingId, previous.Metadata);
            return new(true);
        }
    }
}
