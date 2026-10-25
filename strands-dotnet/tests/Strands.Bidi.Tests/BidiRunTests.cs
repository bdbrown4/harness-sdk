using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRunTests
{
    [Fact]
    public async Task RunStartsInputThenOutputAndStopsBorrowedStreamsInReverseBeforeTheAgentStopHook()
    {
        var seen = new List<string>();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { seen.Add("agent-stop"); return default; });
        var first = Input("first"); var second = Input("second");
        var output = new BidiOutputStream((value, _) => { Assert.IsType<BidiConnectionStartEvent>(value); seen.Add("output"); return default; },
            (_, _) => { seen.Add("out-start"); return default; }, token => { Assert.False(token.CanBeCanceled); seen.Add("out-stop"); return default; });

        await agent.RunAsync([first, second], [output]);

        Assert.Equal(1, model.Starts); Assert.Equal(1, model.Stops);
        Assert.Equal(new[] { "first-start", "second-start", "out-start", "output", "out-stop", "second-stop", "first-stop", "agent-stop" }, seen);
        BidiInputStream Input(string name) => new(async token => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); },
            (_, _) => { seen.Add(name + "-start"); return default; }, token => { Assert.False(token.CanBeCanceled); seen.Add(name + "-stop"); return default; });
    }

    [Fact]
    public async Task StartingARunOnAnActiveConversationLeavesItsConnectionAndUnusedStreamsIntact()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); var streamCalls = 0;
        var input = new BidiInputStream(_ => { streamCalls++; return new ValueTask<IBidiInput>(new BidiMessage([new TextBlock("unused")])); },
            (_, _) => { streamCalls++; return default; }, _ => { streamCalls++; return default; });
        await agent.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync([input], []));

        Assert.Equal(1, model.Starts); Assert.Equal(0, model.Stops); Assert.Equal(0, streamCalls);
        await agent.SendAsync(new BidiMessage([new TextBlock("still live")])); Assert.Single(model.Inputs); await agent.StopAsync();
    }

    [Fact]
    public async Task PartialStreamStartupRetainsItsPrimaryAndCleansOnlyEnteredLifecyclesInReverse()
    {
        var failure = new IOException("second start"); var cleanup = new IOException("second stop"); var seen = new List<string>();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var first = new BidiInputStream(_ => throw new InvalidOperationException("must not read"),
            (_, _) => { seen.Add("first-start"); return default; }, _ => { seen.Add("first-stop"); return default; });
        var second = new BidiInputStream(_ => throw new InvalidOperationException("must not read"),
            (_, _) => { seen.Add("second-start"); throw failure; }, _ => { seen.Add("second-stop"); throw cleanup; });
        var unused = new BidiOutputStream((_, _) => throw new InvalidOperationException("must not write"),
            (_, _) => throw new InvalidOperationException("must not start"), _ => throw new InvalidOperationException("must not stop"));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.RunAsync([first, second], [unused])));

        Assert.Equal(new[] { "first-start", "second-start", "second-stop", "first-stop" }, seen);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, model.Starts); Assert.Equal(1, model.Stops);
    }
}
