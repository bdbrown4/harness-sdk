using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRunConcurrencyTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRunSendCallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANativeSendCallbackCannotReenterItsOwnSendOrStop(bool stop)
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var entered = Signal(); Task? nested = null; var sends = 0; var reads = 0;
        model.OnSend = _ =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                nested = stop ? agent.StopAsync() : agent.SendAsync(new BidiMessage([new TextBlock("recursive")])); entered.TrySetResult(true);
            }
            return Task.CompletedTask;
        };
        var input = new BidiInputStream(async token =>
        {
            if (Interlocked.Increment(ref reads) == 1) return new BidiMessage([new TextBlock("first")]);
            await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable");
        });
        var run = agent.RunAsync([input], []);
        try { await Within(entered.Task); await Assert.ThrowsAsync<InvalidOperationException>(() => nested!); Assert.Equal(1, sends); Assert.Equal(0, model.Stops); }
        finally { model.Events.Close(); await run; }
    }
}
