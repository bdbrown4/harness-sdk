using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Memory;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class ManualMemoryTracerTests : IDisposable
{
    private readonly Dictionary<string, string?> _environment = new(StringComparer.Ordinal);

    public ManualMemoryTracerTests()
    {
        foreach (var name in new[] { "OTEL_SEMCONV_STABILITY_OPT_IN", "OTEL_SERVICE_NAME", "LANGFUSE_BASE_URL", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" })
        {
            _environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ManualSearchRecordsItsQueryAndRequestedSelectionWithoutChangingAmbient()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("manual caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer();

        var span = tracer.StartMemorySearchSpan(new("private query", ["facts", "facts"]) { MaxSearchResults = 3 });
        tracer.EndMemorySearchSpan(span);

        var activity = Assert.Single(stopped, value => value.OperationName == "memory.search");
        Assert.Equal(ambient.SpanId, activity.ParentSpanId);
        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal("memory.search", activity.GetTagItem("gen_ai.operation.name"));
        Assert.Equal(2, activity.GetTagItem("memory.store.count"));
        Assert.Equal(3, activity.GetTagItem("memory.max_search_results"));
        Assert.Equal(new[] { "facts", "facts" }, JsonSerializer.Deserialize<string[]>(Assert.IsType<string>(activity.GetTagItem("memory.store.names"))));
        Assert.Equal("private query", Assert.Single(activity.Events, value => value.Name == "memory.query").Tags.Single(value => value.Key == "content").Value);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void SearchCompletionDefaultsRecordAnEmptySuccessfulResult()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();

        var span = tracer.StartMemorySearchSpan(new("query", ["facts"]));
        tracer.EndMemorySearchSpan(span);

        var activity = Assert.Single(stopped);
        Assert.Equal(0, activity.GetTagItem("memory.result.count"));
        Assert.Equal(0, activity.GetTagItem("memory.store.failure_count"));
        Assert.Null(activity.GetTagItem("memory.max_search_results"));
        Assert.Equal("[]", Assert.Single(activity.Events, value => value.Name == "memory.results").Tags.Single(value => value.Key == "content").Value);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.NotNull(activity.GetTagItem("gen_ai.event.end_time"));
    }

    [Fact]
    public void ManualWriteCanDetachAndRecordItsOriginalFailure()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("write scheduler").SetIdFormat(ActivityIdFormat.W3C).Start();
        var error = new IOException("write failed");
        var tracer = new Tracer(new Dictionary<string, object?> { ["constructor.attribute"] = "not a manual memory attribute" });

        var span = tracer.StartMemoryAddSpan(new("private fact", ["first", "second"]) { ForceRoot = true });
        tracer.EndMemoryAddSpan(span, new() { StoreFailureCount = 2, Error = error });

        var activity = Assert.Single(stopped, value => value.OperationName == "memory.add");
        Assert.Equal(default, activity.ParentSpanId);
        Assert.NotEqual(ambient.TraceId, activity.TraceId);
        Assert.Equal(2, activity.GetTagItem("memory.store.count"));
        Assert.Equal(2, activity.GetTagItem("memory.store.failure_count"));
        Assert.Null(activity.GetTagItem("constructor.attribute"));
        Assert.Equal("private fact", Assert.Single(activity.Events, value => value.Name == "memory.content").Tags.Single(value => value.Key == "content").Value);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(error.Message, activity.StatusDescription);
        Assert.Equal(error.Message, Assert.Single(activity.Events, value => value.Name == "exception").Tags.Single(value => value.Key == "exception.message").Value);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void ManualInjectionReportsRendererFailureWithoutFailingTheOperation()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();

        var span = tracer.StartMemoryInjectSpan();
        tracer.EndMemoryInjectSpan(span, new(false) { FormatError = true });

        var activity = Assert.Single(stopped, value => value.OperationName == "memory.inject");
        Assert.Null(activity.GetTagItem("memory.max_entries"));
        Assert.Equal(false, activity.GetTagItem("memory.injected"));
        Assert.Equal(0, activity.GetTagItem("memory.entry.count"));
        Assert.Equal(true, activity.GetTagItem("memory.inject.format_error"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Empty(activity.Events);
    }

    [Fact]
    public void ManualExtractionIsADetachedLinkedRootAndDoesNotInventASavedCountOnFailure()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var scheduler = new Activity("extract scheduler").SetIdFormat(ActivityIdFormat.W3C).Start();
        var error = new IOException("save failed");
        var tracer = new Tracer();

        var span = tracer.StartMemoryExtractSpan(new("facts", 3)
        {
            FilteredCount = 2,
            Extractor = "CustomExtractor",
            AgentSpanContext = scheduler.Context,
        });
        tracer.EndMemoryExtractSpan(span, new() { Error = error });

        var activity = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(default, activity.ParentSpanId);
        Assert.NotEqual(scheduler.TraceId, activity.TraceId);
        Assert.Equal(scheduler.Context, Assert.Single(activity.Links).Context);
        Assert.Equal(scheduler.TraceId.ToHexString(), activity.GetTagItem("memory.parent.trace_id"));
        Assert.Equal(scheduler.SpanId.ToHexString(), activity.GetTagItem("memory.parent.span_id"));
        Assert.Equal("facts", activity.GetTagItem("memory.store.name"));
        Assert.Equal(3, activity.GetTagItem("memory.message.count"));
        Assert.Equal(2, activity.GetTagItem("memory.message.filtered_count"));
        Assert.Equal("CustomExtractor", activity.GetTagItem("memory.extractor"));
        Assert.Null(activity.GetTagItem("memory.entry.count"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(error.Message, activity.StatusDescription);
        Assert.Same(scheduler, Activity.Current);
    }

    [Fact]
    public void PythonManualSearchUsesItsExplicitParentAndPerCallMergePolicy()
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "configured-ts-service");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("ambient").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer(new Dictionary<string, object?> { ["constructor.only"] = "excluded" });
        using var parent = tracer.StartSpan("explicit parent");
        var span = tracer.StartMemorySearchSpan(new("query", ["facts"])
        {
            Profile = MemoryTelemetryProfile.Python,
            ParentSpan = parent,
            CustomTraceAttributes = new Dictionary<string, object?> { ["memory.store.count"] = 7, ["labels"] = new[] { "copied" } },
            ExtraAttributes = new Dictionary<string, object?>
            {
                ["memory.store.count"] = 9,
                ["extra.flag"] = true,
                ["ignored.array"] = new[] { "not scalar" },
                ["gen_ai.event.start_time"] = "custom start",
            }
        });
        tracer.EndMemorySearchSpan(span, new() { Entries = [new("fact")], StoreFailureCount = 1 });

        var activity = Assert.Single(stopped, value => value.OperationName == "memory.search");
        Assert.Equal(parent.Context!.Value.SpanId, activity.ParentSpanId);
        Assert.Equal("strands-agents", activity.GetTagItem("gen_ai.system"));
        Assert.Equal(9, activity.GetTagItem("memory.store.count"));
        Assert.Equal(true, activity.GetTagItem("extra.flag"));
        Assert.Equal("copied", Assert.Single(Assert.IsType<string[]>(activity.GetTagItem("labels"))));
        Assert.Null(activity.GetTagItem("ignored.array"));
        Assert.Null(activity.GetTagItem("constructor.only"));
        Assert.Equal("custom start", activity.GetTagItem("gen_ai.event.start_time"));
        using var results = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(activity.Events, value => value.Name == "memory.results").Tags.Single(value => value.Key == "content").Value));
        Assert.Equal(JsonValueKind.Null, results.RootElement[0].GetProperty("store_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, results.RootElement[0].GetProperty("metadata").ValueKind);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void PropagationOnlyManualActivitiesKeepTheirIdentityWithoutRecordingResultData()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped, ActivitySamplingResult.PropagationData);
        var tracer = new Tracer();

        var span = tracer.StartMemorySearchSpan(new("private query", ["facts"]) { Profile = MemoryTelemetryProfile.Python });
        tracer.EndMemorySearchSpan(span, new() { Entries = [new("private fact")] });

        var activity = Assert.Single(stopped);
        Assert.False(activity.IsAllDataRequested);
        Assert.NotEqual(default, activity.TraceId);
        Assert.NotEqual(default, activity.SpanId);
        Assert.Empty(activity.Events);
        Assert.Null(activity.GetTagItem("memory.result.count"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
    }

    [Fact]
    public void OtherOperationDefaultsPreserveOptionalFieldAbsenceAndKnownZero()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        tracer.EndMemoryAddSpan(tracer.StartMemoryAddSpan(new("fact", [])));
        tracer.EndMemoryInjectSpan(tracer.StartMemoryInjectSpan(new() { MaxEntries = 4 }), new(true) { EntryCount = 2 });
        tracer.EndMemoryExtractSpan(tracer.StartMemoryExtractSpan(new("facts", 0)), new() { EntryCount = 0 });

        Assert.Equal(0, Assert.Single(stopped, value => value.OperationName == "memory.add").GetTagItem("memory.store.failure_count"));
        var inject = Assert.Single(stopped, value => value.OperationName == "memory.inject");
        Assert.Equal(4, inject.GetTagItem("memory.max_entries"));
        Assert.Equal(true, inject.GetTagItem("memory.injected"));
        Assert.Equal(2, inject.GetTagItem("memory.entry.count"));
        Assert.Null(inject.GetTagItem("memory.inject.format_error"));
        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(0, extract.GetTagItem("memory.message.filtered_count"));
        Assert.Equal(0, extract.GetTagItem("memory.entry.count"));
        Assert.Null(extract.GetTagItem("memory.extractor"));
        Assert.All(stopped, value => Assert.Equal(ActivityStatusCode.Ok, value.Status));
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript, "storeName", false)]
    [InlineData(MemoryTelemetryProfile.Python, "store_name", true)]
    public void SearchResultsRetainTheSelectedSourceJsonPolicy(MemoryTelemetryProfile profile, string storeKey, bool metadataPresent)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        var span = tracer.StartMemorySearchSpan(new("query", ["facts"]) { Profile = profile });
        tracer.EndMemorySearchSpan(span, new() { Entries = [new("fact", storeName: "facts")], StoreFailureCount = 2 });

        var activity = Assert.Single(stopped);
        Assert.Equal(1, activity.GetTagItem("memory.result.count"));
        Assert.Equal(2, activity.GetTagItem("memory.store.failure_count"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        using var json = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(activity.Events, value => value.Name == "memory.results").Tags.Single(value => value.Key == "content").Value));
        Assert.Equal("facts", json.RootElement[0].GetProperty(storeKey).GetString());
        Assert.Equal(metadataPresent, json.RootElement[0].TryGetProperty("metadata", out var metadata));
        if (metadataPresent) Assert.Equal(JsonValueKind.Null, metadata.ValueKind);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript, "configured service")]
    [InlineData(MemoryTelemetryProfile.Python, "strands-agents")]
    public void ManualProviderNamesUseTheirSourceDefaults(MemoryTelemetryProfile profile, string expected)
    {
        Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", "configured service");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        tracer.EndMemoryInjectSpan(tracer.StartMemoryInjectSpan(new() { Profile = profile }), new(false));
        Assert.Equal(expected, Assert.Single(stopped).GetTagItem("gen_ai.system"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawMemoryPayloadsRespectPlacementButNotTheModelContentAllowlist(bool attributesOnly)
    {
        Environment.SetEnvironmentVariable("OTEL_SEMCONV_STABILITY_OPT_IN", "gen_ai_latest_experimental,gen_ai_unredacted_attributes=gen_ai.output.messages" + (attributesOnly ? ",gen_ai_span_attributes_only" : ""));
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        tracer.EndMemorySearchSpan(tracer.StartMemorySearchSpan(new("private query", ["facts"])), new() { Entries = [new("private fact")] });
        var activity = Assert.Single(stopped);
        string results;
        if (attributesOnly)
        {
            Assert.Empty(activity.Events);
            results = Assert.IsType<string>(activity.GetTagItem("content"));
        }
        else
        {
            Assert.Equal("private query", Assert.Single(activity.Events, value => value.Name == "memory.query").Tags.Single(value => value.Key == "content").Value);
            results = Assert.IsType<string>(Assert.Single(activity.Events, value => value.Name == "memory.results").Tags.Single(value => value.Key == "content").Value);
        }
        using var json = JsonDocument.Parse(results);
        Assert.Equal("private fact", json.RootElement[0].GetProperty("content").GetString());
        Assert.False(json.RootElement[0].TryGetProperty("storeName", out _));
    }

    [Fact]
    public void InvalidProfilesAndUnsupportedTypeScriptCallOptionsFailBeforeStartingActivities()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        var unknown = (MemoryTelemetryProfile)999;
        Assert.Throws<ArgumentException>(() => tracer.StartMemorySearchSpan(new("query", []) { Profile = unknown }));
        Assert.Throws<ArgumentException>(() => tracer.StartMemoryAddSpan(new("fact", []) { Profile = unknown }));
        Assert.Throws<ArgumentException>(() => tracer.StartMemoryInjectSpan(new() { Profile = unknown }));
        Assert.Throws<ArgumentException>(() => tracer.StartMemoryExtractSpan(new("facts", 0) { Profile = unknown }));
        Assert.Throws<ArgumentException>(() => tracer.StartMemorySearchSpan(new("query", []) { CustomTraceAttributes = new Dictionary<string, object?>() }));
        Assert.Throws<ArgumentException>(() => tracer.StartMemoryAddSpan(new("fact", []) { ExtraAttributes = new Dictionary<string, object?>() }));
        Assert.Throws<ArgumentException>(() => tracer.StartMemoryInjectSpan(new() { ExtraAttributes = new Dictionary<string, object?>() }));
        Assert.Throws<ArgumentException>(() => tracer.StartMemoryExtractSpan(new("facts", 0) { ExtraAttributes = new Dictionary<string, object?>() }));
        Assert.Empty(stopped);
    }

    [Fact]
    public void CallerSelectionAndAttributeArraysAreCopiedBeforeStartedListenersRun()
    {
        var names = new List<string> { "original" };
        var labels = new[] { "original" };
        var attributes = new Dictionary<string, object?> { ["labels"] = labels };
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped, onStarted: _ => { names[0] = "mutated"; labels[0] = "mutated"; attributes.Clear(); });
        var tracer = new Tracer();
        tracer.EndMemorySearchSpan(tracer.StartMemorySearchSpan(new("query", names) { Profile = MemoryTelemetryProfile.Python, CustomTraceAttributes = attributes }));
        var activity = Assert.Single(stopped);
        Assert.Equal("original", Assert.Single(Assert.IsType<string[]>(activity.GetTagItem("labels"))));
        Assert.Equal(new[] { "original" }, JsonSerializer.Deserialize<string[]>(Assert.IsType<string>(activity.GetTagItem("memory.store.names"))));
    }

    [Fact]
    public void InvalidExtractionContextProducesNoLinkAndUnknownCountIsOmitted()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        tracer.EndMemoryExtractSpan(tracer.StartMemoryExtractSpan(new("facts", 1) { AgentSpanContext = default(ActivityContext) }));
        var activity = Assert.Single(stopped);
        Assert.Empty(activity.Links);
        Assert.Null(activity.GetTagItem("memory.parent.trace_id"));
        Assert.Null(activity.GetTagItem("memory.parent.span_id"));
        Assert.Null(activity.GetTagItem("memory.entry.count"));
    }

    [Fact]
    public void PythonEndedExplicitParentFallsBackToTheCurrentAmbientContext()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("current").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer();
        using var ended = tracer.StartSpan("ended");
        ended.End();
        tracer.EndMemoryInjectSpan(tracer.StartMemoryInjectSpan(new() { Profile = MemoryTelemetryProfile.Python, ParentSpan = ended }), new(false));
        Assert.Equal(ambient.SpanId, Assert.Single(stopped, value => value.OperationName == "memory.inject").ParentSpanId);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void NullAndAlreadyCompletedHandlesDoNotObserveAnotherResultCollection()
    {
        var tracer = new Tracer();
        tracer.EndMemorySearchSpan(null);
        tracer.EndMemoryAddSpan(null);
        tracer.EndMemoryInjectSpan(null, null!);
        tracer.EndMemoryExtractSpan(null);
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var span = tracer.StartMemorySearchSpan(new("query", []));
        tracer.EndMemorySearchSpan(span);
        tracer.EndMemorySearchSpan(span, new() { Entries = new BrokenEntries() });
        Assert.Single(stopped);
    }

    [Fact]
    public void ResultObservationFailureStillClosesTheManualActivity()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        tracer.EndMemorySearchSpan(tracer.StartMemorySearchSpan(new("query", [])), new() { Entries = new BrokenEntries() });
        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.DoesNotContain(activity.Events, value => value.Name == "memory.results");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailingListenersCannotEscapeTheManualHelperOrLeakAmbientContext(bool stop)
    {
        var stopped = new ConcurrentQueue<Activity>();
        Action<Activity> fail = _ => throw new IOException("observer failed");
        using var listener = Listen(stopped, onStarted: stop ? null : fail, onStopped: stop ? fail : null);
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer();
        tracer.EndMemorySearchSpan(tracer.StartMemorySearchSpan(new("query", [])));
        Assert.Single(stopped);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public void NoListenerStillAllowsEveryManualOperationToComplete()
    {
        var tracer = new Tracer();
        using var span = tracer.StartMemorySearchSpan(new("query", []));
        Assert.Null(span.Context);
        tracer.EndMemorySearchSpan(span);
        tracer.EndMemoryAddSpan(tracer.StartMemoryAddSpan(new("fact", [])));
        tracer.EndMemoryInjectSpan(tracer.StartMemoryInjectSpan(), new(false));
        tracer.EndMemoryExtractSpan(tracer.StartMemoryExtractSpan(new("facts", 0)));
    }

    [Fact]
    public void SearchFailureSuppressesTheResultPayloadAndPreservesTheOriginalErrorDescription()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var failure = new IOException("search failed");
        var tracer = new Tracer();
        tracer.EndMemorySearchSpan(tracer.StartMemorySearchSpan(new("query", [])), new() { Entries = [new("hidden result")], StoreFailureCount = 2, Error = failure });
        var activity = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(failure.Message, activity.StatusDescription);
        Assert.Equal(1, activity.GetTagItem("memory.result.count"));
        Assert.Equal(2, activity.GetTagItem("memory.store.failure_count"));
        Assert.DoesNotContain(activity.Events, value => value.Name == "memory.results");
    }

    [Fact]
    public void LangfusePlacementRecordsTheRawMemoryWriteAsAnAttribute()
    {
        Environment.SetEnvironmentVariable("LANGFUSE_BASE_URL", "https://example.invalid");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "https://example.invalid/otlp");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var tracer = new Tracer();
        tracer.EndMemoryAddSpan(tracer.StartMemoryAddSpan(new("private fact", ["facts"])));
        var activity = Assert.Single(stopped);
        Assert.Equal("private fact", activity.GetTagItem("content"));
        Assert.Empty(activity.Events);
    }

    [Fact]
    public void PythonPerCallPolicyAppliesToWritesInjectionAndDetachedExtractionWithoutOwningTheParent()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("ambient").SetIdFormat(ActivityIdFormat.W3C).Start();
        var tracer = new Tracer();
        using var parent = tracer.StartSpan("borrowed parent");
        tracer.EndMemoryAddSpan(tracer.StartMemoryAddSpan(new("fact", [])
        {
            Profile = MemoryTelemetryProfile.Python,
            ParentSpan = parent,
            ForceRoot = true,
            CustomTraceAttributes = new Dictionary<string, object?> { ["write.attribute"] = "retained" },
        }));
        tracer.EndMemoryInjectSpan(tracer.StartMemoryInjectSpan(new()
        {
            Profile = MemoryTelemetryProfile.Python,
            ParentSpan = parent,
            MaxEntries = 3,
            ExtraAttributes = new Dictionary<string, object?> { ["memory.max_entries"] = 7 },
        }), new(true) { EntryCount = 1 });
        tracer.EndMemoryExtractSpan(tracer.StartMemoryExtractSpan(new("facts", 2)
        {
            Profile = MemoryTelemetryProfile.Python,
            AgentSpanContext = parent.Context,
            ExtraAttributes = new Dictionary<string, object?> { ["memory.message.count"] = 8, ["ignored"] = new object() },
        }), new() { EntryCount = 1 });

        var write = Assert.Single(stopped, value => value.OperationName == "memory.add");
        Assert.Equal(default, write.ParentSpanId);
        Assert.Equal("retained", write.GetTagItem("write.attribute"));
        var inject = Assert.Single(stopped, value => value.OperationName == "memory.inject");
        Assert.Equal(parent.Context!.Value.SpanId, inject.ParentSpanId);
        Assert.Equal(7, inject.GetTagItem("memory.max_entries"));
        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(default, extract.ParentSpanId);
        Assert.Equal(parent.Context.Value, Assert.Single(extract.Links).Context);
        Assert.Equal(8, extract.GetTagItem("memory.message.count"));
        Assert.Null(extract.GetTagItem("ignored"));
        Assert.DoesNotContain(stopped, value => value.OperationName == "borrowed parent");
        Assert.Same(ambient, Activity.Current);
    }

    public void Dispose()
    {
        foreach (var value in _environment) Environment.SetEnvironmentVariable(value.Key, value.Value);
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped, ActivitySamplingResult sampling = ActivitySamplingResult.AllDataAndRecorded,
        Action<Activity>? onStarted = null, Action<Activity>? onStopped = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
            ActivityStarted = value => onStarted?.Invoke(value),
            ActivityStopped = value => { stopped.Enqueue(value); onStopped?.Invoke(value); },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class BrokenEntries : IReadOnlyList<MemoryEntry>
    {
        public int Count => 1;
        public MemoryEntry this[int index] => throw new IOException("result observer");
        public IEnumerator<MemoryEntry> GetEnumerator() => throw new IOException("result observer");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
