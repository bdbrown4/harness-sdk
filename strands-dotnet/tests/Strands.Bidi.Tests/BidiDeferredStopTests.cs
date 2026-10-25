using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiDeferredStopTests
{
    [Fact]
    public Task CancelWaitsForEveryAdmittedToolAndCompletedHistoryBeforeStoppingReceive() => RunCaseAsync(async rig =>
    {
        rig.HoldCompletedHistory = true;
        await rig.Agent.StartAsync(rig.State);
        var output = new ConcurrentQueue<IAgentStreamEvent>();
        var receiving = rig.Own(ReceiveUntilStopAsync(rig, output));
        rig.Model.EmitGroup();
        await WaitAsync(Task.WhenAll(rig.ToolEntered.Select(signal => signal.Task)));

        var requested = rig.Agent.CancelSignal;
        RequestRepeatedly(rig.Agent);
        Assert.All(rig.ToolTokens, token => Assert.False(token.IsCancellationRequested));
        Assert.False(rig.Model.ReceiveToken.IsCancellationRequested);
        Assert.False(rig.Host.CancelSignal.IsCancellationRequested);
        Assert.Equal(new[] { 1, 1 }, rig.ToolCalls);
        Assert.Empty(rig.Model.ResultBatches);
        rig.ReleaseTools();

        await WaitAsync(rig.CompletedHistoryEntered.Task);
        Assert.Equal(new[] { 1, 1 }, rig.ToolExits);
        Assert.Equal(2, rig.ToolResultCallbacks);
        Assert.False(rig.CompletedHistoryFinished.Task.IsCompleted);
        Assert.Empty(output.OfType<BidiConnectionStopEvent>());
        Assert.Empty(rig.Model.ResultBatches);
        Assert.False(receiving.IsCompleted);
        Assert.All(rig.ToolTokens, token => Assert.False(token.IsCancellationRequested));

        rig.CompletedHistoryRelease.TrySetResult(true);
        await WaitAsync(receiving);

        var stop = Assert.Single(output.OfType<BidiConnectionStopEvent>());
        Assert.Equal("user_request", stop.Reason);
        Assert.Equal("connection-1", stop.ConnectionId);
        Assert.Same(stop, output.Last());
        Assert.Equal(2, output.OfType<ToolResultEvent>().Count());
        Assert.True(rig.CompletedHistoryFinished.Task.IsCompleted);
        Assert.True(requested.IsCancellationRequested);
        Assert.True(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.All(rig.ToolTokens, token => Assert.False(token.IsCancellationRequested));
        Assert.False(rig.Model.ReceiveToken.IsCancellationRequested);
        Assert.Equal(0, rig.Model.Stops);
        Assert.Empty(rig.Model.ResultBatches);
        AssertHistory(rig);
    });

    [Fact]
    public Task RunAwaitsEveryDeferredStopSinkAndPhysicalInputExitBeforeItsStopHooks() => RunCaseAsync(async rig =>
    {
        var inputEntered = Signal();
        var inputCancelled = Signal();
        var inputStopping = Signal();
        var inputRelease = Signal();
        var inputExited = false;
        var stopHook = false;
        var sinkEntered = new[] { Signal(), Signal() };
        var sinkRelease = new[] { Signal(), Signal() };
        var sinkExited = new[] { Signal(), Signal() };
        var forbiddenResult = Signal();
        CancellationToken inputToken = default;
        rig.ReleaseOnCleanup(() => inputRelease.TrySetResult(true));
        foreach (var signal in sinkRelease) rig.ReleaseOnCleanup(() => signal.TrySetResult(true));
        var input = new BidiInputStream(async token =>
        {
            inputToken = token;
            using var registration = token.Register(() => inputCancelled.TrySetResult(true));
            inputEntered.TrySetResult(true);
            try
            {
                await inputRelease.Task;
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The fixture input must exit through cancellation.");
            }
            finally { inputExited = true; }
        }, stop: _ => { inputStopping.TrySetResult(true); return default; });
        rig.Agent.Hooks.Register<BidiAgentStopEvent>((_, _) =>
        {
            Assert.True(inputExited);
            Assert.All(sinkExited, signal => Assert.True(signal.Task.IsCompleted));
            stopHook = true;
            return default;
        });
        var outputs = Enumerable.Range(0, 2).Select(index => (IBidiOutputStream)new BidiOutputStream(async (value, _) =>
        {
            if (IsProviderResultDiagnostic(value)) forbiddenResult.TrySetResult(true);
            AssertNoForbiddenResultSend(value);
            if (value is not BidiConnectionStopEvent stop) return;
            Assert.Equal("user_request", stop.Reason);
            Assert.Equal("connection-1", stop.ConnectionId);
            sinkEntered[index].TrySetResult(true);
            await sinkRelease[index].Task;
            sinkExited[index].TrySetResult(true);
        })).ToArray();
        var run = rig.Own(rig.Agent.RunAsync([input], outputs, rig.State));
        await WaitAsync(inputEntered.Task);
        rig.Model.EmitGroup();
        await WaitAsync(Task.WhenAll(rig.ToolEntered.Select(signal => signal.Task)));
        RequestRepeatedly(rig.Agent);
        rig.ReleaseTools();

        var allSinks = Task.WhenAll(sinkEntered.Select(signal => signal.Task));
        var winner = await Within(Task.WhenAny(allSinks, forbiddenResult.Task, run));
        if (ReferenceEquals(winner, forbiddenResult.Task))
            Assert.Fail("The provider received a completed result batch after a deferred stop request.");
        if (ReferenceEquals(winner, run)) await run;
        Assert.True(allSinks.IsCompleted);
        Assert.False(inputToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        Assert.False(stopHook);
        Assert.Empty(rig.Model.ResultBatches);

        sinkRelease[0].TrySetResult(true);
        await WaitAsync(sinkExited[0].Task);
        Assert.False(inputToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        sinkRelease[1].TrySetResult(true);
        await WaitAsync(inputStopping.Task);
        await WaitAsync(inputCancelled.Task);
        Assert.False(inputExited);
        Assert.False(run.IsCompleted);
        Assert.False(stopHook);
        inputRelease.TrySetResult(true);
        await WaitAsync(run);

        Assert.True(inputExited);
        Assert.True(stopHook);
        Assert.Equal(1, rig.Model.Stops);
        Assert.Equal(0, rig.Model.ActiveReaders);
        Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.Equal(new[] { 1, 1 }, rig.ToolCalls);
        Assert.Equal(new[] { 1, 1 }, rig.ToolExits);
        AssertHistory(rig);
    });

    [Fact]
    public Task ActualStopClearsTheRequestBeforeALaterConversationSendsItsToolResults() => RunCaseAsync(async rig =>
    {
        await rig.Agent.StartAsync(rig.State);
        var output = new ConcurrentQueue<IAgentStreamEvent>();
        var receiving = rig.Own(ReceiveUntilStopAsync(rig, output));
        rig.Model.EmitGroup();
        await WaitAsync(Task.WhenAll(rig.ToolEntered.Select(signal => signal.Task)));
        RequestRepeatedly(rig.Agent);
        rig.ReleaseTools();
        await WaitAsync(receiving);
        Assert.Single(output.OfType<BidiConnectionStopEvent>());
        Assert.True(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.Empty(rig.Model.ResultBatches);

        await rig.Agent.StopAsync();
        Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.Equal(0, rig.Model.ActiveReaders);
        await rig.Agent.StartAsync(rig.State);
        var later = rig.Own(ReceiveUntilProviderResultAsync(rig));
        rig.Model.EmitGroup("-2");
        await WaitAsync(later);

        var batch = Assert.Single(rig.Model.ResultBatches);
        Assert.Equal("connection-2", batch.ConnectionId);
        Assert.Equal(new[] { "a-2", "b-2" }, batch.Message.Content.Cast<ToolResultBlock>().Select(result => result.ToolUseId));
        Assert.All(batch.Message.Content.Cast<ToolResultBlock>(), result => Assert.Equal(ToolResultStatus.Success, result.Status));
        Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.Equal(new[] { 2, 2 }, rig.ToolCalls);
        Assert.Equal(new[] { 2, 2 }, rig.ToolExits);
        Assert.Equal(4, rig.ToolResultCallbacks);
        Assert.Equal(2, rig.CompletedHistoryNotifications);
        Assert.Equal(4, rig.Agent.Messages.Count);
        Assert.Empty(rig.Agent.Messages.SelectMany(message => message.Content).OfType<TextBlock>());
        Assert.Single(rig.State);
    });

    private static void RequestRepeatedly(BidiAgent agent)
    {
        agent.Cancel("first retained reason");
        agent.Cancel();
        agent.Cancel("replacement retained reason", afterCurrentTools: true);
        agent.Cancel(string.Empty);
        agent.Cancel();
    }

    private static async Task ReceiveUntilStopAsync(Rig rig, ConcurrentQueue<IAgentStreamEvent> output)
    {
        await foreach (var value in rig.Agent.ReceiveAsync())
        {
            output.Enqueue(value);
            AssertNoForbiddenResultSend(value);
        }
        Assert.Single(output.OfType<BidiConnectionStopEvent>());
    }

    private static async Task ReceiveUntilProviderResultAsync(Rig rig)
    {
        await foreach (var value in rig.Agent.ReceiveAsync())
        {
            Assert.IsNotType<BidiConnectionStopEvent>(value);
            if (IsProviderResultDiagnostic(value)) return;
        }
        Assert.Fail("The later ordinary conversation ended without sending its completed result batch.");
    }

    private static void AssertNoForbiddenResultSend(IAgentStreamEvent value) =>
        Assert.False(IsProviderResultDiagnostic(value), "The provider received a completed result batch after a deferred stop request.");

    private static bool IsProviderResultDiagnostic(IAgentStreamEvent value) => value is BidiNativeEvent native &&
        native.Value.TryGetProperty("fixture", out var name) && name.GetString() == "provider_result_batch";

    private static void AssertHistory(Rig rig)
    {
        var history = rig.Agent.Messages;
        Assert.Equal(2, history.Count);
        Assert.Equal(new[] { "a", "b" }, history[0].Content.Cast<ToolUseBlock>().Select(call => call.ToolUseId));
        var results = history[1].Content.Cast<ToolResultBlock>().ToArray();
        Assert.Equal(new[] { "a", "b" }, results.Select(result => result.ToolUseId));
        Assert.All(results, result => Assert.Equal(ToolResultStatus.Success, result.Status));
        Assert.Equal(new[] { "completed-a", "completed-b" }, results.Select(result => result.Content));
        Assert.Empty(history.SelectMany(message => message.Content).OfType<TextBlock>());
        Assert.Single(rig.State);
    }

    private static async Task RunCaseAsync(Func<Rig, Task> body)
    {
        var rig = new Rig();
        Exception? primary = null;
        try { await body(rig); }
        catch (Exception error) { primary = error; throw; }
        finally { await rig.CleanupAsync(primary); }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task WaitAsync(Task task)
    {
        await Within(ObserveAsync(task));
        static async Task<bool> ObserveAsync(Task pending) { await pending; return true; }
    }

    private sealed class Rig
    {
        private readonly List<Task> _owned = [];
        private readonly List<Action> _release = [];
        internal Rig()
        {
            var tools = Enumerable.Range(0, 2).Select(index => (ITool)new FunctionTool(
                new ToolSpec(index == 0 ? "first" : "second", "Gated side effect", JsonSerializer.SerializeToElement(new { type = "object" })),
                async (_, context, token) =>
                {
                    Assert.Same(State, context.InvocationState);
                    ToolTokens[index] = token;
                    Interlocked.Increment(ref ToolCalls[index]);
                    ToolEntered[index].TrySetResult(true);
                    try { await ToolRelease[index].Task; return index == 0 ? "completed-a" : "completed-b"; }
                    finally { Interlocked.Increment(ref ToolExits[index]); }
                })).ToArray();
            Host = new(new() { Model = new NoCallsModel(), Tools = tools });
            Host.Hooks.Register<ToolResultEvent>((value, _) =>
            {
                Assert.Same(State, value.InvocationState);
                Interlocked.Increment(ref ToolResultCallbacks);
                return default;
            });
            Agent = new(new() { ToolAgent = Host, Model = Model });
            Agent.Hooks.Register<BidiMessageUpdatedEvent>(async (value, _) =>
            {
                if (!value.Message.Content.OfType<ToolResultBlock>().Any()) return;
                Assert.All(value.Message.Content.Cast<ToolResultBlock>(), result => Assert.Equal(ToolResultStatus.Success, result.Status));
                Interlocked.Increment(ref CompletedHistoryNotifications);
                CompletedHistoryEntered.TrySetResult(true);
                if (HoldCompletedHistory) await CompletedHistoryRelease.Task;
                CompletedHistoryFinished.TrySetResult(true);
            });
        }
        internal Agent Host { get; }
        internal BidiAgent Agent { get; }
        internal FixtureModel Model { get; } = new();
        internal Dictionary<string, object?> State { get; } = new() { ["caller"] = new object() };
        internal TaskCompletionSource<bool>[] ToolEntered { get; } = [Signal(), Signal()];
        internal TaskCompletionSource<bool>[] ToolRelease { get; } = [Signal(), Signal()];
        internal CancellationToken[] ToolTokens { get; } = new CancellationToken[2];
        internal int[] ToolCalls { get; } = new int[2];
        internal int[] ToolExits { get; } = new int[2];
        internal TaskCompletionSource<bool> CompletedHistoryEntered { get; } = Signal();
        internal TaskCompletionSource<bool> CompletedHistoryRelease { get; } = Signal();
        internal TaskCompletionSource<bool> CompletedHistoryFinished { get; } = Signal();
        internal int ToolResultCallbacks;
        internal int CompletedHistoryNotifications;
        internal bool HoldCompletedHistory;
        internal Task Own(Task task) { _owned.Add(task); return task; }
        internal void ReleaseOnCleanup(Action action) => _release.Add(action);
        internal void ReleaseTools() { foreach (var signal in ToolRelease) signal.TrySetResult(true); }
        internal async Task CleanupAsync(Exception? primary)
        {
            ReleaseTools();
            CompletedHistoryRelease.TrySetResult(true);
            foreach (var release in _release) release();
            Exception? cleanup = null;
            try { await Agent.StopAsync(); } catch (Exception error) { Add(error); }
            foreach (var task in _owned) try { await task; } catch (Exception error) { Add(error); }
            try { Assert.Equal(0, Model.ActiveReaders); } catch (Exception error) { Add(error); }
            try { await Agent.DisposeAsync(); } catch (Exception error) { Add(error); }
            try { Assert.Equal(0, Model.Disposals); } catch (Exception error) { Add(error); }
            try { await Model.DisposeAsync(); } catch (Exception error) { Add(error); }
            try { await Host.DisposeAsync(); } catch (Exception error) { Add(error); }
            if (cleanup is null) return;
            if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
            if (primary!.Data["Strands.CleanupErrors"] is not List<Exception> errors)
                primary.Data["Strands.CleanupErrors"] = errors = [];
            errors.Add(cleanup);
            void Add(Exception error)
            {
                if (ReferenceEquals(error, primary) || ReferenceEquals(error, cleanup)) return;
                if (cleanup is null) cleanup = error;
                else
                {
                    if (cleanup.Data["Strands.CleanupErrors"] is not List<Exception> errors)
                        cleanup.Data["Strands.CleanupErrors"] = errors = [];
                    errors.Add(error);
                }
            }
        }
    }

    private sealed class FixtureModel : IBidiModel
    {
        private readonly object _gate = new();
        private readonly List<FixtureConnection> _connections = [];
        private FixtureConnection? _current;
        private readonly List<(string ConnectionId, BidiMessage Message)> _resultBatches = [];
        internal int Stops;
        internal int Disposals;
        internal int ActiveReaders;
        internal CancellationToken ReceiveToken;
        internal (string ConnectionId, BidiMessage Message)[] ResultBatches { get { lock (_gate) return _resultBatches.ToArray(); } }
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _current = new("connection-" + (_connections.Count + 1));
                _connections.Add(_current);
                _current.Emit(new BidiConnectionStartEvent(_current.Id, "fixture"));
            }
            return Task.CompletedTask;
        }
        internal void EmitGroup(string suffix = "") => Current().Emit(new BidiToolUseBlocksEvent([
            new("a" + suffix, "first", JsonSerializer.SerializeToElement(new { })),
            new("b" + suffix, "second", JsonSerializer.SerializeToElement(new { }))]));
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is BidiMessage message && message.Content.OfType<ToolResultBlock>().Any())
            {
                var connection = Current();
                lock (_gate) _resultBatches.Add((connection.Id, message));
                connection.Emit(new BidiNativeEvent(JsonSerializer.SerializeToElement(new { fixture = "provider_result_batch" })));
            }
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var connection = Current();
            ReceiveToken = cancellationToken;
            Interlocked.Increment(ref ActiveReaders);
            try
            {
                while (await connection.ReadAsync(cancellationToken) is { } value) yield return value;
            }
            finally { Interlocked.Decrement(ref ActiveReaders); }
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Stops);
            Current().Close();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            foreach (var connection in _connections) connection.Dispose();
            return default;
        }
        private FixtureConnection Current() { lock (_gate) return _current ?? throw new InvalidOperationException("The fixture is not started."); }
        private sealed class FixtureConnection(string id) : IDisposable
        {
            private readonly ConcurrentQueue<IBidiOutputEvent> _events = new();
            private readonly SemaphoreSlim _available = new(0);
            private volatile bool _closed;
            internal string Id { get; } = id;
            internal void Emit(IBidiOutputEvent value) { _events.Enqueue(value); _available.Release(); }
            internal async Task<IBidiOutputEvent?> ReadAsync(CancellationToken token)
            {
                while (true)
                {
                    await _available.WaitAsync(token);
                    if (_events.TryDequeue(out var value)) return value;
                    if (_closed) return null;
                }
            }
            internal void Close() { _closed = true; _available.Release(); }
            public void Dispose() => _available.Dispose();
        }
    }
}
