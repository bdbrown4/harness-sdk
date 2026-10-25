using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Telemetry;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

[CollectionDefinition("Bidi telemetry", DisableParallelization = true)]
public sealed class BidiTelemetryCollection { }

[Collection("Bidi telemetry")]
public sealed class BidiTelemetryTests
{
    [Fact]
    public async Task SessionResponseAudioBargeInAndRealToolSpansBalanceWithoutTakingBorrowedOwnership()
    {
        using var capture = new SpanCapture();
        using var caller = new Activity("bidi telemetry caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var toolEntered = Signal();
        var toolRelease = Signal();
        var effects = 0;
        var invocationState = new Dictionary<string, object?> { ["application"] = "original" };
        var tool = new FunctionTool(new("telemetry_lookup", "Look up a value", JsonSerializer.SerializeToElement(new { type = "object" })), async (_, context, token) =>
        {
            Assert.Same(invocationState, context.InvocationState);
            Assert.False(token.IsCancellationRequested);
            Interlocked.Increment(ref effects);
            toolEntered.TrySetResult(true);
            await toolRelease.Task;
            Assert.False(token.IsCancellationRequested);
            return "real lookup result";
        });
        await using var chat = new ToolHostModel();
        await using var host = new Agent(new() { Model = chat, Name = "tool host", Tools = [tool] });
        await using var model = new TelemetryModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        try
        {
            await agent.StartAsync(invocationState);
            Assert.Same(caller, Activity.Current);
            await using var receiver = agent.ReceiveAsync().GetAsyncEnumerator();
            var observed = new List<IAgentStreamEvent>();
            await ReadUntilAsync<BidiConnectionStartEvent>(receiver, observed);
            await agent.SendAsync(new BidiMessage([new TextBlock("look up a value")]));
            Assert.Same(caller, Activity.Current);

            model.Emit(new BidiResponseStartEvent("audio-response"));
            model.Emit(new BidiTextStartEvent("text"));
            model.Emit(new BidiTextDeltaEvent("hello", "text"));
            model.Emit(new BidiTextStopEvent("text"));
            model.Emit(new BidiAudioStartEvent("audio"));
            model.Emit(new BidiAudioDeltaEvent("AQID", "audio", 24000, 1, "pcm"));
            model.Emit(new BidiAudioDeltaEvent("BAUG", "audio", 24000, 1, "pcm"));
            model.Emit(new BidiAudioStopEvent("audio"));
            model.Emit(new BidiBargeInEvent());
            model.Emit(new BidiResponseStopEvent("audio-response"));
            model.Emit(new BidiResponseStartEvent("tool-response"));
            model.Emit(new BidiToolUseBlocksEvent([new("telemetry-call", "telemetry_lookup", JsonSerializer.SerializeToElement(new { }))]));
            model.Emit(new BidiResponseStopEvent("tool-response"));
            await ReadUntilAsync<BidiToolUseBlocksEvent>(receiver, observed);
            await Within(toolEntered.Task);
            toolRelease.TrySetResult(true);
            await ReadUntilAsync<ToolResultEvent>(receiver, observed);
            if (!observed.OfType<BidiResponseStopEvent>().Any(value => value.ResponseId == "tool-response"))
                await ReadUntilAsync<BidiResponseStopEvent>(receiver, observed);
            var batch = await Within(model.Results.Task);
            Assert.Equal("real lookup result", Assert.IsType<ToolResultBlock>(Assert.Single(batch.Content)).Content);
            Assert.Equal(1, effects);
            Assert.Equal("telemetry-call", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).ToolUseId);
            Assert.Empty(host.Messages);
            Assert.Equal(0, chat.Calls);
            Assert.Same(caller, Activity.Current);

            await agent.StopAsync();
            while (await Within(receiver.MoveNextAsync().AsTask())) observed.Add(receiver.Current);
            model.AssertReadersJoined(1);
            Assert.Equal(1, model.Stops);
            await agent.DisposeAsync();
            Assert.Same(caller, Activity.Current);
            Assert.Equal(0, model.Disposals);
            await host.InitializeAsync();
            Assert.Equal(0, chat.Disposals);

            var spans = capture.Ended(caller.TraceId);
            var session = Assert.Single(spans, value => value.DisplayName.StartsWith("bidi_session ", StringComparison.Ordinal));
            var connect = Assert.Single(spans, value => value.DisplayName == "bidi_connect");
            var responses = spans.Where(value => value.DisplayName == "bidi_response").ToArray();
            var toolSpan = Assert.Single(spans, value => value.DisplayName == "execute_tool telemetry_lookup");
            Assert.Equal(caller.SpanId, session.ParentSpanId);
            Assert.Equal("bidi_session", session.GetTagItem("gen_ai.operation.name"));
            var name = Assert.IsType<string>(session.GetTagItem("gen_ai.agent.name"));
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal("bidi_session " + name, session.DisplayName);
            using (var tools = JsonDocument.Parse(Assert.IsType<string>(session.GetTagItem("gen_ai.agent.tools"))))
                Assert.Equal("telemetry_lookup", Assert.Single(tools.RootElement.EnumerateArray()).GetString());
            Assert.Equal(session.SpanId, connect.ParentSpanId);
            Assert.Equal("bidi_connect", connect.GetTagItem("gen_ai.operation.name"));
            Assert.Null(session.GetTagItem("gen_ai.request.model"));
            Assert.Null(connect.GetTagItem("gen_ai.request.model"));
            Assert.DoesNotContain(spans, value => Equals(value.GetTagItem("gen_ai.request.model"), ToolHostModel.ModelId));
            Assert.Equal(2, responses.Length);
            Assert.All(responses, value =>
            {
                Assert.Equal(session.SpanId, value.ParentSpanId);
                Assert.Equal("bidi_response", value.GetTagItem("gen_ai.operation.name"));
            });
            var audio = Assert.Single(responses, value => Equals(value.GetTagItem("gen_ai.response.id"), "audio-response"));
            var withoutAudio = Assert.Single(responses, value => Equals(value.GetTagItem("gen_ai.response.id"), "tool-response"));
            var firstAudio = audio.GetTagItem("gen_ai.server.time_to_first_audio");
            Assert.NotNull(firstAudio);
            Assert.True(Convert.ToInt64(firstAudio, CultureInfo.InvariantCulture) >= 0);
            Assert.Null(withoutAudio.GetTagItem("gen_ai.server.time_to_first_audio"));
            Assert.Single(session.Events, value => value.Name == "bidi_barge_in");
            Assert.Equal(session.SpanId, toolSpan.ParentSpanId);
            Assert.Equal("execute_tool", toolSpan.GetTagItem("gen_ai.operation.name"));
            Assert.Equal("telemetry_lookup", toolSpan.GetTagItem("gen_ai.tool.name"));
            Assert.Equal("telemetry-call", toolSpan.GetTagItem("gen_ai.tool.call.id"));
            capture.AssertBalanced(caller.TraceId, 5);
        }
        finally { toolRelease.TrySetResult(true); }
    }

    [Fact]
    public async Task SessionUsageAddsAuthoritativeFieldsAcrossReconnectAndResetsAtTheNextStart()
    {
        using var capture = new SpanCapture();
        using var caller = new Activity("bidi usage caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var clock = new BidiScheduleTests.Clock();
        await using var chat = new ToolHostModel();
        await using var host = new Agent(new() { Model = chat });
        await using var model = new TelemetryModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync);
        await agent.StartAsync();
        Assert.Same(caller, Activity.Current);
        await using (var receiver = agent.ReceiveAsync().GetAsyncEnumerator())
        {
            var observed = new List<IAgentStreamEvent>();
            await ReadUntilAsync<BidiConnectionStartEvent>(receiver, observed);
            model.Emit(UsageIncrement(10, 4, 100, 3));
            var first = await ReadUntilAsync<BidiUsageEvent>(receiver, observed);
            Assert.Equal(100, first.Usage.TotalTokens);
            model.Emit(UsageIncrement(5, 3, 50, 2));
            await ReadUntilAsync<BidiUsageEvent>(receiver, observed);

            var lead = await Within(clock.NextAsync());
            Assert.Equal(TimeSpan.FromSeconds(50), lead.Duration);
            lead.Complete();
            await ReadUntilAsync<BidiConnectionWarningEvent>(receiver, observed);
            var remaining = await Within(clock.NextAsync());
            Assert.Equal(TimeSpan.FromSeconds(10), remaining.Duration);
            remaining.Complete();
            var restarted = await ReadUntilAsync<BidiConnectionRestartEvent>(receiver, observed);
            Assert.Equal(BidiRestartReason.Scheduled, restarted.Reason);
            Assert.Null(restarted.TimeoutError);
            await ReadUntilAsync<BidiConnectionStartEvent>(receiver, observed);
            Assert.Equal(1, model.Restarts);
            model.Emit(UsageIncrement(8, 5, 80, 4));
            await ReadUntilAsync<BidiUsageEvent>(receiver, observed);
            await agent.StopAsync();
            while (await Within(receiver.MoveNextAsync().AsTask())) observed.Add(receiver.Current);
        }
        model.AssertReadersJoined(2);
        Assert.Same(caller, Activity.Current);

        model.Settings = new() { AutoRestart = false };
        await agent.StartAsync();
        Assert.Same(caller, Activity.Current);
        await using (var receiver = agent.ReceiveAsync().GetAsyncEnumerator())
        {
            await ReadUntilAsync<BidiConnectionStartEvent>(receiver, []);
            await agent.StopAsync();
            while (await Within(receiver.MoveNextAsync().AsTask())) { }
        }
        model.AssertReadersJoined(3);
        Assert.Equal(2, model.Stops);
        await agent.DisposeAsync();
        Assert.Equal(0, model.Disposals);
        await host.InitializeAsync();
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, chat.Disposals);
        Assert.Same(caller, Activity.Current);

        var spans = capture.Ended(caller.TraceId);
        var sessions = capture.Started(caller.TraceId).Where(value => value.DisplayName.StartsWith("bidi_session ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, sessions.Length);
        var used = sessions[0];
        Assert.Contains(used, spans);
        Assert.Equal(23L, used.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(12L, used.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(230L, used.GetTagItem("gen_ai.usage.total_tokens"));
        Assert.Equal(9L, used.GetTagItem("gen_ai.usage.cache_read.input_tokens"));
        var latest = (Environment.GetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN") ?? "").Split(',')
            .Any(value => value.Trim() == "gen_ai_latest_experimental");
        if (latest) Assert.Null(used.GetTagItem("gen_ai.usage.cache_read_input_tokens"));
        else Assert.Equal(9L, used.GetTagItem("gen_ai.usage.cache_read_input_tokens"));
        Assert.DoesNotContain(sessions[1].TagObjects, value => value.Key.StartsWith("gen_ai.usage.", StringComparison.Ordinal));
        Assert.All(sessions, value => Assert.Equal(caller.SpanId, value.ParentSpanId));
        var connects = spans.Where(value => value.DisplayName == "bidi_connect").ToArray();
        Assert.Equal(2, connects.Length);
        Assert.All(connects, value => Assert.Contains(sessions, session => session.SpanId == value.ParentSpanId));
        var restart = Assert.Single(spans, value => value.DisplayName == "bidi_connection_restart");
        Assert.Equal(used.SpanId, restart.ParentSpanId);
        Assert.Equal("bidi_connection_restart", restart.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("scheduled", restart.GetTagItem("gen_ai.bidi.restart_reason"));
        Assert.Null(restart.GetTagItem("gen_ai.bidi.restart_error_message"));
        capture.AssertBalanced(caller.TraceId, 5);
    }

    [Fact]
    public async Task RestartSpanEndsBeforeTheAfterRestartHookFinishes()
    {
        using var capture = new SpanCapture();
        using var caller = new Activity("bidi restart boundary caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var clock = new BidiScheduleTests.Clock();
        var afterEntered = new TaskCompletionSource<(BidiAfterConnectionRestartEvent Event, CancellationToken Token)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterRelease = Signal();
        await using var chat = new ToolHostModel();
        await using var host = new Agent(new() { Model = chat });
        await using var model = new TelemetryModel { Settings = new() { RestartAfter = TimeSpan.FromSeconds(60) } };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }, clock.DelayAsync);
        agent.Hooks.Register<BidiAfterConnectionRestartEvent>(async (value, token) =>
        {
            afterEntered.TrySetResult((value, token));
            await afterRelease.Task;
        });
        await agent.StartAsync();
        await using (var receiver = agent.ReceiveAsync().GetAsyncEnumerator())
        {
            var observed = new List<IAgentStreamEvent>();
            try
            {
                await ReadUntilAsync<BidiConnectionStartEvent>(receiver, observed);
                var lead = await Within(clock.NextAsync());
                Assert.Equal(TimeSpan.FromSeconds(50), lead.Duration);
                lead.Complete();
                await ReadUntilAsync<BidiConnectionWarningEvent>(receiver, observed);
                var remaining = await Within(clock.NextAsync());
                Assert.Equal(TimeSpan.FromSeconds(10), remaining.Duration);
                remaining.Complete();
                var restarted = await ReadUntilAsync<BidiConnectionRestartEvent>(receiver, observed);
                Assert.Equal(BidiRestartReason.Scheduled, restarted.Reason);
                Assert.Null(restarted.TimeoutError);
                var after = await Within(afterEntered.Task);
                Assert.Same(agent, after.Event.Agent);
                Assert.Equal(BidiRestartReason.Scheduled, after.Event.Reason);
                Assert.Null(after.Event.Error);
                Assert.False(after.Token.CanBeCanceled);
                Assert.Equal(1, model.Restarts);
                Assert.False(afterRelease.Task.IsCompleted);
                Assert.Same(caller, Activity.Current);

                var session = Assert.Single(capture.Started(caller.TraceId), value => value.DisplayName.StartsWith("bidi_session ", StringComparison.Ordinal));
                var started = Assert.Single(capture.Started(caller.TraceId), value => value.DisplayName == "bidi_connection_restart");
                var ended = Assert.Single(capture.Ended(caller.TraceId), value => value.DisplayName == "bidi_connection_restart");
                Assert.Same(started, ended);
                Assert.Equal(session.SpanId, ended.ParentSpanId);
                Assert.Equal("bidi_connection_restart", ended.GetTagItem("gen_ai.operation.name"));
                Assert.Equal("scheduled", ended.GetTagItem("gen_ai.bidi.restart_reason"));
                Assert.Null(ended.GetTagItem("gen_ai.bidi.restart_error_message"));
                Assert.NotEqual(ActivityStatusCode.Error, ended.Status);
            }
            finally { afterRelease.TrySetResult(true); }

            await ReadUntilAsync<BidiConnectionStartEvent>(receiver, observed);
            await agent.StopAsync();
            while (await Within(receiver.MoveNextAsync().AsTask())) observed.Add(receiver.Current);
        }
        model.AssertReadersJoined(2);
        Assert.Equal(1, model.Stops);
        await agent.DisposeAsync();
        Assert.Equal(0, model.Disposals);
        await host.InitializeAsync();
        Assert.Equal(0, chat.Calls);
        Assert.Equal(0, chat.Disposals);
        Assert.Same(caller, Activity.Current);
        capture.AssertBalanced(caller.TraceId, 3);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static BidiUsageEvent UsageIncrement(long input, long output, long total, long cacheRead)
        => new(new Usage(input, output, ReportedTotalTokens: total),
            new Dictionary<string, long> { ["audio"] = input, ["text"] = input, ["cache_read"] = cacheRead },
            new Dictionary<string, long> { ["audio"] = output, ["text"] = output });

    private static async Task<T> ReadUntilAsync<T>(IAsyncEnumerator<IAgentStreamEvent> receiver, List<IAgentStreamEvent> observed) where T : IAgentStreamEvent
    {
        for (var count = 0; count < 32; count++)
        {
            Assert.True(await Within(receiver.MoveNextAsync().AsTask()));
            observed.Add(receiver.Current);
            if (receiver.Current is T match) return match;
        }
        throw new InvalidOperationException("The finite telemetry script did not emit " + typeof(T).Name + ".");
    }

    private sealed class SpanCapture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _started = new();
        private readonly ConcurrentQueue<Activity> _ended = new();
        private readonly ActivityListener _listener;
        internal SpanCapture()
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
        internal Activity[] Started(ActivityTraceId traceId) => _started.Where(value => value.TraceId == traceId).ToArray();
        internal Activity[] Ended(ActivityTraceId traceId) => _ended.Where(value => value.TraceId == traceId).ToArray();
        internal void AssertBalanced(ActivityTraceId traceId, int count)
        {
            var started = Started(traceId);
            var ended = Ended(traceId);
            Assert.Equal(count, started.Length);
            Assert.Equal(count, ended.Length);
            Assert.Equal(count, started.Select(value => value.SpanId).Distinct().Count());
            Assert.Equal(started.Select(value => value.SpanId.ToString()).OrderBy(value => value, StringComparer.Ordinal),
                ended.Select(value => value.SpanId.ToString()).OrderBy(value => value, StringComparer.Ordinal));
            Assert.All(ended, value =>
            {
                Assert.True(value.Duration >= TimeSpan.Zero);
                Assert.NotEqual(ActivityStatusCode.Error, value.Status);
            });
        }
        public void Dispose() => _listener.Dispose();
    }

    private sealed class ToolHostModel : IModel, IModelMetadataProvider, IAsyncDisposable
    {
        internal const string ModelId = "ordinary-chat-only";
        internal int Calls;
        internal int Disposals;
        public ModelMetadata Metadata => new(ModelId);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            throw new InvalidOperationException("The Bidi tool host must not call its ordinary chat model.");
        }
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref Disposals, 1); return default; }
    }

    private sealed class TelemetryModel : IRestartableBidiModel
    {
        private readonly List<ModelConnection> _connections = [];
        private ModelConnection? _current;
        private int _activeReaders;
        internal BidiConnectionOptions Settings { get; set; } = new() { AutoRestart = false };
        internal TaskCompletionSource<BidiMessage> Results { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Restarts;
        internal int Stops;
        internal int Disposals;
        public BidiConnectionOptions Connection => Settings;
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Open();
            return Task.CompletedTask;
        }
        public Task RestartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Restarts++;
            _current!.Events.Close();
            Open();
            return Task.CompletedTask;
        }
        private void Open()
        {
            _current = new();
            _connections.Add(_current);
            Emit(new BidiConnectionStartEvent("connection-" + _connections.Count.ToString(CultureInfo.InvariantCulture), "scripted-bidi"));
        }
        internal void Emit(IBidiOutputEvent value) => _current!.Events.Emit(value);
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (input is BidiMessage message && message.Content.Any(value => value is ToolResultBlock)) Results.TrySetResult(message);
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var current = _current!;
            Interlocked.Increment(ref _activeReaders);
            try
            {
                while (await current.Events.ReadAsync(cancellationToken) is { } value)
                    yield return (IBidiOutputEvent)value;
            }
            finally
            {
                Interlocked.Decrement(ref _activeReaders);
                current.Exited.TrySetResult(true);
            }
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stops++;
            _current!.Events.Close();
            return Task.CompletedTask;
        }
        internal void AssertReadersJoined(int expected)
        {
            Assert.Equal(expected, _connections.Count);
            Assert.Equal(0, Volatile.Read(ref _activeReaders));
            Assert.All(_connections, value => Assert.True(value.Exited.Task.IsCompleted));
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref Disposals, 1);
            foreach (var connection in _connections) connection.Events.Close();
            return default;
        }
        private sealed class ModelConnection
        {
            internal BidiRestartTests.Generation Events { get; } = new();
            internal TaskCompletionSource<bool> Exited { get; } = Signal();
        }
    }
}
