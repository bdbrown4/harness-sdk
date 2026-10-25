using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.ToolBatchExecutor;

public sealed class ToolBatchExecutorTests
{
    [Fact]
    public async Task CustomOrderRunsExistingPipelineAndCommitsIssuedOrder()
    {
        var order = new List<string>(); var state = new Dictionary<string, object?> { ["request"] = 42 }; var scheduled = 0;
        var executor = new Scheduler(async (batch, _) => { scheduled++; Assert.Same(state, batch.InvocationState); foreach (var index in new[] { 2, 0, 1 }) Assert.NotNull(await batch.ExecuteAsync(index)); });
        await using var agent = Create(order, executor); await agent.InvokeAsync("run", new() { InvocationState = state });
        Assert.Equal(1, scheduled); Assert.Equal(new[] { "c", "a", "b" }, order); Assert.Equal(new[] { "a", "b", "c" }, agent.Messages[^1].Content.OfType<ToolResultBlock>().Select(x => x.ToolUseId));
    }

    [Fact]
    public async Task AnIncompleteSchedulerCannotExecuteUnadmittedBodiesOrForgeSuccess()
    {
        var order = new List<string>(); var scheduled = 0; var executor = new Scheduler((_, _) => { scheduled++; return default; });
        await using var agent = Create(order, executor); await agent.InvokeAsync("run"); Assert.Equal(1, scheduled); Assert.Empty(order);
        var results = agent.Messages[^1].Content.OfType<ToolResultBlock>().ToArray(); Assert.Equal(3, results.Length); Assert.All(results, x => { Assert.Equal(ToolResultStatus.Error, x.Status); Assert.Contains("did not schedule", x.Content); });
    }

    [Fact]
    public async Task DuplicateIndexReturnsSameTaskAndRunsOnlyOnce()
    {
        var order = new List<string>(); var scheduled = 0;
        var executor = new Scheduler(async (batch, _) => { scheduled++; var first = batch.ExecuteAsync(1); Assert.Same(first, batch.ExecuteAsync(1)); Assert.NotNull(await first); });
        await using var agent = Create(order, executor); await agent.InvokeAsync("run"); Assert.Equal(1, scheduled); Assert.Equal(new[] { "b" }, order);
        var results = agent.Messages[^1].Content.OfType<ToolResultBlock>().ToArray(); Assert.Equal(new[] { ToolResultStatus.Error, ToolResultStatus.Success, ToolResultStatus.Error }, results.Select(x => x.Status));
    }

    private static Agent Create(List<string> order, IToolBatchExecutor executor)
    {
        var tool = new FunctionTool(new("work", "Record original call", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => { order.Add(context.ToolUse.ToolUseId); return new("done"); });
        return new(new() { Model = new BatchModel(), Tools = [tool], MaxTurns = 1, ToolBatchExecutor = executor });
    }
    private sealed class Scheduler(Func<ToolBatchExecutionContext, CancellationToken, ValueTask> callback) : IToolBatchExecutor
    {
        public ValueTask ExecuteAsync(ToolBatchExecutionContext execution, CancellationToken cancellationToken = default) => callback(execution, cancellationToken);
    }
    private sealed class BatchModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent();
            var index = 0;
            foreach (var id in new[] { "a", "b", "c" }) { yield return new ModelContentBlockStartEvent(index, new(id, "work", JsonSerializer.SerializeToElement(new { }))); yield return new ModelContentBlockDeltaEvent(index, ToolInput: "{}"); yield return new ModelContentBlockStopEvent(index++); }
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }
}
