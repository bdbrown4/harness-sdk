using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRunConcurrencyTests
{
    [Fact]
    public async Task InputsReadConcurrentlyAndRealToolsKeepTheOriginalCallerState()
    {
        var entered = new[] { Signal(), Signal() }; var release = Signal(); var sent = Signal(); var received = 0; var effects = 0;
        var state = new Dictionary<string, object?> { ["caller"] = 7 };
        var tool = new FunctionTool(new("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, context, _) => { Assert.Same(state, context.InvocationState); effects++; return new ValueTask<string>("answer"); });
        await using var host = new Agent(new() { Model = new NoCallsModel(), Tools = [tool] });
        await using var model = new RunModel { OnSend = input => { if (input is BidiMessage message && message.Content[0] is TextBlock && Interlocked.Increment(ref received) == 2) sent.TrySetResult(true); return Task.CompletedTask; } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var run = agent.RunAsync([Input(0), Input(1)], [], state);
        try
        {
            await Within(Task.WhenAll(entered.Select(value => value.Task))); release.TrySetResult(true); await Within(sent.Task);
            model.Events.Emit(new BidiToolUseBlocksEvent([new("call", "lookup", JsonSerializer.SerializeToElement(new { }))])); model.Events.Close();
            await run;
            Assert.Equal(1, effects); Assert.Single(state); Assert.Equal(2, agent.Messages.Count(message => message.Text.StartsWith("input", StringComparison.Ordinal)));
            Assert.Contains(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Content == "answer");
        }
        finally { release.TrySetResult(true); model.Events.Close(); await agent.StopAsync(); }
        BidiInputStream Input(int index)
        {
            var reads = 0;
            return new(async token =>
            {
                if (Interlocked.Increment(ref reads) == 1) { entered[index].TrySetResult(true); await release.Task; return new BidiMessage([new TextBlock("input" + index)]); }
                await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable");
            });
        }
    }

    [Fact]
    public async Task OutputsFanOutConcurrentlyButTheNextEventWaitsForEverySink()
    {
        var slowEntered = Signal(); var release = Signal(); var fastEntered = Signal(); var seen = new List<string>();
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var slow = new BidiOutputStream(async (output, _) => { if (output is BidiResponseStopEvent { ResponseId: "one" }) { slowEntered.TrySetResult(true); await release.Task; } });
        var fast = new BidiOutputStream((output, _) => { seen.Add(((BidiResponseStopEvent)output).ResponseId); fastEntered.TrySetResult(true); return default; });
        var run = agent.RunAsync([], [slow, fast]); model.Events.Emit(new BidiResponseStopEvent("one"));
        try
        {
            await Within(slowEntered.Task); await Within(fastEntered.Task); model.Events.Emit(new BidiResponseStopEvent("two")); model.Events.Close();
            Assert.Equal(new[] { "one" }, seen); release.TrySetResult(true); await run; Assert.Equal(new[] { "one", "two" }, seen);
        }
        finally { release.TrySetResult(true); model.Events.Close(); await agent.StopAsync(); }
    }

    [Fact]
    public async Task OutputFailureCancelsInputButStillJoinsItsPhysicalExitAndRetainsCleanup()
    {
        var release = Signal(); var canceled = Signal(); var stopped = Signal(); var failure = new IOException("output"); var cleanup = new IOException("input stop");
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new BidiInputStream(async token => { using var registration = token.Register(() => canceled.TrySetResult(true)); await release.Task; token.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); },
            stop: _ => { stopped.TrySetResult(true); throw cleanup; });
        var output = new BidiOutputStream((_, _) => throw failure);
        var run = agent.RunAsync([input], [output]); model.Events.Emit(new BidiResponseStopEvent("one"));
        try { await Within(canceled.Task); await Within(stopped.Task); Assert.False(run.IsCompleted); }
        finally { release.TrySetResult(true); }
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => run));
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]))); Assert.Equal(1, model.Stops);
    }

    [Fact]
    public async Task CallerCancellationRetainsTheExactTokenAfterAllStreamsStop()
    {
        var entered = Signal(); var stopped = 0; using var cancellation = new CancellationTokenSource();
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var input = new BidiInputStream(async token => { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); },
            stop: token => { Assert.False(token.CanBeCanceled); stopped++; return default; });
        var run = agent.RunAsync([input], [], cancellationToken: cancellation.Token); await Within(entered.Task); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(1, stopped); Assert.Equal(1, model.Stops);
    }

    [Fact]
    public async Task StreamCallbacksCannotAwaitTheirOwnLifecycleOrOutput()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel { EndAfterEvents = true };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); var checkedCallbacks = 0;
        var output = new BidiOutputStream(async (_, _) => await CheckAsync(), async (_, _) => await CheckAsync(), async _ => await CheckAsync());
        await agent.RunAsync([], [output]); Assert.Equal(3, checkedCallbacks);
        async Task CheckAsync()
        {
            checkedCallbacks++;
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StopAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StartAsync());
            Assert.Throws<InvalidOperationException>(() => { agent.DisposeAsync(); });
            await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await receiver.MoveNextAsync());
        }
    }

    [Fact]
    public async Task RunReservesItsReceiverBeforeStartingBorrowedIo()
    {
        var entered = Signal(); var release = Signal();
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RunModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var output = new BidiOutputStream((_, _) => default, async (_, _) => { entered.TrySetResult(true); await release.Task; });
        var run = agent.RunAsync([], [output]); await Within(entered.Task);
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator();
        try { await Assert.ThrowsAsync<InvalidOperationException>(async () => await receiver.MoveNextAsync()); }
        finally { release.TrySetResult(true); model.Events.Close(); }
        await run;
    }

    internal sealed class RunModel : IBidiModel
    {
        internal Generation Events { get; } = new(); internal Func<IBidiInput, Task>? OnSend; internal int Starts; internal int Stops;
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default) { Starts++; return Task.CompletedTask; }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return OnSend?.Invoke(input) ?? Task.CompletedTask; }
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true) { var value = await Events.ReadAsync(cancellationToken); if (value is null) yield break; if (value is Exception error) throw error; yield return (IBidiOutputEvent)value; }
        }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stops++; Events.Close(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Events.Close(); return default; }
    }
    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
