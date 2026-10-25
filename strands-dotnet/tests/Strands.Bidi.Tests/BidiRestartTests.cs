using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiRestartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReactiveRestartUsesTheCurrentAutomaticRestartPolicy(bool enabled)
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { AutoRestart = !enabled } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        model.Settings = new() { AutoRestart = enabled }; var timeout = new BidiConnectionTimeoutException("deadline"); model.Current.Emit(timeout);
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator();
        if (enabled)
        {
            Assert.True(await Within(NextAsync())); Assert.IsType<BidiConnectionRestartEvent>(receiver.Current); await Within(model.Reconnected.Task); Assert.Equal(1, model.Restarts);
        }
        else { Assert.Same(timeout, await Assert.ThrowsAsync<BidiConnectionTimeoutException>(NextAsync)); Assert.Equal(0, model.Restarts); }
        await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task ATimeoutRestartsWithDetachedHistoryAndTheOriginalCallerState()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel(), SystemPrompt = "instructions" });
        await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var state = new Dictionary<string, object?> { ["caller"] = "original" };
        await agent.StartAsync(state);
        await agent.SendAsync(new BidiMessage([new TextBlock("remember this")]));
        var timeout = new BidiConnectionTimeoutException("deadline");
        model.Current.Emit(timeout);
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await Within(NextAsync()));
        var restart = Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        Assert.Equal(BidiRestartReason.Timeout, restart.Reason); Assert.Same(timeout, restart.TimeoutError); Assert.True(restart.TurnInterrupted);
        await Within(model.Reconnected.Task);
        Assert.Equal("remember this", Assert.Single(model.Sessions[1].Messages).Text); Assert.Equal("instructions", model.Sessions[1].SystemPrompt);
        Assert.Equal(1, model.Restarts); Assert.Single(state); Assert.Equal("original", state["caller"]);
        await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task AnExplicitOptOutSurfacesTheOriginalTimeoutWithoutRestarting()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { Settings = new() { AutoRestart = false } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        var timeout = new BidiConnectionTimeoutException("deadline"); model.Current.Emit(timeout);
        var actual = await Assert.ThrowsAsync<BidiConnectionTimeoutException>(async () => { await foreach (var item in agent.ReceiveAsync()) { } });
        Assert.Same(timeout, actual); Assert.Equal(0, model.Restarts); await agent.StopAsync();
    }

    [Fact]
    public async Task AModelWithoutTheRestartCapabilityUsesStopThenStart()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RecoveringModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        Assert.IsType<BidiConnectionRestartEvent>(receiver.Current); await Within(model.Reconnected.Task);
        Assert.Equal(2, model.Starts); Assert.Equal(1, model.Stops);
        await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    [Fact]
    public async Task AFailedSwapPublishesItsRestartEventThenPreservesTheOriginalFailure()
    {
        var failure = new IOException("reconnect failed");
        await using var host = new Agent(new() { Model = new NoCallsModel() });
        await using var model = new RestartModel { RestartError = failure };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        model.Current.Emit(new BidiConnectionTimeoutException("deadline"));
        await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator(); Assert.True(await Within(NextAsync()));
        Assert.IsType<BidiConnectionRestartEvent>(receiver.Current);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(NextAsync)); await agent.StopAsync();
        async Task<bool> NextAsync() => await receiver.MoveNextAsync();
    }

    internal class RecoveringModel : IBidiModel
    {
        private readonly object _gate = new();
        private Generation? _current;
        internal Generation Current { get { lock (_gate) return _current!; } }
        internal List<BidiSessionOptions> Sessions { get; } = [];
        internal List<IBidiInput> Inputs { get; } = [];
        internal TaskCompletionSource<BidiMessage> Results { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Starts; internal int Stops;
        internal BidiConnectionOptions Settings { get; set; } = new();
        public BidiConnectionOptions Connection => Settings;
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) { _current = new(); Sessions.Add(session!); Starts++; if (Starts > 1) Reconnected.TrySetResult(true); }
            return Task.CompletedTask;
        }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); lock (Inputs) Inputs.Add(input); if (input is BidiMessage message && message.Content[0] is ToolResultBlock) Results.TrySetResult(message); return Task.CompletedTask; }
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var generation = Current;
            while (true)
            {
                var item = await generation.ReadAsync(cancellationToken);
                if (item is null) yield break;
                if (item is Exception error) throw error;
                yield return (IBidiOutputEvent)item;
            }
        }
        public Task StopAsync(CancellationToken cancellationToken = default) { Stops++; Current.Close(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { lock (_gate) _current?.Close(); return default; }
    }

    internal sealed class RestartModel : RecoveringModel, IRestartableBidiModel
    {
        internal int Restarts; internal Exception? RestartError;
        internal Task? RestartGate;
        internal TaskCompletionSource<bool> RestartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            Restarts++; RestartEntered.TrySetResult(true);
            if (RestartGate is { } gate)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
                if (await Task.WhenAny(gate, cancelled.Task) != gate) cancellationToken.ThrowIfCancellationRequested();
                await gate;
            }
            Current.Close();
            if (RestartError is { } failure) throw failure;
            await StartAsync(session, cancellationToken);
        }
    }

    internal sealed class Generation
    {
        private readonly object _gate = new();
        private readonly Queue<object?> _items = new();
        private readonly SemaphoreSlim _available = new(0);
        private bool _closed;
        internal void Emit(object value) { lock (_gate) _items.Enqueue(value); _available.Release(); }
        internal void Close() { lock (_gate) { if (_closed) return; _closed = true; _items.Enqueue(null); } _available.Release(); }
        internal async Task<object?> ReadAsync(CancellationToken cancellationToken)
        { await _available.WaitAsync(cancellationToken); lock (_gate) return _items.Dequeue(); }
    }
}
