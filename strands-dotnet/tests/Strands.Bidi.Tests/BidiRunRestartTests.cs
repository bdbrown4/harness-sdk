using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.BidiRunConcurrencyTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRunRestartTests
{
    [Fact]
    public async Task AConnectionRestartKeepsTheSameRunIoLifecyclesAndCallerState()
    {
        var started = Signal(); var inputStarts = 0; var inputStops = 0; var reads = 0; var seen = new List<IAgentStreamEvent>();
        var state = new Dictionary<string, object?> { ["caller"] = "unchanged" };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new BidiInputStream(async token => { reads++; await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); },
            (_, _) => { inputStarts++; started.TrySetResult(true); return default; }, _ => { inputStops++; return default; });
        var output = new BidiOutputStream((value, _) => { seen.Add(value); return default; });
        var run = agent.RunAsync([input], [output], state); await Within(started.Task); model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
        await Within(model.Reconnected.Task); model.Current.Emit(new BidiResponseStopEvent("recovered")); model.Current.Close(); await run;
        Assert.Equal(1, model.Restarts); Assert.Equal(1, inputStarts); Assert.Equal(1, inputStops); Assert.Equal(1, reads);
        Assert.IsType<BidiConnectionRestartEvent>(seen[0]); Assert.Equal("recovered", Assert.IsType<BidiResponseStopEvent>(seen[1]).ResponseId);
        Assert.Single(state); Assert.Equal("unchanged", state["caller"]);
    }
}
