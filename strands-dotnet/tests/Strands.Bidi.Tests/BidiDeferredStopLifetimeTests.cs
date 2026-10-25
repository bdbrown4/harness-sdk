using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Strands.Agents;
using Strands.Agents.Bidi;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiDeferredStopLifetimeTests
{
    [Fact]
    public Task UnadmittedStopRetainsAnIdleRequestAndActualIdleStopClearsIt() => RunCaseAsync(async rig =>
    {
        var requested = rig.Agent.CancelSignal;
        rig.Agent.Cancel("idle request");
        Assert.True(requested.IsCancellationRequested);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Agent.StopAsync(caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(requested, rig.Agent.CancelSignal);
        Assert.True(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.Equal(0, rig.Model.Starts);
        Assert.Equal(0, rig.Model.Stops);

        await rig.Agent.StopAsync();
        var fresh = rig.Agent.CancelSignal;
        Assert.False(fresh.IsCancellationRequested);
        Assert.NotEqual(requested, fresh);
        rig.Agent.Cancel();
        Assert.True(fresh.IsCancellationRequested);
        await rig.Agent.StopAsync();
        Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.Equal(0, rig.Model.Starts);
        Assert.Equal(0, rig.Model.Stops);
        Assert.Equal(0, rig.StopHooks);
        Assert.Empty(rig.Agent.Messages);
    });

    [Fact]
    public Task TeardownFailureStillJoinsTheReaderAndClearsTheRequest() => RunCaseAsync(async rig =>
    {
        var primary = new IOException("provider stop primary");
        var secondary = new IOException("stop hook secondary");
        rig.Model.StopError = primary;
        rig.Model.HoldReaderExit = true;
        rig.StopHookError = secondary;
        await rig.Agent.StartAsync();
        var connection = rig.Model.Current;
        await Within(connection.ReaderEntered.Task);
        var requested = rig.Agent.CancelSignal;
        rig.Agent.Cancel("wait for the real reader");
        Assert.True(requested.IsCancellationRequested);
        Assert.False(connection.ReceiveToken.IsCancellationRequested);

        var stopping = rig.Own(rig.Agent.StopAsync(), primary);
        await Within(connection.StopEntered.Task);
        await Within(connection.ReaderCancelled.Task);
        Assert.False(stopping.IsCompleted);
        Assert.Equal(1, rig.Model.ActiveReaders);
        Assert.False(connection.ReaderExited.Task.IsCompleted);
        Assert.Equal(0, rig.StopHooks);
        Assert.True(rig.Agent.CancelSignal.IsCancellationRequested);
        connection.ReaderExitRelease.TrySetResult(true);

        var actual = await Assert.ThrowsAsync<IOException>(() => WaitAsync(stopping));
        Assert.Same(primary, actual);
        Assert.Contains(secondary, Assert.IsType<List<Exception>>(actual.Data["Strands.CleanupErrors"]));
        Assert.Equal(TaskStatus.RanToCompletion, connection.ReaderExited.Task.Status);
        Assert.Equal(0, rig.Model.ActiveReaders);
        Assert.Equal(1, rig.Model.Stops);
        Assert.Equal(1, rig.StopHooks);
        Assert.Equal(0, rig.Model.Disposals);
        Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.NotEqual(requested, rig.Agent.CancelSignal);
        Assert.Empty(rig.Agent.Messages);
    });

    [Fact]
    public Task ReentrantStopRetiresTheSignalOnlyAfterItsCancellationCallbackReturns() => RunCaseAsync(async rig =>
    {
        await rig.Agent.StartAsync();
        var connection = rig.Model.Current;
        await Within(connection.ReaderEntered.Task);
        var requested = rig.Agent.CancelSignal;
        var borrowedHandle = requested.WaitHandle.SafeWaitHandle;
        var callbackRelease = new ManualResetEventSlim(false);
        rig.ReleaseOnCleanup(callbackRelease.Set);
        rig.DisposeAfterJoin(callbackRelease);
        var stopPublished = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.DisposeAfterJoin(requested.Register(() =>
        {
            try
            {
                var stopping = rig.Own(rig.Agent.StopAsync());
                stopPublished.TrySetResult(stopping);
                callbackRelease.Wait();
                Assert.False(borrowedHandle.IsClosed);
                Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
            }
            catch (Exception error) { stopPublished.TrySetException(error); throw; }
        }));

        var cancelling = rig.Own(Task.Run(() => rig.Agent.Cancel("callback stop")));
        var first = await Within(Task.WhenAny(stopPublished.Task, cancelling));
        if (first == cancelling)
        {
            await cancelling;
            Assert.Fail("Cancel returned before its registered callback published the reentrant Stop task.");
        }
        var stopped = await Within(stopPublished.Task);
        await WaitAsync(stopped);
        Assert.Equal(TaskStatus.RanToCompletion, connection.ReaderExited.Task.Status);
        Assert.Equal(0, rig.Model.ActiveReaders);
        Assert.Equal(1, rig.StopHooks);
        Assert.False(cancelling.IsCompleted);
        Assert.False(borrowedHandle.IsClosed);
        Assert.True(requested.IsCancellationRequested);
        Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.NotEqual(requested, rig.Agent.CancelSignal);

        callbackRelease.Set();
        await WaitAsync(cancelling);
        Assert.True(borrowedHandle.IsClosed);
        Assert.False(rig.Agent.CancelSignal.IsCancellationRequested);
        Assert.Equal(0, rig.Model.Disposals);
        Assert.Empty(rig.Agent.Messages);
    });

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
        private readonly ConcurrentQueue<(Task Task, Exception? ExpectedFailure)> _owned = new();
        private readonly List<Action> _release = [];
        private readonly List<IDisposable> _disposeAfterJoin = [];
        internal Rig()
        {
            Host = new(new() { Model = new NoCallsModel() });
            Agent = new(new() { ToolAgent = Host, Model = Model });
            Agent.Hooks.Register<BidiAgentStopEvent>((_, _) =>
            {
                Assert.Equal(0, Model.ActiveReaders);
                Assert.Equal(TaskStatus.RanToCompletion, Model.Current.ReaderExited.Task.Status);
                Interlocked.Increment(ref StopHooks);
                if (StopHookError is { } error) throw error;
                return default;
            });
        }
        internal Agent Host { get; }
        internal BidiAgent Agent { get; }
        internal FixtureModel Model { get; } = new();
        internal int StopHooks;
        internal Exception? StopHookError;
        internal Task Own(Task task, Exception? expectedFailure = null)
        { _owned.Enqueue((task, expectedFailure)); return task; }
        internal void ReleaseOnCleanup(Action action) => _release.Add(action);
        internal void DisposeAfterJoin(IDisposable resource) => _disposeAfterJoin.Add(resource);
        internal async Task CleanupAsync(Exception? primary)
        {
            Model.ReleaseReaders();
            foreach (var release in _release) release();
            Exception? cleanup = null;
            try { await Agent.StopAsync(); } catch (Exception error) { Add(error); }
            while (_owned.TryDequeue(out var owned))
                try { await owned.Task; }
                catch (Exception error) { if (!ReferenceEquals(error, owned.ExpectedFailure)) Add(error); }
            foreach (var resource in _disposeAfterJoin)
                try { resource.Dispose(); } catch (Exception error) { Add(error); }
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
        internal int Starts;
        internal int Stops;
        internal int Disposals;
        internal int ActiveReaders;
        internal bool HoldReaderExit;
        internal Exception? StopError;
        internal FixtureConnection Current { get { lock (_gate) return _connections.Last(); } }
        public BidiConnectionOptions Connection => new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) _connections.Add(new("lifetime-" + Interlocked.Increment(ref Starts), HoldReaderExit));
            return Task.CompletedTask;
        }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("These lifetime cases must not send provider input.");
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var connection = Current;
            connection.ReceiveToken = cancellationToken;
            Interlocked.Increment(ref ActiveReaders);
            using var registration = cancellationToken.Register(() => connection.ReaderCancelled.TrySetResult(true));
            try
            {
                connection.ReaderEntered.TrySetResult(true);
                yield return new BidiConnectionStartEvent(connection.Id, "fixture");
                await connection.ReaderCancelled.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                await connection.ReaderExitRelease.Task;
                Interlocked.Decrement(ref ActiveReaders);
                connection.ReaderExited.TrySetResult(true);
            }
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Stops);
            Current.StopEntered.TrySetResult(true);
            return StopError is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
        internal void ReleaseReaders()
        {
            lock (_gate) foreach (var connection in _connections) connection.ReaderExitRelease.TrySetResult(true);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            ReleaseReaders();
            return default;
        }
        internal sealed class FixtureConnection
        {
            internal FixtureConnection(string id, bool holdReaderExit)
            { Id = id; if (!holdReaderExit) ReaderExitRelease.TrySetResult(true); }
            internal string Id { get; }
            internal CancellationToken ReceiveToken;
            internal TaskCompletionSource<bool> ReaderEntered { get; } = Signal();
            internal TaskCompletionSource<bool> ReaderCancelled { get; } = Signal();
            internal TaskCompletionSource<bool> ReaderExitRelease { get; } = Signal();
            internal TaskCompletionSource<bool> ReaderExited { get; } = Signal();
            internal TaskCompletionSource<bool> StopEntered { get; } = Signal();
        }
    }
}
