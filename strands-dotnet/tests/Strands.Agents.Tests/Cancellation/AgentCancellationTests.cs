using System.Text.Json;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationTests
{
    [Fact]
    public async Task ToolCanRequestImmediateCancellationWithoutOpeningAnotherModelTurn()
    {
        var model = new CancellationTestModel(CancellationTestModel.Call(("stop-call", "stop", "{}")), CancellationTestModel.Text("unwanted second turn"));
        var signaled = false;
        var stop = Tool("stop", (_, context, _) =>
        {
            context.Agent.Cancel();
            signaled = context.Agent.CancelSignal.IsCancellationRequested;
            return new("accepted");
        });
        await using var agent = new Agent(new() { Model = model, Tools = [stop] });
        var result = await agent.InvokeAsync("perform the operation");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.True(signaled);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task DeferredCancellationFinishesSequentialPeersAndHonorsExplicitTerminalText()
    {
        var model = new CancellationTestModel(CancellationTestModel.Call(("stop-call", "stop", "{}"), ("peer-call", "peer", "{}")), CancellationTestModel.Text("unwanted second turn"));
        var peerCalls = 0;
        var peerWasCancelled = false;
        var stop = Tool("stop", (_, context, _) =>
        {
            context.Agent.Cancel("finished the accepted batch", afterCurrentTools: true);
            return new("stop requested");
        });
        var peer = Tool("peer", (_, context, _) =>
        {
            peerCalls++;
            peerWasCancelled = context.Agent.CancelSignal.IsCancellationRequested;
            return new("peer completed");
        });
        await using var agent = new Agent(new() { Model = model, Tools = [stop, peer], ToolExecution = ToolExecutionStrategy.Sequential });
        var result = await agent.InvokeAsync("finish both accepted operations");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(1, peerCalls);
        Assert.False(peerWasCancelled);
        Assert.Single(model.Requests);
        Assert.Equal("finished the accepted batch", Assert.Single(Assert.IsType<Message>(result.Message).Content.OfType<TextBlock>()).Text);
    }

    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback) =>
        new(new(name, name, JsonSerializer.SerializeToElement(new { type = "object" })), callback);
}
