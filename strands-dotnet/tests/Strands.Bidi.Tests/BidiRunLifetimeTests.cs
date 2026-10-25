using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRunLifetimeTests
{
    [Fact]
    public async Task AgentStopHooksWaitForPhysicalInputExitAfterItsLogicalCancellation()
    {
        var release = Signal(); var stopping = Signal(); var physicalExit = false; var observed = false;
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiAgentStopEvent>((_, _) => { observed = true; Assert.True(physicalExit); return default; });
        var input = new BidiInputStream(async token => { try { await release.Task; token.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); } finally { physicalExit = true; } },
            stop: _ => { stopping.TrySetResult(true); return default; });
        var run = agent.RunAsync([input], []);
        try { await Within(stopping.Task); Assert.False(run.IsCompleted); }
        finally { release.TrySetResult(true); }
        await run; Assert.True(observed); Assert.True(physicalExit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStopOrDisposeCannotReturnBeforeTheRunHasDrainedItsBorrowedInput(bool dispose)
    {
        var release = Signal(); var stopping = Signal(); var physicalExit = false;
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ScriptedModel { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new BidiInputStream(async token => { try { await release.Task; token.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); } finally { physicalExit = true; } },
            stop: _ => { stopping.TrySetResult(true); return default; });
        var run = agent.RunAsync([input], []); await Within(stopping.Task);
        var cleanup = dispose ? agent.DisposeAsync().AsTask() : agent.StopAsync();
        try { Assert.False(cleanup.IsCompleted); Assert.False(physicalExit); }
        finally { release.TrySetResult(true); }
        await cleanup; await run; Assert.True(physicalExit); Assert.Equal(1, model.Stops);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
