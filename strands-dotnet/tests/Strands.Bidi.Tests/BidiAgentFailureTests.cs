using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;

namespace Strands.Bidi.Tests;

public sealed class BidiAgentFailureTests
{
    [Fact]
    public async Task AProgressObserverFailureRetainsItsIdentityAndAllCleanupFailures()
    {
        var primary = new IOException("progress observer"); var iteratorCleanup = new IOException("iterator cleanup"); var afterCleanup = new IOException("after cleanup");
        var tool = new StreamingFunctionTool(new("stream", "Stream data", JsonSerializer.SerializeToElement(new { type = "object" })), Stream);
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        host.Hooks.Register<ToolStreamUpdateEvent>((_, _) => throw primary);
        host.Hooks.Register<AfterToolCallEvent>((_, _) => throw afterCleanup);
        await using var model = new ScriptedModel(new BidiToolUseBlocksEvent([new("call", "stream", JsonSerializer.SerializeToElement(new { }))]));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var error = await Assert.ThrowsAsync<IOException>(async () => { await foreach (var item in agent.ReceiveAsync(safety.Token)) { } });
        Assert.Same(primary, error);
        var cleanup = Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]);
        Assert.Contains(iteratorCleanup, cleanup); Assert.Contains(afterCleanup, cleanup);
        await agent.StopAsync();
        async IAsyncEnumerable<IToolStreamEvent> Stream(JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        { try { yield return new ToolStreamEvent("update"); await Task.CompletedTask; } finally { throw iteratorCleanup; } }
    }

    [Fact]
    public async Task AGracefullyClosedModelCompletesTheReceiverAfterItsFinalEvents()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel(new BidiTextStartEvent("t"), new BidiTextDeltaEvent("done", "t"), new BidiTextStopEvent("t")) { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2)); var output = new List<IAgentStreamEvent>();
        await foreach (var item in agent.ReceiveAsync(safety.Token)) output.Add(item);
        Assert.IsType<BidiTextBlockEvent>(output[output.Count - 1]); Assert.Equal("done", Assert.Single(agent.Messages).Text);
        await agent.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedToolIdentifiersCannotExecuteSideEffectsTwice(bool separateBatches)
    {
        var effects = 0;
        var firstEffect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new ToolUseBlock("repeated", "effect", JsonSerializer.SerializeToElement(new { }));
        var tool = new FunctionTool(new("effect", "Perform a side effect", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { effects++; firstEffect.TrySetResult(true); return new ValueTask<string>("done"); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = separateBatches ? new ScriptedModel(new BidiToolUseBlocksEvent([call]), new BidiToolUseBlocksEvent([call]))
        { BeforeEvent = index => index == 1 ? firstEffect.Task : Task.CompletedTask } : new ScriptedModel(new BidiToolUseBlocksEvent([call, call]));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ModelException>(async () => { await foreach (var item in agent.ReceiveAsync(safety.Token)) { } });
        await agent.StopAsync();
        Assert.Equal(separateBatches ? 1 : 0, effects);
    }
}
