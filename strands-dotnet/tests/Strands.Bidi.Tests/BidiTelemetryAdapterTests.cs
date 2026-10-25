using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Bidi.Models;
using Strands.Agents.Telemetry;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

[Collection("Bidi telemetry")]
public sealed class BidiTelemetryAdapterTests
{
    [Theory]
    [InlineData("openai", "fixture-model")]
    [InlineData("google", "live-fixture")]
    public async Task ConfiguredAdapterModelIdentityIsExportedOnSessionAndConnectSpans(string adapter, string modelId)
    {
        using var capture = new SpanCapture();
        using var caller = new Activity("bidi adapter identity caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var startingCancellation = new CancellationTokenSource();
        await using var server = new WebSocketServer();
        await using var chat = new ToolHostModel();
        await using var host = new Agent(new() { Model = chat });
        await using var model = CreateModel(adapter, modelId, server);
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var starting = agent.StartAsync(cancellationToken: startingCancellation.Token);
        var handshaking = CompleteHandshakeAsync(server, adapter, modelId);
        var completed = false;
        try
        {
            Assert.True(await Within(handshaking));
            Assert.True(await Within(CompleteAsync(starting)));
            Assert.Same(caller, Activity.Current);
            await using (var receiver = agent.ReceiveAsync().GetAsyncEnumerator())
            {
                Assert.True(await Within(receiver.MoveNextAsync().AsTask()));
                Assert.Equal(modelId, Assert.IsType<BidiConnectionStartEvent>(receiver.Current).Model);
                await agent.StopAsync();
                while (await Within(receiver.MoveNextAsync().AsTask())) { }
            }
            await agent.DisposeAsync();
            Assert.Same(caller, Activity.Current);
            AssertConfiguredAndUsable(model, modelId);
            await host.InitializeAsync();
            Assert.Equal(0, chat.Calls);
            Assert.Equal(0, chat.Disposals);

            var spans = capture.Ended(caller.TraceId);
            var session = Assert.Single(spans, value => value.DisplayName.StartsWith("bidi_session ", StringComparison.Ordinal));
            var connect = Assert.Single(spans, value => value.DisplayName == "bidi_connect");
            Assert.Equal(caller.SpanId, session.ParentSpanId);
            Assert.Equal(session.SpanId, connect.ParentSpanId);
            Assert.Equal(modelId, session.GetTagItem("gen_ai.request.model"));
            Assert.Equal(modelId, connect.GetTagItem("gen_ai.request.model"));
            Assert.DoesNotContain(spans, value => Equals(value.GetTagItem("gen_ai.request.model"), ToolHostModel.ModelId));
            Assert.DoesNotContain(spans.SelectMany(value => value.TagObjects), value => value.Value is string text && text.Contains("fixture-key"));
            capture.AssertBalanced(caller.TraceId, 2);
            completed = true;
        }
        finally
        {
            startingCancellation.Cancel();
            try { await agent.DisposeAsync(); }
            finally
            {
                try { await server.DisposeAsync(); }
                finally
                {
                    try { await starting; } catch (Exception) when (!completed) { }
                    try { await handshaking; } catch (Exception) when (!completed) { }
                }
            }
        }
    }

    private static IBidiModel CreateModel(string adapter, string modelId, WebSocketServer server)
        => adapter switch
        {
            "openai" => new OpenAIRealtimeModel(new()
            {
                ModelId = modelId,
                TranscriptionModelId = null,
                ApiKey = "fixture-key",
                Endpoint = server.Endpoint,
                Connection = new() { AutoRestart = false },
                CloseTimeout = TimeSpan.FromMilliseconds(100)
            }),
            "google" => new GoogleGeminiLiveModel(new()
            {
                ModelId = modelId,
                ApiKey = "fixture-key",
                Endpoint = server.Endpoint,
                Connection = new() { AutoRestart = false },
                CloseTimeout = TimeSpan.FromMilliseconds(100)
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(adapter))
        };

    private static async Task<bool> CompleteHandshakeAsync(WebSocketServer server, string adapter, string modelId)
    {
        var peer = await server.Connection;
        var initialization = await peer.ReceiveAsync();
        if (adapter == "google")
        {
            Assert.Contains("key=fixture-key", peer.RequestLine);
            Assert.Equal("models/" + modelId, initialization.GetProperty("setup").GetProperty("model").GetString());
            await peer.SendAsync("""{"setupComplete":{}}""");
        }
        else
        {
            Assert.Equal("Bearer fixture-key", peer.Headers["Authorization"]);
            Assert.Contains("model=" + modelId, peer.RequestLine);
            Assert.Equal("session.update", initialization.GetProperty("type").GetString());
        }
        return true;
    }

    private static async Task<bool> CompleteAsync(Task task) { await task; return true; }

    private static void AssertConfiguredAndUsable(IBidiModel model, string modelId)
    {
        if (model is OpenAIRealtimeModel realtime)
        {
            realtime.UpdateConfig(options => options with { });
            Assert.Equal(modelId, realtime.GetConfig().ModelId);
        }
        else
        {
            var live = Assert.IsType<GoogleGeminiLiveModel>(model);
            live.UpdateConfig(options => options with { });
            Assert.Equal(modelId, live.GetConfig().ModelId);
        }
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
}
