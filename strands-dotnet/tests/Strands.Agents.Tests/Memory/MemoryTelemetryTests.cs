using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Strands.Agents.Memory;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Memory;

[Collection("Telemetry listeners")]
public sealed class MemoryTelemetryTests
{
    [Fact]
    public async Task SearchPublishesRawQueryAttributedResultsAndRequestedStoreSelection()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var manager = new MemoryManager(new() { Stores = [new Store("facts", [new("native C#", "untrusted", JsonSerializer.SerializeToElement(new { score = 7 }))])], Injection = null });

        var results = await manager.SearchAsync("private query", new() { Stores = ["facts", "facts"], MaxSearchResults = 2 });

        var span = Assert.Single(stopped, value => value.OperationName == "memory.search");
        Assert.Equal(ambient.SpanId, span.ParentSpanId);
        Assert.Same(ambient, Activity.Current);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("memory.search", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("strands-agents", span.GetTagItem("gen_ai.system"));
        Assert.Equal("[\"facts\",\"facts\"]", span.GetTagItem("memory.store.names"));
        Assert.Equal(2, span.GetTagItem("memory.store.count"));
        Assert.Equal(2, span.GetTagItem("memory.max_search_results"));
        Assert.Equal(1, span.GetTagItem("memory.result.count"));
        Assert.Equal(0, span.GetTagItem("memory.store.failure_count"));
        Assert.Equal("private query", Payload(span, "memory.query"));
        using var payload = JsonDocument.Parse(Payload(span, "memory.results"));
        Assert.Equal("native C#", payload.RootElement[0].GetProperty("content").GetString());
        Assert.Equal("facts", payload.RootElement[0].GetProperty("storeName").GetString());
        Assert.Equal(7, payload.RootElement[0].GetProperty("metadata").GetProperty("score").GetInt32());
        Assert.Equal("facts", Assert.Single(results).StoreName);
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task ProfileControlsBackendContextAndResultFieldNames(MemoryTelemetryProfile profile)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var store = new ContextStore();
        var manager = new MemoryManager(new() { Stores = [store], Injection = null, Telemetry = new() { Profile = profile } });

        await manager.SearchAsync("query");

        var span = Assert.Single(stopped, item => item.OperationName == "memory.search");
        Assert.Equal(profile == MemoryTelemetryProfile.Python ? span.SpanId : ambient.SpanId, store.ObservedParent);
        Assert.Same(ambient, Activity.Current);
        using var payload = JsonDocument.Parse(Payload(span, "memory.results"));
        var entry = payload.RootElement[0];
        Assert.Equal("context", entry.GetProperty(profile == MemoryTelemetryProfile.Python ? "store_name" : "storeName").GetString());
        Assert.Equal(profile == MemoryTelemetryProfile.Python, entry.TryGetProperty("metadata", out var metadata));
        if (profile == MemoryTelemetryProfile.Python) Assert.Equal(JsonValueKind.Null, metadata.ValueKind);
    }

    [Fact]
    public async Task AddPublishesOnlyWritableDefaultStoresAndRawContent()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var writable = new WritableStore("writable");
        var manager = new MemoryManager(new() { Stores = [new Store("read-only", []), writable], Injection = null });

        await manager.AddAsync("private fact", new() { Metadata = JsonSerializer.SerializeToElement(new { source = "user" }) });

        var span = Assert.Single(stopped, value => value.OperationName == "memory.add");
        Assert.Equal(ambient.SpanId, span.ParentSpanId);
        Assert.Same(ambient, Activity.Current);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("memory.add", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("[\"writable\"]", span.GetTagItem("memory.store.names"));
        Assert.Equal(1, span.GetTagItem("memory.store.count"));
        Assert.Equal(0, span.GetTagItem("memory.store.failure_count"));
        Assert.Equal("private fact", Payload(span, "memory.content"));
        Assert.Null(span.GetTagItem("memory.result.count"));
        Assert.Null(span.GetTagItem("memory.max_search_results"));
        Assert.Equal(1, writable.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackendSearchFailuresRemainSuccessfulAndCountStores(bool allFail)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var manager = new MemoryManager(new()
        {
            Stores = [new Store("first", [], new IOException("offline")),
                new Store("second", [new("available")], allFail ? new InvalidOperationException("unavailable") : null)],
            Injection = null
        });

        var results = await manager.SearchAsync("query");

        var span = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(allFail ? 2 : 1, span.GetTagItem("memory.store.failure_count"));
        Assert.Equal(allFail ? 0 : 1, span.GetTagItem("memory.result.count"));
        Assert.Equal(allFail ? 0 : 1, results.Count);
        Assert.DoesNotContain(span.Events, value => value.Name == "exception");
        using var payload = JsonDocument.Parse(Payload(span, "memory.results"));
        Assert.Equal(results.Count, payload.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownSelectionFailsTheSpanBeforeAnyBackendIo(bool write)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var store = new CallbackStore();
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });

        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            if (write) await manager.AddAsync("fact", new() { Stores = [store.Name, "missing"] });
            else await manager.SearchAsync("query", new() { Stores = [store.Name, "missing"] });
        });

        var span = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(error.Message, span.StatusDescription);
        Assert.Equal(typeof(ArgumentException).FullName, span.GetTagItem("error.type"));
        Assert.Equal("[\"callback\",\"missing\"]", span.GetTagItem("memory.store.names"));
        Assert.Equal(0, span.GetTagItem("memory.store.failure_count"));
        Assert.DoesNotContain(span.Events, value => value.Name == "memory.results");
        Assert.Equal(0, store.Reads + store.Writes);
    }

    [Fact]
    public async Task AggregateWriteFailureCountsFailedStoresAndPreservesTheirOriginalCauses()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var first = new IOException("first cause");
        var second = new InvalidOperationException("second cause");
        var healthy = new WritableStore("healthy");
        var manager = new MemoryManager(new() { Stores = [new WritableStore("failed", new AggregateException(first, second)), healthy], Injection = null });

        var error = await Assert.ThrowsAsync<AggregateException>(async () => await manager.AddAsync("fact"));

        Assert.Equal(new Exception[] { first, second }, error.InnerExceptions);
        Assert.Equal(1, healthy.Writes);
        var span = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(AggregateException).FullName, span.GetTagItem("error.type"));
        Assert.Equal(1, span.GetTagItem("memory.store.failure_count"));
    }

    [Theory]
    [InlineData(false, MemoryTelemetryProfile.TypeScript)]
    [InlineData(true, MemoryTelemetryProfile.TypeScript)]
    [InlineData(false, MemoryTelemetryProfile.Python)]
    [InlineData(true, MemoryTelemetryProfile.Python)]
    public async Task CallerCancellationEndsFailedWithTheExactTokenAndRestoresAmbient(bool write, MemoryTelemetryProfile profile)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var cancellation = new CancellationTokenSource();
        var store = new CallbackStore
        {
            Search = async token => { await Task.Yield(); cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Array.Empty<MemoryEntry>(); },
            Add = async token => { await Task.Yield(); cancellation.Cancel(); token.ThrowIfCancellationRequested(); },
        };
        var manager = new MemoryManager(new() { Stores = [store], Injection = null, Telemetry = new() { Profile = profile } });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (write) await manager.AddAsync("fact", cancellationToken: cancellation.Token);
            else await manager.SearchAsync("query", cancellationToken: cancellation.Token);
        });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(ambient, Activity.Current);
        var span = Assert.Single(stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.DoesNotContain(span.Events, value => value.Name == "memory.results");
    }

    [Theory]
    [InlineData(false, MemoryTelemetryProfile.TypeScript)]
    [InlineData(true, MemoryTelemetryProfile.TypeScript)]
    [InlineData(false, MemoryTelemetryProfile.Python)]
    [InlineData(true, MemoryTelemetryProfile.Python)]
    public async Task RequestedCallerCancellationReplacesAnUnrelatedBackendTokenAndRetainsTheCause(bool write, MemoryTelemetryProfile profile)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var cancellation = new CancellationTokenSource();
        var backend = new OperationCanceledException("backend canceled without the caller token");
        var store = new CallbackStore
        {
            Search = async _ => { await Task.Yield(); cancellation.Cancel(); throw backend; },
            Add = async _ => { await Task.Yield(); cancellation.Cancel(); throw backend; },
        };
        var manager = new MemoryManager(new() { Stores = [store], Injection = null, Telemetry = new() { Profile = profile } });

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (write) await manager.AddAsync("fact", cancellationToken: cancellation.Token);
            else await manager.SearchAsync("query", cancellationToken: cancellation.Token);
        });

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Same(backend, actual.InnerException);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(stopped).Status);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task AcceptedDetachedToolWritesStartRootSpansAndDrainWithoutCallerLifetime(MemoryTelemetryProfile profile)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var ambient = new Activity("tool caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActivitySpanId observed = default;
        var store = new CallbackStore { Add = async _ => { await release.Task.ConfigureAwait(false); observed = Activity.Current?.SpanId ?? default; } };
        var manager = new MemoryManager(new() { Stores = [store], AddTool = new() { WaitForWrites = false }, Injection = null, Telemetry = new() { Profile = profile } });
        await using var agent = new Agent(new() { Model = new UnusedModel() });
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(manager.GetTools(), value => value.Spec.Name == "add_memory"));
        var input = JsonSerializer.SerializeToElement(new { entries = new[] { "fact" } });
        try
        {
            var result = await tool.InvokeResultAsync(input, new(agent, new("write", tool.Spec.Name, input), new Dictionary<string, object?>()));
            Assert.Equal(1, result.ContentBlocks.OfType<JsonBlock>().Single().Json.GetProperty("accepted").GetInt32());
            Assert.DoesNotContain(stopped, value => value.OperationName == "memory.add");
        }
        finally { release.TrySetResult(true); }
        await manager.FlushAsync();

        var span = Assert.Single(stopped, value => value.OperationName == "memory.add");
        Assert.Equal(default, span.ParentSpanId);
        Assert.NotEqual(ambient.TraceId, span.TraceId);
        Assert.Equal(profile == MemoryTelemetryProfile.Python ? span.SpanId : ambient.SpanId, observed);
        Assert.Same(ambient, Activity.Current);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PayloadPlacementUsesExistingConventionsWithoutApplyingModelRedaction(bool attributesOnly, bool latest)
    {
        var tokens = "gen_ai_unredacted_attributes=" + (attributesOnly ? ",gen_ai_span_attributes_only" : "")
            + (latest ? ",gen_ai_latest_experimental" : "");
        using var environment = new EnvironmentScope(tokens);
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var manager = new MemoryManager(new() { Stores = [new WritableStore("facts")], Injection = null });

        await manager.SearchAsync("raw query");
        await manager.AddAsync("raw fact");

        var search = Assert.Single(stopped, value => value.OperationName == "memory.search");
        var add = Assert.Single(stopped, value => value.OperationName == "memory.add");
        Assert.Equal("strands-agents", search.GetTagItem(latest ? "gen_ai.provider.name" : "gen_ai.system"));
        Assert.Null(search.GetTagItem("memory.max_search_results"));
        if (attributesOnly)
        {
            Assert.Empty(search.Events);
            Assert.Equal("[]", search.GetTagItem("content"));
            Assert.Equal("raw fact", add.GetTagItem("content"));
        }
        else
        {
            Assert.Equal("raw query", Payload(search, "memory.query"));
            Assert.Equal("raw fact", Payload(add, "memory.content"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailingActivityListenersCannotChangeApplicationResultsOrAmbient(bool failAtStart)
    {
        using var environment = new EnvironmentScope();
        using var ambient = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { if (failAtStart) throw new IOException("listener start"); },
            ActivityStopped = _ => { if (!failAtStart) throw new IOException("listener stop"); },
        };
        ActivitySource.AddActivityListener(listener);
        var manager = new MemoryManager(new() { Stores = [new Store("facts", [new("available")])], Injection = null });

        Assert.Equal("available", Assert.Single(await manager.SearchAsync("query")).Content);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task ExplicitAttributesAreCopiedAndCannotOverrideOperationIdentity()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var values = new[] { "original" };
        var attributes = new Dictionary<string, object?> { ["custom.values"] = values, ["gen_ai.operation.name"] = "forged" };
        var manager = new MemoryManager(new() { Stores = [new Store("facts", [])], Injection = null, Telemetry = new() { TraceAttributes = attributes } });
        values[0] = "mutated";
        attributes["new"] = "later";

        await manager.SearchAsync("query");

        var span = Assert.Single(stopped);
        Assert.Equal("original", Assert.IsType<string[]>(span.GetTagItem("custom.values"))[0]);
        Assert.Null(span.GetTagItem("new"));
        Assert.Equal("memory.search", span.GetTagItem("gen_ai.operation.name"));
    }

    [Fact]
    public async Task FailingDiagnosticSinkCannotPromoteAnIsolatedSearchFailureToAnApplicationError()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var original = Trace.Listeners.Cast<TraceListener>().ToArray();
        using var broken = new ThrowingDiagnosticListener();
        try
        {
            Trace.Listeners.Clear();
            Trace.Listeners.Add(broken);
            var manager = new MemoryManager(new() { Stores = [new Store("failed", [], new IOException("backend"))], Injection = null });

            Assert.Empty(await manager.SearchAsync("query"));

            Assert.True(broken.Calls > 0);
            var span = Assert.Single(stopped);
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
            Assert.Equal(1, span.GetTagItem("memory.store.failure_count"));
        }
        finally
        {
            Trace.Listeners.Clear();
            foreach (var previous in original) Trace.Listeners.Add(previous);
        }
    }

    [Fact]
    public async Task LangfuseSelectsAttributePayloadsWithoutAnExplicitOptIn()
    {
        using var environment = new EnvironmentScope();
        Environment.SetEnvironmentVariable("LANGFUSE_BASE_URL", "https://langfuse.invalid");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var manager = new MemoryManager(new() { Stores = [new WritableStore("facts")], Injection = null });

        await manager.AddAsync("raw fact");

        var span = Assert.Single(stopped);
        Assert.Equal("raw fact", span.GetTagItem("content"));
        Assert.Empty(span.Events);
    }

    [Fact]
    public async Task PreCanceledCallsDoNotAdmitSpansOrBackendWork()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new CallbackStore();
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });

        var search = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.SearchAsync("query", cancellationToken: cancellation.Token));
        var add = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.AddAsync("fact", cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, search.CancellationToken);
        Assert.Equal(cancellation.Token, add.CancellationToken);
        Assert.Empty(stopped);
        Assert.Equal(0, store.Reads + store.Writes);
    }

    [Fact]
    public void InvalidTelemetryConfigurationFailsBeforeBackendWork()
    {
        var store = new CallbackStore();
        Assert.Throws<ArgumentException>(() => new MemoryManager(new() { Stores = [store], Telemetry = new() { Profile = (MemoryTelemetryProfile)100 } }));
        Assert.Throws<ArgumentNullException>(() => new MemoryManager(new() { Stores = [store], Telemetry = null! }));
        Assert.Equal(0, store.Reads + store.Writes);
    }

    [Fact]
    public async Task FailedDetachedWriteEndsItsRootSpanWhileTheAcceptedToolAndDrainSucceed()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var manager = new MemoryManager(new() { Stores = [new WritableStore("failed", new IOException("backend failed"))], AddTool = new() { WaitForWrites = false }, Injection = null });
        await using var agent = new Agent(new() { Model = new UnusedModel() });
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(manager.GetTools(), value => value.Spec.Name == "add_memory"));
        var input = JsonSerializer.SerializeToElement(new { entries = new[] { "fact" } });

        var result = await tool.InvokeResultAsync(input, new(agent, new("write", tool.Spec.Name, input), new Dictionary<string, object?>()));
        await manager.FlushAsync();

        Assert.Equal(ToolResultStatus.Success, result.Status);
        var span = Assert.Single(stopped, value => value.OperationName == "memory.add");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(1, span.GetTagItem("memory.store.failure_count"));
        Assert.Equal(default, span.ParentSpanId);
    }

    private static string Payload(Activity span, string name)
        => (string)Assert.Single(span.Events, item => item.Name == name).Tags.Single(pair => pair.Key == "content").Value!;

    private sealed class Store(string name, IReadOnlyList<MemoryEntry> entries, Exception? failure = null) : MemoryStore
    {
        public override string Name => name;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => failure is null ? new(entries) : throw failure;
    }

    private sealed class WritableStore(string name, Exception? failure = null) : MemoryStore, IWritableMemoryStore
    {
        public override string Name => name;
        public override bool Writable => true;
        internal int Writes;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        { Writes++; return failure is null ? default : throw failure; }
    }

    private sealed class ContextStore : MemoryStore
    {
        public override string Name => "context";
        internal ActivitySpanId ObservedParent;
        public override async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
        { await Task.Yield(); ObservedParent = Activity.Current?.SpanId ?? default; return new[] { new MemoryEntry("fact") }; }
    }

    private sealed class CallbackStore : MemoryStore, IWritableMemoryStore
    {
        public override string Name => "callback";
        public override bool Writable => true;
        internal int Reads;
        internal int Writes;
        internal Func<CancellationToken, ValueTask<IReadOnlyList<MemoryEntry>>>? Search { get; init; }
        internal Func<CancellationToken, ValueTask>? Add { get; init; }
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
        { Reads++; return Search?.Invoke(cancellationToken) ?? new((IReadOnlyList<MemoryEntry>)[]); }
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        { Writes++; return Add?.Invoke(cancellationToken) ?? default; }
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture must not call a model.");
    }

    private sealed class ThrowingDiagnosticListener : TraceListener
    {
        internal int Calls;
        public override void Write(string? message) { Calls++; throw new IOException("diagnostic sink"); }
        public override void WriteLine(string? message) { Calls++; throw new IOException("diagnostic sink"); }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
        internal EnvironmentScope(string? tokens = null)
        {
            foreach (var name in new[] { "OTEL_SEMCONV_STABILITY_OPT_IN", "OTEL_SERVICE_NAME", "LANGFUSE_BASE_URL", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" })
            { _previous[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, name == "OTEL_SEMCONV_STABILITY_OPT_IN" ? tokens : null); }
        }
        public void Dispose()
        { foreach (var entry in _previous) Environment.SetEnvironmentVariable(entry.Key, entry.Value); }
    }
}
