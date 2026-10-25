using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed partial class BashTests
{
    [Fact]
    public Task CommandsInOneSessionSerializeThroughTheCompletionFences() => WithFixture(async fixture =>
    {
        var gate = fixture.Gate();
        var secondMarker = Path.Combine(fixture.Root, "second-entered");
        var first = fixture.Start(gate.Command + "; STRANDS_SERIAL_VALUE=first; builtin printf first");
        await BashFixture.Ready(gate.Ready);
        var second = fixture.Start("builtin printf entered > " + ShellPath(secondMarker) + "; builtin printf '%s' \"$STRANDS_SERIAL_VALUE\"");
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        Assert.False(File.Exists(secondMarker));
        BashFixture.Release(gate.Release);
        Assert.Equal("first", Output(await first));
        Assert.Equal("first", Output(await second));
    });

    [Fact]
    public Task CancelingAQueuedCallerDoesNotCancelTheActiveCommand() => WithFixture(async fixture =>
    {
        var gate = fixture.Gate();
        var first = fixture.Start(gate.Command + "; builtin printf retained");
        await BashFixture.Ready(gate.Ready);
        using var caller = new CancellationTokenSource();
        var queued = fixture.Start("STRANDS_MUST_NOT_RUN=yes", caller: caller.Token);
        Assert.False(queued.IsCompleted);
        caller.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);
        Assert.Equal(caller.Token, canceled.CancellationToken);
        Assert.False(first.IsCompleted);
        BashFixture.Release(gate.Release);
        Assert.Equal("retained", Output(await first));
        Assert.Equal("absent", await fixture.Run("builtin printf '%s' \"${STRANDS_MUST_NOT_RUN-absent}\""));
        _ = caller.Token;
    });

    [Fact]
    public Task CancelingAnActiveCallerJoinsItsInterpreterAndPreservesTheCallerToken() => WithFixture(async fixture =>
    {
        var gate = fixture.Gate();
        using var caller = new CancellationTokenSource();
        var active = fixture.Start("STRANDS_CANCEL_VALUE=old; " + gate.Command, caller: caller.Token);
        await BashFixture.Ready(gate.Ready);
        using var root = BindOwnedBashRoot(fixture);
        var processes = fixture.ObserveNewInterpreters();
        Assert.Contains(processes, process => process.Id == root.Id);
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await active);
        AssertOwnedAssociationExited(root, processes, "active-caller-cancellation");
        Assert.Equal(caller.Token, error.CancellationToken);
        _ = caller.Token;
        Assert.Equal("fresh", await fixture.Run("builtin printf '%s' \"${STRANDS_CANCEL_VALUE-fresh}\""));
    });

    [Fact]
    public Task ActiveContextCancellationUsesTheRealAgentSignalWithoutOwningTheHardCaller() => WithFixture(async fixture =>
    {
        var gate = fixture.Gate();
        var wrapper = new ContextOnlyBash(fixture.Tool);
        var model = new BashCallingModel(gate.Command);
        var agent = fixture.AddAgent(new() { Model = model, Tools = [wrapper] });
        using var hardCaller = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // The outer fixture releases the barrier and joins this invocation even if readiness/assertion fails.
        var invocation = fixture.Track(agent.InvokeAsync("run", cancellationToken: hardCaller.Token));
        await BashFixture.Ready(gate.Ready);
        using var root = BindOwnedBashRoot(fixture, agent);
        var processes = fixture.ObserveNewInterpreters();
        Assert.Contains(processes, process => process.Id == root.Id);
        agent.Cancel();
        var result = await invocation;
        AssertOwnedAssociationExited(root, processes, "active-context-cancellation");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(wrapper.Failure);
        Assert.True(wrapper.Signal.IsCancellationRequested);
        Assert.Equal(wrapper.Signal, canceled.CancellationToken);
        Assert.False(hardCaller.IsCancellationRequested);
        Assert.Equal(1, model.Calls);
    });

    [Fact]
    public Task RestartCutsOffAnActiveAndQueuedOldEpochBeforeNewCommands() => WithFixture(async fixture =>
    {
        var gate = fixture.Gate();
        var active = fixture.Start("STRANDS_EPOCH_VALUE=old; " + gate.Command);
        await BashFixture.Ready(gate.Ready);
        var queued = fixture.Start("builtin printf must-not-run");
        Assert.False(queued.IsCompleted);
        using var root = BindOwnedBashRoot(fixture);
        var processes = fixture.ObserveNewInterpreters();
        Assert.Contains(processes, process => process.Id == root.Id);
        var restarted = fixture.Restart();
        var fresh = fixture.Start("builtin printf '%s' \"${STRANDS_EPOCH_VALUE-fresh}\"");
        Assert.IsType<BashSessionException>(await Failed(active));
        Assert.IsType<BashSessionException>(await Failed(queued));
        Assert.Equal("Bash session restarted", (await restarted).Content);
        AssertOwnedAssociationExited(root, processes, "active-epoch-restart");
        Assert.Equal("fresh", Output(await fresh));
    });

    [Fact]
    public Task ConcurrentRestartRequestsJoinTheOldGenerationAndRemainReusable() => WithFixture(async fixture =>
    {
        await fixture.Run("STRANDS_CONCURRENT_RESTART=old");
        var first = fixture.Restart();
        var second = fixture.Restart();
        Assert.Equal("Bash session restarted", (await first).Content);
        Assert.Equal("Bash session restarted", (await second).Content);
        Assert.Equal("fresh", await fixture.Run("builtin printf '%s' \"${STRANDS_CONCURRENT_RESTART-fresh}\""));
    });

    [Fact]
    public Task AnInterpreterWhichExitsWhileIdleIsJoinedAndLazilyReplaced() => WithFixture(async fixture =>
    {
        Assert.Equal("armed", await fixture.Run("STRANDS_IDLE_VALUE=old; builtin printf armed"));
        // Bind the exact SDK-owned root; shell $$ and same-image membership are not Windows process identity.
        using var root = BindOwnedBashRoot(fixture);
        Assert.False(root.HasExited);
        root.Kill();
        Assert.True(root.WaitForExit(5000), "The SDK-owned root did not reach the deliberate physical idle exit.");
        Assert.Equal("fresh", await fixture.Run("builtin printf '%s' \"${STRANDS_IDLE_VALUE-fresh}\""));
    });

    private sealed class ContextOnlyBash(BashTool inner) : IRichTool
    {
        public ToolSpec Spec => inner.Spec;
        internal CancellationToken Signal { get; private set; }
        internal Exception? Failure { get; private set; }
        public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        {
            Signal = context.CancelSignal;
            try { return await inner.InvokeResultAsync(input, context, CancellationToken.None); }
            catch (Exception error) { Failure = error; throw; }
        }
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
            => (await InvokeResultAsync(input, context, cancellationToken)).Content;
    }
    private sealed class BashCallingModel(string command) : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Calls++;
            yield return new ModelMessageStartEvent();
            if (Calls == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("context-bash", "bash", JsonSerializer.SerializeToElement(new { mode = "execute", command, timeout = 10 })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
