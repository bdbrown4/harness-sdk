using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiHookCleanupTests
{
    [Fact]
    public async Task NativeStopFailureRemainsPrimaryAndOwnedDisposalStillRunsAfterEveryStopCallback()
    {
        var stopError = new IOException("model stop"); var hookError = new IOException("stop hook"); var disposeError = new IOException("model disposal");
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        var model = new StopModel { StopError = stopError, DisposeError = disposeError };
        var registry = new BidiHookRegistry(); var seen = new List<string>();
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host, OwnsModel = true, Hooks = registry });
        registry.Register<BidiAgentStopEvent>((_, _) => { Assert.Equal(1, model.Stops); Assert.Equal(0, model.Disposals); seen.Add("first"); throw hookError; });
        registry.Register<BidiAgentStopEvent>((_, _) => { seen.Add("last"); return default; });
        await agent.StartAsync();

        Assert.Same(stopError, await Assert.ThrowsAsync<IOException>(async () => await agent.DisposeAsync()));

        Assert.Equal(new[] { "last", "first" }, seen); Assert.Equal(1, model.Stops); Assert.Equal(1, model.Disposals);
        Assert.Equal(new Exception[] { hookError, disposeError }, Assert.IsType<List<Exception>>(stopError.Data["Strands.CleanupErrors"]));
        Assert.Same(stopError, await Assert.ThrowsAsync<IOException>(async () => await agent.DisposeAsync())); Assert.Equal(1, model.Disposals);
    }

    [Fact]
    public async Task CallerCancellationAfterStopAdmissionCannotSkipCleanupCallbacks()
    {
        using var cancellation = new CancellationTokenSource(); var callbacks = 0;
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new StopModel { OnStop = cancellation.Cancel };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiAgentStopEvent>((_, token) => { Assert.False(token.CanBeCanceled); callbacks++; return default; });
        await agent.StartAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.StopAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Equal(1, callbacks); Assert.Equal(1, model.Stops);
        await agent.StopAsync(); Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task ACompletedResponseHookCanSendTheNextUserTurnWithoutTakingTheLifecycleGate()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        agent.Hooks.Register<BidiResponseStopHookEvent>(async (_, token) => await agent.SendAsync(new BidiMessage([new TextBlock("follow-up")]), token));
        await agent.StartAsync(); model.Current.Emit(new BidiResponseStopEvent("actual")); model.Current.Close();
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.False(await Within(NextAsync()));
        Assert.Single(model.Inputs); Assert.Equal("follow-up", Assert.Single(agent.Messages).Text); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task DisposingAnAgentDoesNotClearItsBorrowedApplicationRegistry()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var one = new RestartModel(); await using var two = new RestartModel();
        var registry = new BidiHookRegistry(); var seen = new List<BidiAgent>();
        using var registration = registry.Register<BidiAgentStopEvent>((value, _) => { seen.Add(value.Agent); return default; });
        var first = new BidiAgent(new() { Model = one, ToolAgent = host, Hooks = registry });
        await first.StartAsync(); await first.DisposeAsync();
        await using var second = new BidiAgent(new() { Model = two, ToolAgent = host, Hooks = registry }); await second.StartAsync(); await second.StopAsync();
        Assert.Equal(new[] { first, second }, seen);
    }

    private sealed class StopModel : IBidiModel
    {
        private readonly Generation _generation = new();
        internal Exception? StopError; internal Exception? DisposeError; internal Action? OnStop;
        internal int Stops; internal int Disposals;
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _generation.ReadAsync(cancellationToken); yield break;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.CanBeCanceled); Stops++; _generation.Close(); OnStop?.Invoke();
            return StopError is null ? Task.CompletedTask : Task.FromException(StopError);
        }
        public ValueTask DisposeAsync()
        {
            Disposals++; _generation.Close(); return DisposeError is null ? default : new(Task.FromException(DisposeError));
        }
    }
}
