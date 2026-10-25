using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Telemetry;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

[Collection("Bidi telemetry")]
public sealed class BidiTelemetryLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledStartBalancesOnlyItsOwnSpans(bool cancel)
    {
        using var capture = new Capture();
        using var caller = new Activity("bidi start lifetime caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("connection did not open");
        await using var chat = new NoChatModel();
        await using var host = new Agent(new() { Model = chat });
        await using var model = new LifetimeModel { HoldStart = true, StartError = cancel ? null : failure };
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        Task<Exception?>? starting = null;
        try
        {
            starting = OutcomeAsync(agent.StartAsync(cancellationToken: cancellation.Token));
            var admitted = await Within(model.StartEntered.Task);
            Assert.True(admitted.CanBeCanceled);
            Assert.False(starting.IsCompleted);
            Assert.Empty(capture.Ended(caller.TraceId));
            if (cancel) cancellation.Cancel();
            model.StartRelease.TrySetResult(true);
            var error = await Within(starting);
            if (cancel) Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
            else Assert.Same(failure, error);
            Assert.Same(caller, Activity.Current);
            Assert.Equal(0, model.Stops);
            model.AssertJoined(0);

            var spans = capture.AssertBalanced(caller.TraceId, 2);
            var session = Assert.Single(spans, value => value.DisplayName.StartsWith("bidi_session ", StringComparison.Ordinal));
            var connect = Assert.Single(spans, value => value.DisplayName == "bidi_connect");
            Assert.Equal(caller.SpanId, session.ParentSpanId);
            Assert.Equal(session.SpanId, connect.ParentSpanId);
            Assert.All(spans, span => AssertError(span, cancel ? null : failure));
        }
        finally { await FinishAsync(agent, model, starting); }
        Assert.Same(caller, Activity.Current);
        Assert.Equal(0, model.Disposals);
        await host.InitializeAsync();
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, chat.Disposals);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("eof")]
    [InlineData("cancel")]
    public async Task OpenResponseEndsAfterPhysicalReaderAndHistoryCleanup(string terminal)
    {
        using var capture = new Capture();
        using var caller = new Activity("bidi response lifetime caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var cancellation = new CancellationTokenSource();
        var historyEntered = Signal();
        var historyRelease = Signal();
        var failure = new IOException("active response disconnected");
        var observed = new BidiRestartTests.Generation();
        var observationTasks = new List<Task>();
        var outputs = new ConcurrentQueue<IAgentStreamEvent>();
        await using var chat = new NoChatModel();
        await using var host = new Agent(new() { Model = chat });
        await using var model = new LifetimeModel { HoldFirstReaderExit = true };
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var completions = 0;
        using var completed = agent.Hooks.Register<BidiResponseStopHookEvent>((_, _) => { Interlocked.Increment(ref completions); return default; });
        using var history = agent.Hooks.Register<BidiMessageUpdatedEvent>(async (value, token) =>
        {
            Assert.Equal("unfinished", value.Message.Text);
            Assert.False(token.CanBeCanceled);
            historyEntered.TrySetResult(true);
            await historyRelease.Task;
        });
        Task<Exception?>? draining = null;
        Task<Exception?>? stopping = null;
        try
        {
            await agent.StartAsync();
            draining = DrainAsync(agent, observed, outputs);
            await ReadUntilAsync<BidiConnectionStartEvent>(observed, observationTasks);
            var connection = model.Current;
            connection.Events.Emit(new BidiResponseStartEvent("open"));
            await ReadUntilAsync<BidiResponseStartEvent>(observed, observationTasks);
            connection.Events.Emit(new BidiTextStartEvent("partial"));
            connection.Events.Emit(new BidiTextDeltaEvent("unfinished", "partial"));
            await ReadUntilAsync<BidiTextDeltaEvent>(observed, observationTasks);
            var response = Assert.Single(capture.Started(caller.TraceId), value => value.DisplayName == "bidi_response");
            var session = Assert.Single(capture.Started(caller.TraceId), value => value.DisplayName.StartsWith("bidi_session ", StringComparison.Ordinal));
            Assert.Equal(session.SpanId, response.ParentSpanId);

            if (terminal == "failure") connection.Events.Emit(failure);
            else if (terminal == "eof") connection.Events.Close();
            else
            {
                stopping = OutcomeAsync(agent.StopAsync(cancellation.Token));
                await Within(model.StopEntered.Task);
                cancellation.Cancel();
            }
            await Within(connection.ExitEntered.Task);
            Assert.DoesNotContain(response, capture.Ended(caller.TraceId));
            Assert.DoesNotContain(session, capture.Ended(caller.TraceId));
            Assert.False(draining.IsCompleted);
            if (stopping is not null) Assert.False(stopping.IsCompleted);
            connection.ExitRelease.TrySetResult(true);
            await Within(historyEntered.Task);
            Assert.True(connection.Exited.Task.IsCompleted);
            Assert.DoesNotContain(response, capture.Ended(caller.TraceId));
            Assert.DoesNotContain(session, capture.Ended(caller.TraceId));
            Assert.False(draining.IsCompleted);
            historyRelease.TrySetResult(true);

            var receiveError = await Within(draining);
            if (terminal == "failure") Assert.Same(failure, receiveError);
            else Assert.Null(receiveError);
            Assert.Single(capture.Ended(caller.TraceId), value => value.SpanId == response.SpanId);
            AssertError(response, terminal == "failure" ? failure : null);
            Assert.Null(response.GetTagItem("gen_ai.server.time_to_first_audio"));
            if (stopping is null)
            {
                Assert.DoesNotContain(session, capture.Ended(caller.TraceId));
                stopping = OutcomeAsync(agent.StopAsync());
                Assert.Null(await Within(stopping));
            }
            else Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(await Within(stopping)).CancellationToken);
            AssertError(session, null);
            capture.AssertBalanced(caller.TraceId, 3);
            model.AssertJoined(1);
            Assert.Equal(0, completions);
            Assert.DoesNotContain(outputs, value => value is BidiResponseStopEvent);
            var message = Assert.Single(agent.Messages);
            Assert.Equal("unfinished", message.Text);
            Assert.Equal("incomplete", message.Metadata!.Value.GetProperty("custom").GetProperty("bidi").GetProperty("status").GetString());
            Assert.Same(caller, Activity.Current);
        }
        finally
        {
            historyRelease.TrySetResult(true);
            observed.Close();
            try { await FinishAsync(agent, model, stopping, draining); }
            finally { await Task.WhenAll(observationTasks); }
        }
        Assert.Same(caller, Activity.Current);
        Assert.Equal(0, model.Disposals);
        await host.InitializeAsync();
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, chat.Disposals);
    }

    [Fact]
    public async Task SupersededReaderCannotRecordLateResponseAudioOrUsageOnTheFreshGeneration()
    {
        using var capture = new Capture();
        using var caller = new Activity("bidi generation telemetry caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var ticks = new BidiRestartTests.Generation();
        var observed = new BidiRestartTests.Generation();
        var observationTasks = new List<Task>();
        var outputs = new ConcurrentQueue<IAgentStreamEvent>();
        await using var chat = new NoChatModel();
        await using var host = new Agent(new() { Model = chat });
        await using var model = new LifetimeModel { HoldRestart = true, HoldFirstReaderExit = true, Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, DelayAsync);
        Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (duration == TimeSpan.Zero) return Task.CompletedTask;
            var request = new BidiScheduleTests.Delay(duration, token, false);
            ticks.Emit(request);
            return request.WaitAsync();
        }
        Task<Exception?>? draining = null;
        try
        {
            await agent.StartAsync();
            draining = DrainAsync(agent, observed, outputs);
            await ReadUntilAsync<BidiConnectionStartEvent>(observed, observationTasks);
            var old = model.Current;
            old.Events.Emit(new BidiResponseStartEvent("old"));
            await ReadUntilAsync<BidiResponseStartEvent>(observed, observationTasks);
            old.Events.Emit(UsageIncrement(1, 2, 10, 3));
            await ReadUntilAsync<BidiUsageEvent>(observed, observationTasks);
            var oldResponse = Assert.Single(capture.Started(caller.TraceId), value => value.DisplayName == "bidi_response");
            (await ReadUntilAsync<BidiScheduleTests.Delay>(ticks, observationTasks)).Complete();
            await ReadUntilAsync<BidiConnectionWarningEvent>(observed, observationTasks);
            (await ReadUntilAsync<BidiScheduleTests.Delay>(ticks, observationTasks)).Complete();
            var turn = await ReadUntilAsync<BidiScheduleTests.Delay>(ticks, observationTasks);
            Assert.Equal(TimeSpan.FromSeconds(10), turn.Duration);
            turn.Complete();
            await ReadUntilAsync<BidiConnectionRestartEvent>(observed, observationTasks);
            await Within(model.RestartEntered.Task);

            old.Events.Emit(new BidiResponseStartEvent("stale"));
            old.Events.Emit(new BidiAudioDeltaEvent("AQID", "late", 24000, 1, "pcm"));
            old.Events.Emit(UsageIncrement(1000, 2000, 9000, 3000));
            old.Events.Close();
            model.RestartRelease.TrySetResult(true);
            await Within(old.ExitEntered.Task);
            Assert.DoesNotContain(oldResponse, capture.Ended(caller.TraceId));
            old.ExitRelease.TrySetResult(true);
            var fresh = await ReadUntilAsync<BidiConnectionStartEvent>(observed, observationTasks);
            Assert.Equal("connection-2", fresh.ConnectionId);
            Assert.True(old.Exited.Task.IsCompleted);
            model.Current.Events.Emit(new BidiResponseStartEvent("fresh"));
            await ReadUntilAsync<BidiResponseStartEvent>(observed, observationTasks);
            model.Current.Events.Emit(UsageIncrement(4, 5, 90, 6));
            await ReadUntilAsync<BidiUsageEvent>(observed, observationTasks);
            model.Current.Events.Emit(new BidiResponseStopEvent("fresh"));
            await ReadUntilAsync<BidiResponseStopEvent>(observed, observationTasks);
            await agent.StopAsync();
            Assert.Null(await Within(draining));
            model.AssertJoined(2);
            Assert.Equal(1, model.Restarts);
            var spans = capture.AssertBalanced(caller.TraceId, 5);
            var session = Assert.Single(spans, value => value.DisplayName.StartsWith("bidi_session ", StringComparison.Ordinal));
            var responses = spans.Where(value => value.DisplayName == "bidi_response").ToArray();
            Assert.Equal(new[] { "fresh", "old" }, responses.Select(value => (string)value.GetTagItem("gen_ai.response.id")!).OrderBy(value => value, StringComparer.Ordinal));
            Assert.All(responses, value =>
            {
                Assert.Equal(session.SpanId, value.ParentSpanId);
                Assert.Null(value.GetTagItem("gen_ai.server.time_to_first_audio"));
            });
            Assert.Equal(5L, session.GetTagItem("gen_ai.usage.input_tokens"));
            Assert.Equal(7L, session.GetTagItem("gen_ai.usage.output_tokens"));
            Assert.Equal(100L, session.GetTagItem("gen_ai.usage.total_tokens"));
            Assert.Equal(9L, session.GetTagItem("gen_ai.usage.cache_read.input_tokens"));
            Assert.DoesNotContain(outputs, value => value is BidiResponseStartEvent { ResponseId: "stale" } or BidiAudioDeltaEvent);
            Assert.All(spans, value => AssertError(value, null));
            Assert.Empty(agent.Messages);
            Assert.Same(caller, Activity.Current);
        }
        finally
        {
            observed.Close(); ticks.Close();
            try { await FinishAsync(agent, model, draining); }
            finally { await Task.WhenAll(observationTasks); }
        }
        Assert.Same(caller, Activity.Current);
        Assert.Equal(0, model.Disposals);
        await host.InitializeAsync();
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, chat.Disposals);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static BidiUsageEvent UsageIncrement(long input, long output, long total, long cache)
        => new(new Usage(input, output, ReportedTotalTokens: total), new Dictionary<string, long> { ["cache_read"] = cache });

    private static async Task<Exception?> OutcomeAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> DrainAsync(BidiAgent agent, BidiRestartTests.Generation observed, ConcurrentQueue<IAgentStreamEvent> outputs)
    {
        try
        {
            await foreach (var value in agent.ReceiveAsync()) { outputs.Enqueue(value); observed.Emit(value); }
            return null;
        }
        catch (Exception error) { return error; }
        finally { observed.Close(); }
    }

    private static async Task<T> ReadUntilAsync<T>(BidiRestartTests.Generation observed, List<Task> owned)
    {
        for (var count = 0; count < 32; count++)
        {
            var reading = observed.ReadAsync(default);
            owned.Add(reading);
            var value = await Within(reading);
            Assert.NotNull(value);
            if (value is T match) return match;
        }
        throw new InvalidOperationException("The finite telemetry script did not emit " + typeof(T).Name + ".");
    }

    private static async Task FinishAsync(BidiAgent agent, LifetimeModel model, params Task?[] owned)
    {
        model.ReleaseAll();
        var cleanup = OutcomeAsync(agent.StopAsync());
        foreach (var task in owned) if (task is not null) await task.ConfigureAwait(false);
        Assert.Null(await cleanup.ConfigureAwait(false));
        await agent.DisposeAsync();
        Assert.Equal(0, model.ActiveReaders);
    }

    private static void AssertError(Activity span, Exception? error)
    {
        Assert.Equal(error is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error, span.Status);
        var exceptions = span.Events.Where(value => value.Name == "exception").ToArray();
        if (error is null) { Assert.Empty(exceptions); Assert.Null(span.GetTagItem("error.type")); }
        else
        {
            Assert.Equal(error.Message, span.StatusDescription);
            Assert.Equal(error.GetType().FullName, span.GetTagItem("error.type"));
            var recorded = Assert.Single(exceptions).Tags.ToDictionary(value => value.Key, value => value.Value);
            Assert.Equal(error.GetType().FullName, recorded["exception.type"]);
            Assert.Equal(error.Message, recorded["exception.message"]);
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _started = new();
        private readonly ConcurrentQueue<Activity> _ended = new();
        private readonly ActivityListener _listener;
        internal Capture()
        {
            _listener = new()
            {
                ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = _started.Enqueue,
                ActivityStopped = _ended.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }
        internal Activity[] Started(ActivityTraceId trace) => _started.Where(value => value.TraceId == trace).ToArray();
        internal Activity[] Ended(ActivityTraceId trace) => _ended.Where(value => value.TraceId == trace).ToArray();
        internal Activity[] AssertBalanced(ActivityTraceId trace, int count)
        {
            var started = Started(trace); var ended = Ended(trace);
            Assert.Equal(count, started.Length); Assert.Equal(count, ended.Length);
            Assert.Equal(count, started.Select(value => value.SpanId).Distinct().Count());
            Assert.Equal(started.Select(value => value.SpanId.ToString()).OrderBy(value => value, StringComparer.Ordinal), ended.Select(value => value.SpanId.ToString()).OrderBy(value => value, StringComparer.Ordinal));
            return ended;
        }
        public void Dispose() => _listener.Dispose();
    }

    private sealed class NoChatModel : IModel, IAsyncDisposable
    {
        internal int Calls;
        internal int Disposals;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            throw new InvalidOperationException("The real tool host must not invoke its ordinary chat model.");
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return default; }
    }

    private sealed class LifetimeModel : IRestartableBidiModel
    {
        private readonly object _gate = new();
        private readonly List<ReaderEpoch> _epochs = [];
        private int _activeReaders;
        private int _finishing;
        internal bool HoldStart { get; init; }
        internal bool HoldRestart { get; init; }
        internal bool HoldFirstReaderExit { get; init; }
        internal Exception? StartError { get; init; }
        internal BidiConnectionOptions Settings { get; init; } = new() { AutoRestart = false };
        internal TaskCompletionSource<CancellationToken> StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> StartRelease { get; } = Signal();
        internal TaskCompletionSource<bool> RestartEntered { get; } = Signal();
        internal TaskCompletionSource<bool> RestartRelease { get; } = Signal();
        internal TaskCompletionSource<bool> StopEntered { get; } = Signal();
        internal ReaderEpoch Current { get; private set; } = null!;
        internal int ActiveReaders => Volatile.Read(ref _activeReaders);
        internal int Stops;
        internal int Restarts;
        internal int Disposals;
        public BidiConnectionOptions Connection => Settings;
        public async Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            StartEntered.TrySetResult(cancellationToken);
            if (HoldStart) await StartRelease.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (StartError is { } error) throw error;
            Open();
        }
        private void Open()
        {
            lock (_gate)
            {
                Current = new(HoldFirstReaderExit && _epochs.Count == 0);
                _epochs.Add(Current);
                Current.Events.Emit(new BidiConnectionStartEvent("connection-" + _epochs.Count, "scripted-lifetime"));
                if (Volatile.Read(ref _finishing) != 0) { Current.ExitRelease.TrySetResult(true); Current.Events.Close(); }
            }
        }
        public async Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Restarts);
            RestartEntered.TrySetResult(true);
            if (HoldRestart) await RestartRelease.Task;
            cancellationToken.ThrowIfCancellationRequested();
            Open();
        }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("These telemetry lifetime cases do not send provider input.");
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var epoch = Current;
            Interlocked.Increment(ref _activeReaders);
            try
            {
                while (await epoch.Events.ReadAsync(cancellationToken) is { } value)
                {
                    if (value is Exception error) throw error;
                    yield return (IBidiOutputEvent)value;
                }
            }
            finally
            {
                epoch.ExitEntered.TrySetResult(true);
                await epoch.ExitRelease.Task;
                Interlocked.Decrement(ref _activeReaders);
                epoch.Exited.TrySetResult(true);
            }
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            Interlocked.Increment(ref Stops);
            StopEntered.TrySetResult(true);
            Current.Events.Close();
            return Task.CompletedTask;
        }
        internal void ReleaseAll()
        {
            Interlocked.Exchange(ref _finishing, 1);
            StartRelease.TrySetResult(true); RestartRelease.TrySetResult(true);
            ReaderEpoch[] epochs;
            lock (_gate) epochs = _epochs.ToArray();
            foreach (var epoch in epochs) { epoch.ExitRelease.TrySetResult(true); epoch.Events.Close(); }
        }
        internal void AssertJoined(int expected)
        {
            ReaderEpoch[] epochs;
            lock (_gate) epochs = _epochs.ToArray();
            Assert.Equal(expected, epochs.Length);
            Assert.Equal(0, ActiveReaders);
            Assert.All(epochs, epoch => Assert.True(epoch.Exited.Task.IsCompleted));
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); ReleaseAll(); return default; }
    }

    private sealed class ReaderEpoch
    {
        internal ReaderEpoch(bool holdExit) { if (!holdExit) ExitRelease.TrySetResult(true); }
        internal BidiRestartTests.Generation Events { get; } = new();
        internal TaskCompletionSource<bool> ExitEntered { get; } = Signal();
        internal TaskCompletionSource<bool> ExitRelease { get; } = Signal();
        internal TaskCompletionSource<bool> Exited { get; } = Signal();
    }
}
