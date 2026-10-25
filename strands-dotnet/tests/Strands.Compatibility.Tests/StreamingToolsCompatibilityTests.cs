using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Compatibility.Tests;

public sealed class StreamingToolsCompatibilityTests
{
    [Fact]
    public async Task EarlyDisposalPreservesPrimaryCleanupFailureAcrossTheInvocationBoundary()
    {
        var primary = new InvalidOperationException("after-tool cleanup");
        var secondary = new IOException("after-invocation cleanup");
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((_, _) => throw primary);
        hooks.Register<AfterInvocationEvent>((_, _) => throw secondary);
        var tool = new StreamingFunctionTool(Spec(), (_, _, token) => Waiting(() => { }, () => { }, token));
        await using var agent = new Agent(new() { Model = new Model("call"), Tools = [tool], Hooks = hooks });
        var iterator = agent.StreamAsync("go").GetAsyncEnumerator();
        while (await iterator.MoveNextAsync() && iterator.Current is not ToolStreamUpdateEvent) { }

        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => iterator.DisposeAsync().AsTask()));

        Assert.Same(secondary, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task ProgressAndRichTerminalRetainHookOrderCorrelationAndDetachedJson()
    {
        var order = new List<string>();
        var state = new Dictionary<string, object?> { ["caller"] = 7 };
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => { order.Add("before"); value.ToolUse = value.ToolUse with { ToolUseId = "forged" }; return default; });
        hooks.Register<ToolStreamUpdateEvent>((_, _) => { order.Add("progress"); return default; });
        hooks.Register<AfterToolCallEvent>((value, _) => { order.Add("after"); value.Result = value.Result with { ToolUseId = "forged" }; return default; });
        hooks.Register<ToolResultEvent>((_, _) => { order.Add("result"); return default; });
        var stream = new StreamingFunctionTool(Spec(), (_, context, _) =>
        {
            Assert.Equal("call", context.ToolUse.ToolUseId);
            Assert.Same(state, context.InvocationState);
            using var document = JsonDocument.Parse("{\"value\":42}");
            return new Sequence([new ToolStreamEvent(document), new ToolStreamResultEvent(new(context.ToolUse.ToolUseId,
                [new TextBlock("display"), new JsonBlock(document.RootElement), new ImageBlock("image/png", "aGVsbG8=")], structuredContent: document.RootElement))]);
        });
        var model = new Model("call");
        await using var agent = new Agent(new() { Model = model, Tools = [stream], Hooks = hooks });
        var progressCount = 0;
        var terminalCount = 0;

        await foreach (var item in agent.StreamAsync("go", new() { InvocationState = state }))
        {
            if (item is ToolStreamUpdateEvent update)
            {
                progressCount++;
                Assert.Single(model.Requests);
                Assert.Equal("call", update.ToolUse.ToolUseId);
                Assert.Equal(42, Assert.IsType<JsonElement>(update.Event.Data).GetProperty("value").GetInt32());
            }
            if (item is ToolResultEvent terminal)
            {
                terminalCount++;
                Assert.Single(model.Requests);
                Assert.Equal("call", terminal.Result.ToolUseId);
                Assert.Same(state, terminal.InvocationState);
            }
        }

        Assert.Equal(1, progressCount);
        Assert.Equal(1, terminalCount);
        Assert.Equal(["before", "progress", "after", "result"], order);
        var result = Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[2].Content[0]);
        Assert.Equal("call", result.ToolUseId);
        Assert.Equal("display", result.Content);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("value").GetInt32());
        Assert.Equal(42, Assert.IsType<JsonBlock>(result.ContentBlocks[1]).Json.GetProperty("value").GetInt32());
        Assert.IsType<ImageBlock>(result.ContentBlocks[2]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong_id")]
    [InlineData("after_result")]
    public async Task MalformedToolStreamsAreRejectedAndDisposed(string kind)
    {
        var result = new ToolStreamResultEvent(new("call", "done"));
        var sequence = new Sequence(kind switch
        {
            "missing" => [new ToolStreamEvent("progress")],
            "duplicate" => [result, result],
            "wrong_id" => [new ToolStreamResultEvent(new("other", "done"))],
            _ => [result, new ToolStreamEvent("late")]
        });
        var tool = new StreamingFunctionTool(Spec(), (_, _, _) => sequence);
        await using var agent = new Agent(new() { Model = new Model("call") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeResultAsync(Json("{}"), new(agent, new("call", "stream", Json("{}")), new Dictionary<string, object?>())).AsTask());

        Assert.Equal(1, sequence.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryAndCleanupFailuresRetainIdentityAndStayOutOfSerializedResults(bool throughAgent)
    {
        var primary = new ApplicationException("body failed");
        var cleanup = new IOException("cleanup failed");
        var sequence = new Sequence([], primary, cleanup);
        var tool = new StreamingFunctionTool(Spec(), (_, _, _) => sequence);
        await using var agent = new Agent(new() { Model = new Model("call"), Tools = [tool] });
        if (throughAgent)
        {
            Assert.Equal("done", (await agent.InvokeAsync("go")).ToString());
            var result = Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]);
            Assert.Same(primary, result.Error);
            Assert.Equal(ToolResultStatus.Error, result.Status);
            using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.False(serialized.RootElement.TryGetProperty(nameof(ToolResultBlock.Error), out _));
        }
        else
        {
            Assert.Same(primary, await Assert.ThrowsAsync<ApplicationException>(() => tool.InvokeResultAsync(Json("{}"), new(agent, new("call", "stream", Json("{}")), new Dictionary<string, object?>())).AsTask()));
        }

        Assert.Equal(1, sequence.Disposals);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task EarlyConsumerDisposalCancelsBlockedProgressAndRetainsCompletedSiblingEffects()
    {
        var stopped = false;
        var advances = 0;
        var tool = new BorrowedTool((context, token) => context.ToolUse.ToolUseId == "a"
            ? Waiting(() => advances++, () => stopped = true, token)
            : new Sequence([new ToolStreamResultEvent(new("b", "saved"))]));
        var model = new Model("a", "b");
        var agent = new Agent(new() { Model = model, Tools = [tool], MaxParallelTools = 2 });

        await using (var iterator = agent.StreamAsync("go").GetAsyncEnumerator())
            while (await iterator.MoveNextAsync())
                if (iterator.Current is ToolResultEvent { Result.ToolUseId: "b" }) break;

        Assert.True(stopped);
        Assert.Equal(1, advances);
        var results = agent.Messages[2].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(["a", "b"], results.Select(result => result.ToolUseId));
        Assert.Equal(ToolResultStatus.Error, results[0].Status);
        Assert.Equal(ToolResultStatus.Success, results[1].Status);
        Assert.Equal("saved", results[1].Content);
        Assert.Equal("done", (await agent.InvokeAsync(null)).ToString());
        await agent.DisposeAsync();
        Assert.Equal(0, tool.Disposals);
    }

    [Fact]
    public async Task ProgressHookFailureUnblocksTheProducerAndRetainsAfterHookCleanupFailure()
    {
        var primary = new InvalidOperationException("progress failed");
        var cleanup = new IOException("after-hook cleanup");
        var hooks = new HookRegistry();
        hooks.Register<ToolStreamUpdateEvent>((_, _) => throw primary);
        hooks.Register<AfterToolCallEvent>((_, _) => throw cleanup);
        var sequence = new Sequence([new ToolStreamEvent("working"), new ToolStreamResultEvent(new("call", "done"))]);
        var tool = new StreamingFunctionTool(Spec(), (_, _, _) => sequence);
        await using var agent = new Agent(new() { Model = new Model("call"), Tools = [tool], Hooks = hooks });

        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("go")));

        Assert.Equal(1, sequence.Disposals);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal("call", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).ToolUseId);
    }

    private static ToolSpec Spec() => new("stream", "Stream progress", Json("{\"type\":\"object\"}"));
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static async IAsyncEnumerable<IToolStreamEvent> Waiting(Action onAdvance, Action onDispose, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            onAdvance();
            yield return new ToolStreamEvent("waiting");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally { onDispose(); }
    }

    private sealed class BorrowedTool(Func<ToolContext, CancellationToken, IAsyncEnumerable<IToolStreamEvent>> callback) : IStreamingTool, IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ToolSpec Spec { get; } = StreamingToolsCompatibilityTests.Spec();
        public IAsyncEnumerable<IToolStreamEvent> StreamAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => callback(context, cancellationToken);
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The streaming capability must be selected.");
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class Sequence(IReadOnlyList<IToolStreamEvent> events, Exception? primary = null, Exception? cleanup = null) : IAsyncEnumerable<IToolStreamEvent>, IAsyncEnumerator<IToolStreamEvent>
    {
        private int _index = -1;
        public int Disposals { get; private set; }
        public IToolStreamEvent Current => events[_index];
        public IAsyncEnumerator<IToolStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { if (++_index < events.Count) return new(true); if (primary is not null) throw primary; return new(false); }
        public ValueTask DisposeAsync() { Disposals++; if (cleanup is not null) throw cleanup; return default; }
    }

    private sealed class Model(params string[] calls) : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            if (Requests.Count == 1)
            {
                for (var index = 0; index < calls.Length; index++)
                {
                    yield return new ModelContentBlockStartEvent(index, new(calls[index], "stream", Json("{}")));
                    yield return new ModelContentBlockDeltaEvent(index, ToolInput: "{}");
                    yield return new ModelContentBlockStopEvent(index);
                }
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            await Task.CompletedTask;
        }
    }
}
