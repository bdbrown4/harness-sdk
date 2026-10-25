using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents.Memory;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Memory;

[Collection("Telemetry listeners")]
public sealed class ModelExtractorTelemetryTests : IDisposable
{
    private readonly Dictionary<string, string?> _environment = new[] { "OTEL_SEMCONV_STABILITY_OPT_IN", "LANGFUSE_BASE_URL", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" }
        .ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public ModelExtractorTelemetryTests()
    {
        foreach (var name in _environment.Keys) Environment.SetEnvironmentVariable(name, null);
    }

    public void Dispose()
    {
        foreach (var pair in _environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }

    [Fact]
    public async Task CallerCancellationAcceptedByTheCompletedSpanListenerPreventsSuccessfulFactReturn()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var caller = new CancellationTokenSource();
        using var listener = Listen(stopped, onStopped: span => { if (span.OperationName == "chat") caller.Cancel(); });
        var model = new Model();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelExtractor(new() { Model = model })
            .ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }, caller.Token).AsTask());

        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(ActivityStatusCode.Ok, Assert.Single(stopped).Status);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task PropagationOnlyActivitiesStopWithoutFabricatedModelAccounting(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped, recording: false);
        var model = new Model();
        Assert.Single(await new ModelExtractor(new() { Model = model, TelemetryProfile = profile }).ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }));
        var chat = Assert.Single(stopped);
        Assert.False(chat.IsAllDataRequested);
        Assert.Equal(ActivityStatusCode.Unset, chat.Status);
        Assert.Null(chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Null(chat.GetTagItem("gen_ai.response.finish_reasons"));
        Assert.Empty(chat.Events);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task SynchronousProviderOpenFailureClosesTheModelSpanAndKeepsItsCause(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("outer").SetIdFormat(ActivityIdFormat.W3C).Start();
        var primary = new IOException("open failed");
        var model = new FailureModel(primary, failOnOpen: true);
        var extractor = new ModelExtractor(new() { Model = model, TelemetryProfile = profile });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => extractor.ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }).AsTask()));
        var chat = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, chat.Status);
        Assert.Same(profile == MemoryTelemetryProfile.Python ? chat : ambient, model.OpenAmbient);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task IteratorCleanupFailureAlonePreventsSuccessfulFactsAndMarksTheSpanFailed()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var cleanup = new IOException("close failed");
        var model = new ProbeModel(Events(), cleanup: cleanup);
        Assert.Same(cleanup, await Assert.ThrowsAsync<IOException>(() => new ModelExtractor(new() { Model = model, TelemetryProfile = MemoryTelemetryProfile.Python })
            .ExtractAsync([Message.User("remember")]).AsTask()));
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(stopped).Status);
        Assert.True(model.Cleaned);
    }

    [Fact]
    public async Task MissingTerminalModelResponseIsAStreamFailureRatherThanSuccessfulEmptyFacts()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new ProbeModel([new ModelMessageStartEvent()]);
        await Assert.ThrowsAsync<ModelException>(() => new ModelExtractor(new() { Model = model, TelemetryProfile = MemoryTelemetryProfile.Python })
            .ExtractAsync([Message.User("remember")]).AsTask());
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(stopped).Status);
        Assert.True(model.Cleaned);
    }

    [Fact]
    public async Task TypeScriptStandaloneAndEmptyInputDoNotCreateModelSpans()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new Model();
        Assert.Single(await new ModelExtractor(new() { Model = model }).ExtractAsync([Message.User("remember")]));
        Assert.Empty(await new ModelExtractor(new() { Model = model, TelemetryProfile = MemoryTelemetryProfile.Python }).ExtractAsync([]));
        Assert.Empty(stopped);
    }

    [Fact]
    public void InvalidProfileFailsBeforeAnyProviderOrActivity()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelExtractor(new() { Model = new Model(), TelemetryProfile = (MemoryTelemetryProfile)99 }));
        Assert.Empty(stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeScriptRetainsAbsentUsageAndOnlyEmitsLatencyWithReportedUsage(bool reported)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new Model(reportUsage: reported, metrics: new(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(12)));
        await new ModelExtractor(new() { Model = model }).ExtractAsync([Message.User("remember")], new(model) { Tracer = new() });
        var chat = Assert.Single(stopped);
        Assert.Equal(reported ? 3L : null, chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(reported ? 75d : null, chat.GetTagItem("gen_ai.server.request.duration"));
        Assert.Null(chat.GetTagItem("gen_ai.server.time_to_first_token"));
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task ExplicitExtractorPolicyWinsOverManagerContext(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("outer").SetIdFormat(ActivityIdFormat.W3C).Start();
        var model = new Model();
        var context = new ExtractionContext(model) { Tracer = new(), TelemetryProfile = profile == MemoryTelemetryProfile.Python ? MemoryTelemetryProfile.TypeScript : MemoryTelemetryProfile.Python };
        await new ModelExtractor(new() { Model = model, TelemetryProfile = profile }).ExtractAsync([Message.User("remember")], context);
        Assert.Same(profile == MemoryTelemetryProfile.Python ? Assert.Single(stopped) : ambient, model.ObservedAmbient);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task TolerantFactParsingHappensAfterSuccessfulModelCompletion(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new Model(output: "[invalid]");
        Assert.Empty(await new ModelExtractor(new() { Model = model, TelemetryProfile = profile }).ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }));
        Assert.Equal(ActivityStatusCode.Ok, Assert.Single(stopped).Status);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task ProviderFailureRemainsPrimaryWhenIteratorCleanupAlsoFails(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var primary = new IOException("read failed");
        var cleanup = new IOException("close failed");
        var model = new FailureModel(primary, cleanup);
        var extractor = new ModelExtractor(new() { Model = model, TelemetryProfile = profile });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => extractor.ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }).AsTask()));
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]));
        var chat = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, chat.Status);
        Assert.Equal(primary.Message, chat.StatusDescription);
    }

    [Fact]
    public async Task IndependentBackendCancellationDoesNotAcquireAnUnrequestedCallerToken()
    {
        using var caller = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        independent.Cancel();
        var original = new OperationCanceledException("independent", independent.Token);
        var model = new FailureModel(original);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelExtractor(new() { Model = model }).ExtractAsync([Message.User("remember")], cancellationToken: caller.Token).AsTask());
        Assert.Equal(independent.Token, error.CancellationToken);
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationAcceptedDuringIteratorDisposalPreventsSuccessfulFacts()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var caller = new CancellationTokenSource();
        var model = new ProbeModel(Events(), onDispose: caller.Cancel);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelExtractor(new() { Model = model }).ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }, caller.Token).AsTask());
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(stopped).Status);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task ProviderOpenReadAndCleanupUseTheSelectedAmbientPolicyAndBorrowTheModel(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("outer").SetIdFormat(ActivityIdFormat.W3C).Start();
        var model = new ProbeModel(Events());
        await new ModelExtractor(new() { Model = model, TelemetryProfile = profile }).ExtractAsync([Message.User("remember")], new(model) { Tracer = new() });
        var expected = profile == MemoryTelemetryProfile.Python ? Assert.Single(stopped) : ambient;
        Assert.True(model.Opened);
        Assert.True(model.Cleaned);
        Assert.All(model.Ambient, observed => Assert.Same(expected, observed));
        Assert.False(model.Disposed);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task BrokenMetadataObservationDoesNotPreventProviderWork()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new ProbeModel(Events(), metadataFailure: new IOException("metadata failed"));
        Assert.Empty(await new ModelExtractor(new() { Model = model }).ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }));
        Assert.Null(Assert.Single(stopped).GetTagItem("gen_ai.request.model"));
        Assert.True(model.Cleaned);
    }

    [Fact]
    public async Task ExtractorPromptsAndOutputUseModelContentRedactionRatherThanRawMemoryPayloadRules()
    {
        Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", "gen_ai_unredacted_attributes=");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new Model();
        await new ModelExtractor(new() { Model = model, SystemPrompt = "system-secret" }).ExtractAsync([Message.User("user-secret")], new(model) { Tracer = new() });
        var chat = Assert.Single(stopped);
        var data = string.Join("\n", chat.Events.SelectMany(value => value.Tags).Select(value => value.Value?.ToString()));
        Assert.Contains("[REDACTED]", data);
        Assert.DoesNotContain("system-secret", data);
        Assert.DoesNotContain("user-secret", data);
        Assert.DoesNotContain("remembered", data);
    }

    [Fact]
    public async Task PythonExtractionWorksWithoutAnyActivityListenerOrExporter()
    {
        var model = new Model();
        Assert.Single(await new ModelExtractor(new() { Model = model, TelemetryProfile = MemoryTelemetryProfile.Python }).ExtractAsync([Message.User("remember")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerFailuresCannotChangeSuccessfulFactsOrLeakAmbientContext(bool failOnStop)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { if (!failOnStop) throw new IOException("start observer"); },
            ActivityStopped = _ => { if (failOnStop) throw new IOException("stop observer"); }
        };
        ActivitySource.AddActivityListener(listener);
        using var ambient = new Activity("outer").SetIdFormat(ActivityIdFormat.W3C).Start();
        Assert.Single(await new ModelExtractor(new() { Model = new Model(), TelemetryProfile = MemoryTelemetryProfile.Python }).ExtractAsync([Message.User("remember")]));
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task PreCanceledCallOpensNeitherProviderNorSpan()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var model = new ProbeModel(Events());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelExtractor(new() { Model = model, TelemetryProfile = MemoryTelemetryProfile.Python }).ExtractAsync([Message.User("remember")], cancellationToken: caller.Token).AsTask());
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.False(model.Opened);
        Assert.Empty(stopped);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript, false)]
    [InlineData(MemoryTelemetryProfile.Python, false)]
    [InlineData(MemoryTelemetryProfile.TypeScript, true)]
    [InlineData(MemoryTelemetryProfile.Python, true)]
    public async Task RequestedCallerCancellationRetainsItsTokenAndTheIndependentBackendCause(MemoryTelemetryProfile profile, bool ioFailure)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var caller = new CancellationTokenSource();
        Exception backend = ioFailure ? new IOException("transport closed after caller cancellation") : new OperationCanceledException("backend cancellation");
        var model = new FailureModel(backend, onMove: caller.Cancel);
        var extractor = new ModelExtractor(new() { Model = model, TelemetryProfile = profile });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync([Message.User("remember")], new(model) { Tracer = new() }, caller.Token).AsTask());

        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Same(backend, error.InnerException);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(stopped).Status);
    }

    [Fact]
    public async Task PythonCompletedCallUsesCompatibilityZeroCountersAndBothReportedTimingsWhenUsageIsAbsent()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new Model(reportUsage: false, metrics: new(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(12)));

        await new ModelExtractor(new() { Model = model, TelemetryProfile = MemoryTelemetryProfile.Python }).ExtractAsync([Message.User("remember")]);

        var chat = Assert.Single(stopped);
        Assert.Equal(0L, chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(0L, chat.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(75d, chat.GetTagItem("gen_ai.server.request.duration"));
        Assert.Equal(12d, chat.GetTagItem("gen_ai.server.time_to_first_token"));
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task CoordinatorSuppliesItsTracerAndSourcePolicyToTheRealExtractor(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var extractionModel = new Model();
        var store = new FactStore(new ModelExtractor(new() { Model = extractionModel }));
        var memory = new MemoryManager(new() { Stores = [store], Injection = null, Telemetry = new() { Profile = profile } });
        await using var agent = new Agent(new() { Model = new Model("main-model"), MemoryManager = memory });

        await agent.InvokeAsync("remember");
        await memory.FlushAsync();

        var chat = Assert.Single(stopped, span => Equals(span.GetTagItem("gen_ai.request.model"), "extract-model"));
        var extraction = Assert.Single(stopped, span => span.OperationName == "memory.extract");
        Assert.Equal(extraction.SpanId, chat.ParentSpanId);
        Assert.Equal(profile == MemoryTelemetryProfile.Python ? chat : extraction, extractionModel.ObservedAmbient);
        Assert.Equal("remembered", Assert.Single(store.Facts));
    }

    [Fact]
    public async Task PythonStandaloneCallActivatesItsOwnSpanAcrossTheRealProviderAwait()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var model = new Model();
        var extractor = new ModelExtractor(new() { Model = model, TelemetryProfile = MemoryTelemetryProfile.Python });

        await extractor.ExtractAsync([Message.User("remember")]);

        var chat = Assert.Single(stopped, span => span.OperationName == "chat");
        Assert.Same(chat, model.ObservedAmbient);
        Assert.Equal(ambient.SpanId, chat.ParentSpanId);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task SuppliedTracerWrapsTheRealModelCallWithoutActivatingTheTypeScriptSpan()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("memory-extraction-parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        var model = new Model();
        var extractor = new ModelExtractor(new() { Model = model, SystemPrompt = "extract-system" });

        var facts = await extractor.ExtractAsync([Message.User("remember-user")], new(model) { Tracer = new Tracer() });

        Assert.Equal("remembered", Assert.Single(facts).Content);
        var chat = Assert.Single(stopped, span => span.OperationName == "chat");
        Assert.Equal(ambient.SpanId, chat.ParentSpanId);
        Assert.Equal("extract-model", chat.GetTagItem("gen_ai.request.model"));
        Assert.Equal(ActivityStatusCode.Ok, chat.Status);
        Assert.Equal(3L, chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(4L, chat.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(StopReasons.EndTurn, Assert.Single(Assert.IsType<string[]>(chat.GetTagItem("gen_ai.response.finish_reasons"))));
        Assert.Same(ambient, model.ObservedAmbient);
        Assert.Same(ambient, Activity.Current);
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped, bool recording = true, Action<Activity>? onStopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => recording ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData,
            ActivityStopped = span => { stopped.Enqueue(span); onStopped?.Invoke(span); }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class FactStore(IExtractor extractor) : MemoryStore, IWritableMemoryStore
    {
        internal List<string> Facts { get; } = [];
        public override string Name => "facts";
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => new() { Extractor = extractor, Triggers = [new InvocationTrigger()] };
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
            => new((IReadOnlyList<MemoryEntry>)[]);
        public ValueTask AddAsync(string content, System.Text.Json.JsonElement? metadata = null, CancellationToken cancellationToken = default)
        {
            Facts.Add(content);
            return default;
        }
    }

    private sealed class FailureModel(Exception failure, Exception? cleanup = null, Action? onMove = null, bool failOnOpen = false) : IModel, IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        internal Activity? OpenAmbient;
        public ModelStreamEvent Current => throw new InvalidOperationException();
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { OpenAmbient = Activity.Current; if (failOnOpen) throw failure; return this; }
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { onMove?.Invoke(); throw failure; }
        public ValueTask DisposeAsync() { if (cleanup is not null) throw cleanup; return default; }
    }

    private static IReadOnlyList<ModelStreamEvent> Events() => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
        new ModelContentBlockDeltaEvent(0, Text: "[]"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];

    private sealed class ProbeModel(IReadOnlyList<ModelStreamEvent> events, Action? onDispose = null, Exception? metadataFailure = null, Exception? cleanup = null) : IModel, IModelMetadataProvider, IDisposable,
        IAsyncEnumerable<ModelStreamEvent>, IAsyncEnumerator<ModelStreamEvent>
    {
        private int _index = -1;
        internal List<Activity?> Ambient { get; } = [];
        internal bool Opened;
        internal bool Cleaned;
        internal bool Disposed;
        public ModelMetadata Metadata => metadataFailure is null ? new("probe") : throw metadataFailure;
        public ModelStreamEvent Current => events[_index];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Opened = true; Ambient.Add(Activity.Current); return this; }
        public IAsyncEnumerator<ModelStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() { Ambient.Add(Activity.Current); return new(++_index < events.Count); }
        public ValueTask DisposeAsync() { Cleaned = true; Ambient.Add(Activity.Current); onDispose?.Invoke(); if (cleanup is not null) throw cleanup; return default; }
        public void Dispose() => Disposed = true;
    }

    private sealed class Model(string id = "extract-model", bool reportUsage = true, ModelPerformanceMetrics? metrics = null, string output = "[{\"content\":\"remembered\"}]") : IModel, IModelMetadataProvider
    {
        internal Activity? ObservedAmbient;
        public ModelMetadata Metadata => new(id);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ObservedAmbient = Activity.Current;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: output);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            yield return new ModelMetadataEvent(reportUsage ? new(3, 4) : new()) { UsageReported = reportUsage, PerformanceMetrics = metrics };
        }
    }
}
