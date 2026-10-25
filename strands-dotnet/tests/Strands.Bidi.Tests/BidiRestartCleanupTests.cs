using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.BidiScheduleTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRestartCleanupTests
{
    [Fact]
    public async Task ExpectedOldStreamCloseErrorsAreFencedWithoutAbortingTheNewConnection()
    {
        var clock = new Clock();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ManualModel(null, new IOException("old transport closed"));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        (await Within(clock.NextAsync())).Complete(); await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        (await Within(clock.NextAsync())).Complete(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        await Within(model.Reconnected.Task); model.Current.Emit(new BidiResponseStopEvent("new"));
        Assert.True(await Within(NextAsync())); Assert.Equal("new", Assert.IsType<BidiResponseStopEvent>(receiver.Current).ResponseId); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task ARealIteratorDisposalFailureRemainsObservableAfterASuccessfulTransportSwap()
    {
        var failure = new IOException("old iterator disposal"); var clock = new Clock();
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new ManualModel(failure);
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync); await agent.StartAsync();
        (await Within(clock.NextAsync())).Complete();
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionWarningEvent>(receiver.Current);
        (await Within(clock.NextAsync())).Complete(); Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        await Within(model.Reconnected.Task); model.Current.Emit(new BidiResponseStopEvent("new"));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(NextAsync)); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    private sealed class ManualModel(Exception? cleanup, Exception? closeError = null) : IRestartableBidiModel
    {
        internal Generation Current = new();
        internal TaskCompletionSource<bool> Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _starts;
        public BidiConnectionOptions Connection => new() { RestartAfter = TimeSpan.FromSeconds(60) };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default) { _starts++; return Task.CompletedTask; }
        public Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        { if (closeError is not null) Current.Emit(closeError); Current.Close(); Current = new(); _starts++; Reconnected.TrySetResult(true); return Task.CompletedTask; }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync(CancellationToken cancellationToken = default) => new Sequence(Current, _starts == 1 ? cleanup : null);
        public Task StopAsync(CancellationToken cancellationToken = default) { Current.Close(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Current.Close(); return default; }
    }
    private sealed class Sequence(Generation generation, Exception? cleanup) : IAsyncEnumerable<IBidiOutputEvent>
    {
        public IAsyncEnumerator<IBidiOutputEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Iterator(generation, cleanup, cancellationToken);
    }
    private sealed class Iterator(Generation generation, Exception? cleanup, CancellationToken token) : IAsyncEnumerator<IBidiOutputEvent>
    {
        public IBidiOutputEvent Current { get; private set; } = null!;
        public async ValueTask<bool> MoveNextAsync()
        {
            var value = await generation.ReadAsync(token); if (value is null) return false;
            if (value is Exception error) throw error; Current = (IBidiOutputEvent)value; return true;
        }
        public ValueTask DisposeAsync() => cleanup is null ? default : new(Task.FromException(cleanup));
    }
}
