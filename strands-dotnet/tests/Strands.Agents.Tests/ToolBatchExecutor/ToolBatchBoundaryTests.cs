using System.Runtime.CompilerServices;
using System.Text.Json;
using static Strands.Agents.Tests.ToolBatchExecutor.BatchTestSupport;

namespace Strands.Agents.Tests.ToolBatchExecutor;

public sealed class ToolBatchBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnPipelineCannotRescheduleWithFlowingOrSuppressedSynchronousContext(bool suppress)
    {
        ToolBatchExecutionContext? current = null; var rejected = 0; var baseline = ExecutionContext.Capture()!;
        var scheduler = new Scheduler(async (batch, _) => { current = batch; await batch.ExecuteAsync(0); });
        await using var agent = Agent(Tool(async (_, _, _) =>
        {
            if (!suppress) await Task.Yield();
            void Attempt() { Assert.IsType<InvalidOperationException>(Record.Exception(() => { current!.ExecuteAsync(1); })); rejected++; }
            if (suppress) ExecutionContext.Run(baseline.CreateCopy(), _ => Attempt(), null); else Attempt();
            return "ok";
        }), scheduler, calls: 2);
        await agent.InvokeAsync("run"); Assert.Equal(1, rejected);
    }

    [Fact]
    public async Task EscapedContextExpiresWhenItsPipelinePhysicallyReturns()
    {
        var escapedReady = Signal(); var release = Signal(); ToolBatchExecutionContext? current = null; Task? escaped = null; var bodies = 0;
        var scheduler = new Scheduler(async (batch, _) => { current = batch; await batch.ExecuteAsync(0); release.TrySetResult(true); await escaped!; });
        await using var agent = Agent(Tool((_, context, _) =>
        {
            Interlocked.Increment(ref bodies);
            if (context.ToolUse.ToolUseId == "0") escaped = Task.Run(async () => { escapedReady.TrySetResult(true); await release.Task; Assert.NotNull(await current!.ExecuteAsync(1)); });
            return new("ok");
        }), scheduler, calls: 2);
        var invocation = agent.InvokeAsync("run");
        try { await Ready(escapedReady.Task); await invocation; }
        finally { release.TrySetResult(true); await invocation; if (escaped is not null) await escaped; }
        Assert.Equal(2, bodies);
    }

    [Fact]
    public async Task DetachedCallsAndRichResultsDoNotLeakMutableBatchMembershipOrJson()
    {
        using var input = JsonDocument.Parse("{\"value\":7}"); using var payload = JsonDocument.Parse("{\"value\":8}");
        ToolResultBlock? visible = null; var source = new List<ContentBlock> { new JsonBlock(payload.RootElement), new ImageBlock("image/png", "AQID") };
        var tool = new RichTool(source, payload.RootElement);
        var scheduler = new Scheduler(async (batch, _) =>
        {
            Assert.Throws<NotSupportedException>(() => ((IList<ToolUseBlock>)batch.Calls)[0] = Call("fake"));
            Assert.Equal(7, batch.Calls[0].Input.GetProperty("value").GetInt32());
            visible = await batch.ExecuteAsync(0); source.Clear();
            Assert.Equal(2, visible!.ContentBlocks.Count); Assert.Equal(8, visible.StructuredContent!.Value.GetProperty("value").GetInt32());
        });
        await using var agent = new Agent(new() { Model = new Model(new ToolUseBlock("0", "work", input.RootElement)), Tools = [tool], ToolBatchExecutor = scheduler, MaxTurns = 1 });
        await agent.InvokeAsync("run"); input.Dispose(); payload.Dispose();
        var committed = Assert.Single(agent.Messages[^1].Content.OfType<ToolResultBlock>());
        Assert.NotSame(visible, committed); Assert.NotSame(visible!.ContentBlocks, committed.ContentBlocks);
        Assert.Equal(8, Assert.IsType<JsonBlock>(visible.ContentBlocks[0]).Json.GetProperty("value").GetInt32()); Assert.Equal("AQID", Assert.IsType<ImageBlock>(committed.ContentBlocks[1]).Data);
    }

    [Fact]
    public async Task DirectToolCallsBypassCustomBatchScheduler()
    {
        var scheduler = new Scheduler((_, _) => throw new InvalidOperationException("not a model batch")); var bodies = 0;
        await using var agent = Agent(Tool((_, _, _) => { bodies++; return new("direct"); }), scheduler);
        var result = await agent.InvokeToolAsync(Call("direct")); Assert.Equal("direct", result.Content); Assert.Equal(0, scheduler.Calls); Assert.Equal(1, bodies);
    }

    [Fact]
    public async Task StructuredOutputStillUsesTheTemporaryValidatedPipeline()
    {
        var scheduler = new Scheduler(async (batch, _) => { Assert.NotNull(await batch.ExecuteAsync(0)); });
        await using var agent = new Agent(new() { Model = new Model(Call("structured", StructuredOutputTool<Answer>.ToolName, "{\"value\":7}")), ToolBatchExecutor = scheduler });
        var result = await agent.InvokeStructuredAsync("run", new StructuredOutputOptions<Answer> { Schema = Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"]}") });
        Assert.Equal(7, result.Output.Value); Assert.Equal("structured", result.Result.StructuredOutputToolUseId); Assert.Equal(1, scheduler.Calls);
    }

    [Fact]
    public async Task SharedBorrowedExecutorIsInheritedByIndependentChildrenAndNeverDisposed()
    {
        var scheduler = new Scheduler(async (batch, _) => { for (var i = 0; i < batch.Calls.Count; i++) await batch.ExecuteAsync(i); }); var bodies = 0;
        var parent = Agent(Tool((_, _, _) => { Interlocked.Increment(ref bodies); return new("ok"); }), scheduler, calls: 1);
        var child = parent.CreateChild(options => options with { Model = new Model(Call("child")) });
        try { await Task.WhenAll(parent.InvokeAsync("parent"), child.InvokeAsync("child")); Assert.Equal(2, bodies); Assert.Equal(2, scheduler.Calls); }
        finally { await child.DisposeAsync(); await parent.DisposeAsync(); }
        Assert.False(scheduler.Disposed);
    }

    [Fact]
    public async Task EarlyStreamDisposalPhysicallyJoinsTheAcceptedIteratorCleanup()
    {
        var cleanup = Signal(); var release = Signal(); var disposed = false;
        var scheduler = new Scheduler((batch, token) => { _ = batch.ExecuteAsync(0); return default; });
        var tool = new StreamingFunctionTool(new("work", "Stream progress", Parse("{\"type\":\"object\"}")), Stream);
        await using var agent = Agent(tool, scheduler, calls: 1); var enumerator = agent.StreamAsync("run").GetAsyncEnumerator(); Task? close = null;
        try
        {
            while (await enumerator.MoveNextAsync()) if (enumerator.Current is ToolStreamUpdateEvent) break;
            close = enumerator.DisposeAsync().AsTask(); await Ready(cleanup.Task); Assert.False(close.IsCompleted); Assert.False(disposed);
        }
        finally { release.TrySetResult(true); if (close is not null) await close; else await enumerator.DisposeAsync(); }
        Assert.True(disposed); Assert.Single(agent.Messages[^1].Content.OfType<ToolResultBlock>());

        async IAsyncEnumerable<IToolStreamEvent> Stream(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken token)
        {
            try { yield return new ToolStreamEvent("progress"); await Task.Delay(Timeout.Infinite, token); }
            finally { cleanup.TrySetResult(true); await release.Task; disposed = true; }
        }
    }

    public sealed record Answer(int Value);
    private sealed class RichTool(IReadOnlyList<ContentBlock> blocks, JsonElement structured) : IRichTool
    {
        public ToolSpec Spec { get; } = new("work", "Rich result", Parse("{\"type\":\"object\"}"));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("rich");
        public ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new(new ToolResultBlock(context.ToolUse.ToolUseId, "rich") { ContentBlocks = blocks, StructuredContent = structured });
    }
}
