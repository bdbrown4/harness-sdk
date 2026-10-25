using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Telemetry;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

[Collection("Bidi telemetry")]
public sealed class BidiAgentIdentityTests
{
    [Theory]
    [InlineData(null, null, "Strands Agents")]
    [InlineData("", "", "Strands Agents")]
    [InlineData(" \t ", "preserve whitespace name", " \t ")]
    [InlineData("航海案内役 Δ", "C# の会話エージェント", "航海案内役 Δ")]
    public async Task SessionTelemetryUsesTheBidiAgentsOwnSelectedIdentity(string? name, string? description, string expectedName)
    {
        using var capture = new Capture();
        using var caller = new Activity("bidi identity caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        await using var chat = new NoChatModel();
        await using var host = new Agent(new() { Model = chat, Name = "different ordinary tool host", Description = "tool-host description" });
        await using var model = new IdentityModel();
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host, Name = name, Description = description });
        Task<Exception?>? starting = null;
        Task<Exception?>? draining = null;
        Task<Exception?>? stopping = null;
        try
        {
            Assert.Equal(expectedName, agent.Name);
            Assert.Equal(description, agent.Description);
            Assert.NotEqual(host.Name, agent.Name);
            Assert.NotEqual(host.Description, agent.Description);
            starting = OutcomeAsync(agent.StartAsync());
            Assert.Null(await Within(starting));
            Assert.Same(caller, Activity.Current);
            draining = DrainAsync(agent);
            await Within(model.ReaderEntered.Task);
            stopping = OutcomeAsync(agent.StopAsync());
            Assert.Null(await Within(stopping));
            Assert.Null(await Within(draining));
            Assert.True(model.ReaderExited.Task.IsCompleted);
            Assert.Equal(0, model.ActiveReaders);
            Assert.Equal(1, model.Starts);
            Assert.Equal(1, model.Stops);
            await agent.DisposeAsync();
            Assert.Equal(0, model.Disposals);
            await model.StartAsync();
            await model.StopAsync();
            await host.InitializeAsync();
            Assert.Equal(0, chat.Calls);
            Assert.Equal(0, chat.Disposals);
            Assert.Same(caller, Activity.Current);

            var started = capture.Started(caller.TraceId);
            var ended = capture.Ended(caller.TraceId);
            Assert.Equal(2, started.Length);
            Assert.Equal(2, ended.Length);
            Assert.Equal(started.Select(value => value.SpanId.ToString()).OrderBy(value => value, StringComparer.Ordinal), ended.Select(value => value.SpanId.ToString()).OrderBy(value => value, StringComparer.Ordinal));
            var session = Assert.Single(ended, value => Equals(value.GetTagItem("gen_ai.operation.name"), "bidi_session"));
            var connect = Assert.Single(ended, value => value.DisplayName == "bidi_connect");
            Assert.Equal(caller.SpanId, session.ParentSpanId);
            Assert.Equal(session.SpanId, connect.ParentSpanId);
            Assert.All(ended, value => Assert.NotEqual(ActivityStatusCode.Error, value.Status));
            Assert.DoesNotContain(ended, value => Equals(value.GetTagItem("gen_ai.agent.name"), host.Name));

            // These assertions follow real connection shutdown and every borrowed-resource check.
            Assert.Equal("bidi_session " + expectedName, session.DisplayName);
            Assert.Equal(expectedName, session.GetTagItem("gen_ai.agent.name"));
        }
        finally
        {
            model.Close();
            var cleanup = OutcomeAsync(agent.StopAsync());
            if (starting is not null) await starting;
            if (stopping is not null) await stopping;
            if (draining is not null) await draining;
            Assert.Null(await cleanup);
            await agent.DisposeAsync();
            Assert.Equal(0, model.ActiveReaders);
        }
        Assert.Same(caller, Activity.Current);
    }

    private static async Task<Exception?> OutcomeAsync(Task task)
    {
        try { await task.ConfigureAwait(false); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> DrainAsync(BidiAgent agent)
    {
        try { await foreach (var _ in agent.ReceiveAsync()) { } return null; }
        catch (Exception error) { return error; }
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
            throw new InvalidOperationException("The Bidi tool host must not invoke its ordinary chat model.");
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return default; }
    }

    private sealed class IdentityModel : IBidiModel
    {
        private BidiRestartTests.Generation? _current;
        private int _open;
        private int _activeReaders;
        internal int Starts;
        internal int Stops;
        internal int Disposals;
        internal int ActiveReaders => Volatile.Read(ref _activeReaders);
        internal TaskCompletionSource<bool> ReaderEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReaderExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BidiConnectionOptions Connection { get; } = new() { AutoRestart = false };
        public Task StartAsync(BidiSessionOptions? session = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref Disposals) != 0) throw new ObjectDisposedException(nameof(IdentityModel));
            if (Interlocked.CompareExchange(ref _open, 1, 0) != 0) throw new InvalidOperationException("The model is already open.");
            Interlocked.Increment(ref Starts);
            _current = new();
            _current.Emit(new BidiConnectionStartEvent("identity-connection", "scripted-identity"));
            return Task.CompletedTask;
        }
        public Task SendAsync(IBidiInput input, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The identity fixture does not send provider input.");
        public async IAsyncEnumerable<IBidiOutputEvent> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var current = _current!;
            Interlocked.Increment(ref _activeReaders);
            ReaderEntered.TrySetResult(true);
            try
            {
                while (await current.ReadAsync(cancellationToken) is { } value) yield return (IBidiOutputEvent)value;
            }
            finally { Interlocked.Decrement(ref _activeReaders); ReaderExited.TrySetResult(true); }
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Stops);
            Interlocked.Exchange(ref _open, 0);
            Close();
            return Task.CompletedTask;
        }
        internal void Close() => _current?.Close();
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); Close(); return default; }
    }
}
