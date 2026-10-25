using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Context;

namespace Strands.Compatibility.Tests;

public sealed class OffloadCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchSummariesCannotExpandTypedJsonWhenTheTokenHeuristicDecreases(bool duplicateStructuredContent)
    {
        var json = JsonSerializer.SerializeToElement(Enumerable.Repeat("", 200).ToArray());
        var model = new TextModel();
        var auxiliary = new TextModel(new string('x', 1_000));
        var history = History(new("call", [new JsonBlock(json)], structuredContent: duplicateStructuredContent ? json : null)).Concat(new[] { Message.Assistant("recent reply"), Message.User("latest") }).ToArray();
        await using var agent = new Agent(new()
        {
            Model = model,
            AuxModel = auxiliary,
            Messages = history,
            ContextManager = new(new() { Strategies = [Offload.Summarize().When(new() { Utilization = 0, PreserveRecent = 2 })], StashEnabled = false })
        });

        await agent.InvokeAsync(null);

        var result = Assert.Single(model.Requests[0].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.True(JsonElement.DeepEquals(json, Assert.IsType<JsonBlock>(Assert.Single(result.ContentBlocks)).Json));
        Assert.Single(auxiliary.Requests);
    }

    [Fact]
    public async Task ToolReductionKeepsCorrelationStatusOpaqueContentAndStructuredPayload()
    {
        var text = new string('x', 8_000);
        var data = JsonSerializer.SerializeToElement(new { code = 12 });
        var result = new ToolResultBlock("call", [new TextBlock(text), new ImageBlock("image/png", "AA==")], ToolResultStatus.Error, data);
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, Messages = History(result), ContextManager = new(new() { Strategies = [Offload.Truncate(OffloadTarget.ToolResultErrors, new() { PreviewTokens = 10 }).When(new() { Threshold = 100 })], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        var reduced = Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]);
        Assert.StartsWith("[Truncated:", reduced.Content);
        Assert.Equal("call", reduced.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, reduced.Status);
        Assert.IsType<ImageBlock>(reduced.ContentBlocks[1]);
        Assert.True(JsonElement.DeepEquals(data, reduced.StructuredContent!.Value));
        Assert.Equal(text, result.Content);
    }

    [Fact]
    public async Task AdjacentPairPinsProtectToolResults()
    {
        var text = new string('x', 8_000);
        var history = History(new("call", text));
        history[1] = MessagePins.Pin(history[1]);
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, Messages = history, ContextManager = new(new() { Strategies = [Offload.Drop(OffloadTarget.ToolResults)], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        Assert.True(MessagePins.IsPinned(history, 2));
        Assert.Equal(text, Assert.IsType<ToolResultBlock>(model.Requests[0].Messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task BatchReductionRemovesWholePairsAndRetainsProtectedEnds()
    {
        var model = new TextModel();
        var head = Message.User("head");
        var latest = Message.User("latest");
        Message[] history = [head, new(Role.Assistant, [new ToolUseBlock("call", "read", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [new ToolResultBlock("call", new string('x', 8_000))]), Message.Assistant("recent"), latest];
        await using var agent = new Agent(new() { Model = model, Messages = history, ContextManager = new(new() { Strategies = [Offload.Drop().When(new() { Utilization = 0, PreserveRecent = 2 })], StashEnabled = false }) });

        await agent.InvokeAsync(null);

        var projected = model.Requests[0].Messages;
        Assert.Equal(head.TrackingId, projected[0].TrackingId);
        Assert.Equal(latest.TrackingId, projected[projected.Count - 1].TrackingId);
        Assert.DoesNotContain(projected.SelectMany(message => message.Content), block => block is ToolUseBlock or ToolResultBlock);
        Assert.Equal(new[] { Role.User, Role.Assistant, Role.User }, projected.Select(message => message.Role));
    }

    [Fact]
    public void NativeJsonPreviewCannotExpandWireSize()
    {
        var original = new ToolResultBlock("call", [new JsonBlock(JsonSerializer.SerializeToElement(Enumerable.Repeat("", 400).ToArray()))]);

        Assert.Same(original, ContextReductionMethods.TruncateToolResult(original, new() { PreviewTokens = 200 }));
    }

    [Fact]
    public async Task SharedStrategiesCaptureTheCorrectAgentsStashOnArrival()
    {
        var strategy = Offload.Truncate(OffloadTarget.UserText, new() { PreviewTokens = 10 }).When(new() { Threshold = 100 });
        var firstManager = new ContextManager(new() { Strategies = [strategy] });
        var secondManager = new ContextManager(new() { Strategies = [strategy] });
        var firstModel = new TextModel();
        var secondModel = new TextModel();
        await using var first = new Agent(new() { Model = firstModel, ContextManager = firstManager });
        await using var second = new Agent(new() { Model = secondModel, ContextManager = secondManager });
        Message? firstOriginal = null, secondOriginal = null;
        first.Hooks.Register<MessageAddedEvent>((value, _) => { firstOriginal ??= value.Message; return default; }, -100);
        second.Hooks.Register<MessageAddedEvent>((value, _) => { secondOriginal ??= value.Message; return default; }, -100);

        await first.InvokeAsync(new string('a', 8_000));
        await second.InvokeAsync(new string('b', 8_000));

        var firstReference = Assert.Single(firstManager.Stash!.GetReferences(firstOriginal!.Content[0], firstOriginal, 0));
        var secondReference = Assert.Single(secondManager.Stash!.GetReferences(secondOriginal!.Content[0], secondOriginal, 0));
        Assert.Equal(new string('a', 8_000), Assert.IsType<TextBlock>(await firstManager.Stash.RetrieveAsync(firstReference)).Text);
        Assert.Equal(new string('b', 8_000), Assert.IsType<TextBlock>(await secondManager.Stash.RetrieveAsync(secondReference)).Text);
        Assert.Null(await secondManager.Stash.RetrieveAsync(firstReference));
        Assert.StartsWith("[Truncated:", firstModel.Requests[0].Messages[0].Text);
        Assert.StartsWith("[Truncated:", secondModel.Requests[0].Messages[0].Text);
    }

    [Fact]
    public async Task SummarizerCancellationClosesTheBorrowedModelsIterator()
    {
        var model = new BlockingModel();
        using var cancellation = new CancellationTokenSource();
        var pending = ContextReductionMethods.SummarizeAsync([new TextBlock("content")], model, cancellationToken: cancellation.Token).AsTask();
        await model.Started.Task;

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(model.IteratorDisposed);
    }

    private static Message[] History(ToolResultBlock result) => [Message.User("head"), new(Role.Assistant, [new ToolUseBlock(result.ToolUseId, "read", JsonSerializer.SerializeToElement(new { }))]), new(Role.User, [result])];

    private sealed class TextModel(string text = "done") : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: text);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }

    private sealed class BlockingModel : IModel
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IteratorDisposed;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                Started.TrySetResult(true);
                yield return new ModelMessageStartEvent();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            finally { IteratorDisposed = true; }
        }
    }
}
