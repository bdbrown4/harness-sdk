using System.Text.Json;
using Strands.Agents.Context;

namespace Strands.Agents.Tests.Context;

public sealed class OffloadStrategyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchSummaryCannotGrowTypedJsonEvenWhenItsTokenEstimateDecreases(bool duplicateStructuredContent)
    {
        var json = JsonSerializer.Deserialize<JsonElement>("[" + string.Join(",", Enumerable.Repeat("\"\"", 200)) + "]");
        var main = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var auxiliary = new AgentTests.ScriptedModel(AgentTests.Text(new string('x', 1_000)));
        await using var agent = new Agent(new()
        {
            Model = main,
            AuxModel = auxiliary,
            ContextManager = new(new() { Strategies = [Offload.Summarize().When(new() { Utilization = 0, PreserveRecent = 2 })], StashEnabled = false }),
            Messages = [Message.User("head"), new(Role.Assistant, [new ToolUseBlock("read", "read", JsonSerializer.Deserialize<JsonElement>("{}"))]), new(Role.User, [new ToolResultBlock("read", [new JsonBlock(json)], structuredContent: duplicateStructuredContent ? json : null)]), Message.Assistant("recent reply"), Message.User("latest")]
        });

        await agent.InvokeAsync(null);

        var result = Assert.Single(main.Requests[0].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.True(JsonElement.DeepEquals(json, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json));
        Assert.Single(auxiliary.Requests);
    }

    [Fact]
    public async Task ThresholdTruncatesOnlySuccessfulResultsAndKeepsToolCorrelation()
    {
        var text = new string('x', 8_000);
        Message[] messages = [Message.User("head"), new(Role.Assistant, [new ToolUseBlock("ok", "read", JsonSerializer.SerializeToElement(new { })), new ToolUseBlock("bad", "read", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("ok", text), new ToolResultBlock("bad", text, ToolResultStatus.Error)])];
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var strategy = Offload.Truncate(OffloadTarget.ToolResults, new() { PreviewTokens = 10 }).When(new() { Threshold = 100 });
        await using var agent = new Agent(new() { Model = model, Messages = messages, ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        var result = model.Requests[0].Messages[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Contains("[Truncated:", result[0].Content);
        Assert.Equal("ok", result[0].ToolUseId);
        Assert.Equal(text, result[1].Content);
        Assert.Equal(text, Assert.IsType<ToolResultBlock>(messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task PinOnAToolRequestProtectsTheCorrelatedResultFromBlockReduction()
    {
        var text = new string('x', 8_000);
        Message[] messages = [Message.User("head"), MessagePins.Pin(new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.SerializeToElement(new { }))])), new(Role.User, [new ToolResultBlock("call", text)])];
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var strategy = Offload.Truncate(OffloadTarget.ToolResults, new() { PreviewTokens = 10 }).When(new() { Threshold = 100 });
        await using var agent = new Agent(new() { Model = model, Messages = messages, ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        Assert.Equal(text, Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task ThresholdReductionRunsOnArrivalAfterStashingBeforeBeforeModelHooks()
    {
        var text = new string('x', 8_000);
        var strategy = Offload.Truncate(OffloadTarget.UserText, new() { PreviewTokens = 10 }).When(new() { Threshold = 100 });
        var manager = new ContextManager(new() { Strategies = [strategy] });
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, ContextManager = manager });
        Message? original = null;
        var eager = false;
        agent.Hooks.Register<MessageAddedEvent>((value, _) => { original ??= value.Message; return default; }, -100);
        agent.Hooks.Register<MessageAddedEvent>((value, _) => { if (value.Message.Role == Role.User) eager = value.Agent.Messages[0].Text.StartsWith("[Truncated:", StringComparison.Ordinal); return default; }, int.MaxValue);

        await agent.InvokeAsync(text);

        Assert.True(eager, "Arrival reduction must finish before before-model hooks, independently of the pipeline.");
        var reference = Assert.Single(manager.Stash!.GetReferences(original!.Content[0], original, 0));
        Assert.Equal(text, Assert.IsType<TextBlock>(await manager.Stash.RetrieveAsync(reference)).Text);
    }

    [Fact]
    public async Task DropTargetsErrorsAndPreservesStatusCorrelationAndStructuredData()
    {
        var text = new string('x', 8_000);
        var structured = JsonSerializer.SerializeToElement(new { code = 12 });
        Message[] messages = [Message.User("head"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("call", [new TextBlock(text)], ToolResultStatus.Error, structured)])];
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Messages = messages, ContextManager = new(new() { Strategies = [Offload.Drop(OffloadTarget.ToolResultErrors).When(new() { Threshold = 100 })], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        var result = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        Assert.Equal("[Dropped]", result.Content);
        Assert.Equal("call", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.True(JsonElement.DeepEquals(structured, result.StructuredContent!.Value));
    }

    [Fact]
    public async Task UtilizationDropsWholeOldToolPairsAndPreservesHeadLatestAndRecentMatches()
    {
        var head = Message.User("head");
        var latest = Message.User("latest");
        var recent = new string('k', 1_000);
        Message[] messages = [head, new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("call", new string('x', 8_000))]), Message.Assistant(recent), latest];
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var strategy = Offload.Drop().When(new() { Utilization = 0, PreserveRecent = 2 });
        await using var agent = new Agent(new() { Model = model, Messages = messages, ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        var projected = model.Requests[0].Messages;
        Assert.DoesNotContain(projected.SelectMany(message => message.Content), block => block is ToolUseBlock or ToolResultBlock);
        Assert.Equal(head.TrackingId, projected[0].TrackingId);
        Assert.Equal(latest.TrackingId, projected[^1].TrackingId);
        Assert.Contains(projected, message => message.Text.Contains(recent, StringComparison.Ordinal));
        Assert.Contains(projected, message => message.Text.Contains("[Dropped: 2 messages]", StringComparison.Ordinal));
        Assert.Equal([Role.User, Role.Assistant, Role.User], projected.Select(message => message.Role));
    }

    [Fact]
    public async Task SummarizeUsesTheExplicitModelAndPromptAndPreservesSignedReasoning()
    {
        var main = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var unusedAuxiliary = new AgentTests.ScriptedModel();
        var summarizer = new AgentTests.ScriptedModel(AgentTests.Text("key facts"));
        var signed = new ReasoningBlock("private reasoning", "signature");
        var strategy = Offload.Summarize(OffloadTarget.AssistantText, new() { Model = summarizer, SystemPrompt = "Summarize facts" }).When(new() { Threshold = 100 });
        await using var agent = new Agent(new() { Model = main, AuxModel = unusedAuxiliary, Messages = [Message.User("head"), new(Role.Assistant, [new TextBlock(new string('x', 8_000)), signed]), Message.User("latest")], ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        var projected = main.Requests[0].Messages[1];
        Assert.Contains("[Summarized: text block, ~2,000 tokens]", projected.Text);
        Assert.Contains("key facts", projected.Text);
        Assert.Equal(signed, projected.Content[1]);
        var request = Assert.Single(summarizer.Requests);
        Assert.Equal("Summarize facts", request.SystemPrompt);
        Assert.StartsWith("<content>", request.Messages[0].Text);
        Assert.EndsWith("</content>", request.Messages[0].Text);
        Assert.Empty(unusedAuxiliary.Requests);
    }

    [Fact]
    public async Task BatchSummarizationUsesCompleteToolExchangeWithRoleMarkersAndAuxiliaryFallback()
    {
        var main = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var auxiliary = new AgentTests.ScriptedModel(AgentTests.Text("file read successfully"));
        Message[] messages = [Message.User("head"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.SerializeToElement(new { path = "file" }))]), new(Role.User, [new ToolResultBlock("call", new string('x', 8_000))]), Message.Assistant("recent"), Message.User("latest")];
        var strategy = Offload.Summarize().When(new() { Utilization = 0, PreserveRecent = 2 });
        await using var agent = new Agent(new() { Model = main, AuxModel = auxiliary, Messages = messages, ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        Assert.Contains(main.Requests[0].Messages, message => message.Text.Contains("[Summarized: 2 messages, ~", StringComparison.Ordinal));
        Assert.Contains(main.Requests[0].Messages, message => message.Text.Contains("file read successfully", StringComparison.Ordinal));
        var input = Assert.Single(auxiliary.Requests).Messages[0].Text;
        Assert.Contains("[assistant]", input);
        Assert.Contains("[user]", input);
        Assert.Contains("read", input);
        Assert.DoesNotContain(main.Requests[0].Messages.SelectMany(message => message.Content), block => block is ToolUseBlock or ToolResultBlock);
    }

    [Fact]
    public async Task EmergencyReductionStillRunsWithAnExplicitlyEmptyPipeline()
    {
        var large = new string('x', 8_000);
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var head = Message.User("head");
        var latest = Message.User("latest");
        await using var agent = new Agent(new() { Model = model, Messages = [head, Message.Assistant(large), Message.User("middle"), MessagePins.Pin(Message.Assistant("keep")), latest], ContextManager = new(new() { Strategies = [], StashEnabled = false, ModelMetadata = new(ContextWindowLimit: 1) }) });

        await agent.InvokeAsync(null);

        var projected = model.Requests[0].Messages;
        Assert.DoesNotContain(projected, message => message.Text == large);
        Assert.Contains(projected, message => message.Text.Contains("1 message elided", StringComparison.Ordinal));
        Assert.Equal(head.TrackingId, projected[0].TrackingId);
        Assert.Equal(latest.TrackingId, projected[^1].TrackingId);
        Assert.Contains(projected, message => message.Text == "keep");
    }

    [Fact]
    public async Task PairExpansionCannotDeleteAMatchingResultInsideThePreservedRecentTail()
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var text = new string('x', 8_000);
        Message[] messages = [Message.User("head"), new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("call", text)]), Message.Assistant("recent"), Message.User("latest")];
        await using var agent = new Agent(new() { Model = model, Messages = messages, ContextManager = new(new() { Strategies = [Offload.Drop().When(new() { Utilization = 0, PreserveRecent = 3 })], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        Assert.Contains(model.Requests[0].Messages.SelectMany(message => message.Content), block => block is ToolResultBlock result && result.ToolUseId == "call" && result.Content == text);
    }

    [Fact]
    public async Task ProviderCounterCanEnableMediaOffloadingAndRetainTheOriginalInStash()
    {
        var image = new ImageBlock("image/png", Convert.ToBase64String(new byte[8_000]));
        var original = new Message(Role.User, [image]);
        var model = new MediaCountingModel();
        var manager = new ContextManager(new() { Strategies = [Offload.Truncate(options: new() { PreviewTokens = 1 }).When(new() { Threshold = 10 })] });
        await using var agent = new Agent(new() { Model = model, Messages = [original], ContextManager = manager });

        await agent.InvokeAsync(null);

        Assert.StartsWith("[Offloaded: ~2048 tokens]", Assert.IsType<TextBlock>(Assert.Single(model.Inner.Requests[0].Messages[0].Content)).Text);
        var reference = Assert.Single(manager.Stash!.GetReferences(image, original, 0));
        Assert.Equal(image, Assert.IsType<ImageBlock>(await manager.Stash.RetrieveAsync(reference)));
    }

    [Theory]
    [InlineData("tool::read", true)]
    [InlineData("!tool::other", true)]
    [InlineData("tool::READ", false)]
    public async Task NamedTargetsUseTheCorrelatedTurnWhenProvidersReuseCallIds(string filter, bool matches)
    {
        var text = new string('x', 8_000);
        Message Call(string name) => new(Role.Assistant, [new ToolUseBlock("reused", name, JsonSerializer.SerializeToElement(new { }))]);
        Message Result() => new(Role.User, [new ToolResultBlock("reused", text)]);
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        await using var agent = new Agent(new() { Model = model, Messages = [Message.User("head"), Call("read"), Result(), Call("other"), Result()], ContextManager = new(new() { Strategies = [Offload.Truncate(OffloadTarget.ForTools(filter), new() { PreviewTokens = 10 }).When(new() { Threshold = 100 })], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        Assert.Equal(matches, Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]).Content.StartsWith("[Truncated:", StringComparison.Ordinal));
        Assert.Equal(text, Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[4].Content[0]).Content);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(0.5, 1)]
    [InlineData(0.99, 0)]
    public async Task PreserveRecentCountsOnlyMatchingMessagesAndRoundsRatiosUp(double preserveRecent, int reduced)
    {
        var text = new string('x', 8_000);
        var model = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var strategy = Offload.Truncate(OffloadTarget.AssistantText, new() { PreviewTokens = 10 }).When(new() { Threshold = 100, PreserveRecent = preserveRecent });
        await using var agent = new Agent(new() { Model = model, Messages = [Message.User("head"), Message.Assistant(text), Message.Assistant(text), Message.Assistant(text), Message.User("latest")], ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        Assert.Equal(reduced, model.Requests[0].Messages.Count(message => message.Text.StartsWith("[Truncated:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnExpandingSummaryLeavesTheOriginalUntouched()
    {
        var text = new string('x', 8_000);
        var main = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var auxiliary = new AgentTests.ScriptedModel(AgentTests.Text(new string('y', 16_000)));
        await using var agent = new Agent(new() { Model = main, AuxModel = auxiliary, Messages = [Message.User("head"), Message.Assistant(text), Message.User("latest")], ContextManager = new(new() { Strategies = [Offload.Summarize(OffloadTarget.AssistantText).When(new() { Threshold = 100 })], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        Assert.Equal(text, main.Requests[0].Messages[1].Text);
        Assert.Single(auxiliary.Requests);
    }

    [Fact]
    public void InvalidConditionsAndEmptyFiltersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => OffloadTarget.ForTools([]));
        Assert.Throws<ArgumentException>(() => OffloadTarget.ForTools("!tool::"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Offload.Drop().When(new() { Threshold = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Offload.Drop().When(new() { Utilization = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Offload.Drop().When(new() { PreserveRecent = double.PositiveInfinity }));
        Assert.Throws<ArgumentException>(() => Offload.Truncate(options: new() { PreviewTokens = 10 }).When(new() { Threshold = 10 }));
    }

    private sealed class MediaCountingModel : IModel, IModelTokenCounter
    {
        internal AgentTests.ScriptedModel Inner { get; } = new(AgentTests.Text("done"));
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => Inner.StreamAsync(request, cancellationToken);
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default) => new(request.Messages.Sum(message => message.Content.Sum(block => block switch { ImageBlock or AudioBlock or DocumentBlock => 2048L, TextBlock text => (text.Text.Length + 3L) / 4, _ => 0 })));
    }
}
