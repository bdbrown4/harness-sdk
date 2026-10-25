using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Memory;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Memory;

[Collection("Telemetry listeners")]
public sealed class MemoryLifecycleTelemetryTests
{
    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task InjectionParentsItsSearchAndRecordsOnlyTheEphemeralProjection(MemoryTelemetryProfile profile)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new Model();
        var memory = new MemoryManager(new() { Stores = [new Store()], Telemetry = new() { Profile = profile } });
        await using var agent = new Agent(new() { Model = model, MemoryManager = memory });

        await agent.InvokeAsync("question");

        var inject = Assert.Single(stopped, value => value.OperationName == "memory.inject");
        var search = Assert.Single(stopped, value => value.OperationName == "memory.search");
        Assert.Equal(inject.SpanId, search.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Ok, inject.Status);
        Assert.Equal("memory.inject", inject.GetTagItem("gen_ai.operation.name"));
        Assert.Equal(5, inject.GetTagItem("memory.max_entries"));
        Assert.Equal(true, inject.GetTagItem("memory.injected"));
        Assert.Equal(1, inject.GetTagItem("memory.entry.count"));
        Assert.Contains("<memory>", Assert.Single(model.Requests).Messages[0].Text);
        Assert.Equal(1, model.Requests[0].DynamicTrailingBlocks);
        Assert.Equal("question", agent.Messages[0].Text);
        Assert.DoesNotContain("<memory>", agent.Messages[0].Text);
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task InvocationExtractionIsAnActiveRootLinkedToItsAlreadyEndedSchedulingRun(MemoryTelemetryProfile profile)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        var started = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped, started);
        var store = new MessageStore();
        var manager = new MemoryManager(new() { Stores = [store], Injection = null, Telemetry = new() { Profile = profile } });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        try
        {
            await agent.InvokeAsync("remember");
            var invocation = Assert.Single(stopped, value => value.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal));
            var extracting = Assert.Single(started, value => value.OperationName == "memory.extract");
            Assert.DoesNotContain(stopped, value => ReferenceEquals(value, extracting));
            Assert.Equal(default, extracting.ParentSpanId);
            Assert.NotEqual(invocation.TraceId, extracting.TraceId);
            var linked = Assert.Single(extracting.Links).Context;
            Assert.Equal(invocation.Context, linked);
            Assert.Equal(invocation.TraceId.ToHexString(), extracting.GetTagItem("memory.parent.trace_id"));
            Assert.Equal(invocation.SpanId.ToHexString(), extracting.GetTagItem("memory.parent.span_id"));
        }
        finally { store.Release.TrySetResult(true); await manager.FlushAsync(); }

        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(ActivityStatusCode.Ok, extract.Status);
        Assert.Equal(extract.SpanId, store.ObservedContext);
        Assert.Equal("facts", extract.GetTagItem("memory.store.name"));
        Assert.Equal(2, extract.GetTagItem("memory.message.count"));
        Assert.Equal(0, extract.GetTagItem("memory.message.filtered_count"));
        Assert.Equal(2, extract.GetTagItem("memory.entry.count"));
        Assert.Null(extract.GetTagItem("memory.extractor"));
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("predicate-failure")]
    [InlineData("empty-query")]
    [InlineData("query-failure")]
    [InlineData("no-results")]
    [InlineData("format-failure")]
    [InlineData("empty-format")]
    public async Task InjectionSkipAndFailOpenOutcomesMatchTheRendererContract(string outcome)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var options = outcome switch
        {
            "gate" => new MemoryInjectionOptions { Predicate = _ => false },
            "predicate-failure" => new MemoryInjectionOptions { Predicate = _ => throw new IOException("predicate") },
            "empty-query" => new MemoryInjectionOptions { Query = (_, _) => new((string?)null) },
            "query-failure" => new MemoryInjectionOptions { Query = (_, _) => throw new IOException("query") },
            "format-failure" => new MemoryInjectionOptions { Format = (_, _) => throw new IOException("format") },
            "empty-format" => new MemoryInjectionOptions { Format = (_, _) => new(" ") },
            _ => new MemoryInjectionOptions(),
        };
        var model = new Model();
        var manager = new MemoryManager(new() { Stores = [outcome == "no-results" ? new Store([]) : new Store()], Injection = options });
        await using var agent = new Agent(new() { Model = model, MemoryManager = manager });

        await agent.InvokeAsync("question");

        Assert.Equal("question", Assert.Single(model.Requests).Messages[0].Text);
        if (outcome is "gate" or "predicate-failure")
        {
            Assert.DoesNotContain(stopped, value => value.OperationName.StartsWith("memory.", StringComparison.Ordinal));
            return;
        }
        var inject = Assert.Single(stopped, value => value.OperationName == "memory.inject");
        Assert.Equal(ActivityStatusCode.Ok, inject.Status);
        Assert.Equal(outcome == "empty-format", inject.GetTagItem("memory.injected"));
        Assert.Equal(outcome == "empty-format" ? 1 : 0, inject.GetTagItem("memory.entry.count"));
        Assert.Equal(outcome == "format-failure" ? true : null, inject.GetTagItem("memory.inject.format_error"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InjectionCallerCancellationPropagatesItsTokenAndClosesTheSpan(bool formatting)
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var cancellation = new CancellationTokenSource();
        var backend = new OperationCanceledException("callback token unavailable");
        var injection = formatting ? new MemoryInjectionOptions
        {
            Format = async (_, _) => { await Task.Yield(); cancellation.Cancel(); throw backend; },
        } : new MemoryInjectionOptions
        {
            Query = async (_, _) => { await Task.Yield(); cancellation.Cancel(); throw backend; },
        };
        var model = new Model();
        await using var agent = new Agent(new() { Model = model, MemoryManager = new(new() { Stores = [new Store()], Injection = injection }) });
        await agent.InitializeAsync();
        var before = new BeforeModelCallEvent(agent, new([Message.User("question")]));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await agent.Hooks.InvokeAsync(before, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(backend, error.InnerException);
        Assert.Empty(model.Requests);
        var inject = Assert.Single(stopped, value => value.OperationName == "memory.inject");
        Assert.Equal(ActivityStatusCode.Ok, inject.Status);
        Assert.Equal(false, inject.GetTagItem("memory.injected"));
        Assert.Null(inject.GetTagItem("memory.inject.format_error"));
    }

    [Fact]
    public async Task ExtractionFlushStartsAnUnlinkedRootEvenInsideAnAmbientRequest()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var store = new MessageStore(new() { Triggers = [new IntervalTrigger(50)] }, delayed: false);
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        await agent.InvokeAsync("remember");
        Assert.DoesNotContain(stopped, value => value.OperationName == "memory.extract");
        using var ambient = new Activity("flush caller").SetIdFormat(ActivityIdFormat.W3C).Start();

        await manager.FlushAsync();

        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(default, extract.ParentSpanId);
        Assert.Empty(extract.Links);
        Assert.Null(extract.GetTagItem("memory.parent.trace_id"));
        Assert.Null(extract.GetTagItem("memory.parent.span_id"));
        Assert.Equal(2, extract.GetTagItem("memory.entry.count"));
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task QueuedExtractionsKeepDistinctSchedulingLinksAndCanceledFlushDoesNotAbortAcceptedWork()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var store = new MessageStore();
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        try
        {
            await agent.InvokeAsync("first");
            await agent.InvokeAsync("second");
            Assert.Equal(1, store.Calls);
            using var cancellation = new CancellationTokenSource();
            var waiting = manager.FlushAsync(cancellation.Token);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.DoesNotContain(stopped, value => value.OperationName == "memory.extract");
        }
        finally { store.Release.TrySetResult(true); await manager.FlushAsync(); }

        Assert.Equal(2, store.Calls);
        Assert.False(store.ObservedToken.CanBeCanceled);
        var invocations = stopped.Where(value => value.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal)).ToArray();
        var extractions = stopped.Where(value => value.OperationName == "memory.extract").ToArray();
        Assert.Equal(2, extractions.Length);
        Assert.Equal(invocations.Select(value => value.Context), extractions.Select(value => Assert.Single(value.Links).Context));
        Assert.All(extractions, value => Assert.Equal(2, value.GetTagItem("memory.entry.count")));
    }

    [Fact]
    public async Task ExtractionFailureRetainsPendingMessagesAndSuccessfulFlushRetriesWithoutTheOldLink()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var backend = new IOException("backend unavailable");
        var store = new MessageStore(delayed: false) { Failure = backend };
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });

        await agent.InvokeAsync("remember");
        var failed = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal(backend.Message, failed.StatusDescription);
        Assert.Null(failed.GetTagItem("memory.entry.count"));
        store.Failure = null;
        await manager.FlushAsync();
        await manager.FlushAsync();

        var extracts = stopped.Where(value => value.OperationName == "memory.extract").ToArray();
        Assert.Equal(2, extracts.Length);
        Assert.Equal(2, store.Calls);
        Assert.Equal(ActivityStatusCode.Ok, extracts[1].Status);
        Assert.Equal(2, extracts[1].GetTagItem("memory.entry.count"));
        Assert.Empty(extracts[1].Links);
    }

    [Fact]
    public async Task FullyFilteredFreshBatchesEmitZeroEntriesWithoutTouchingTheBackendOrRepeatingTheBatch()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var store = new MessageStore(new() { Triggers = [new InvocationTrigger()], ExcludeContentBlocks = ["text"] }, delayed: false);
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });

        await agent.InvokeAsync("remember");
        await manager.FlushAsync();

        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(0, store.Calls);
        Assert.Equal(0, extract.GetTagItem("memory.message.count"));
        Assert.Equal(2, extract.GetTagItem("memory.message.filtered_count"));
        Assert.Equal(0, extract.GetTagItem("memory.entry.count"));
        Assert.Equal(ActivityStatusCode.Ok, extract.Status);
    }

    [Fact]
    public async Task ClientExtractionReportsFactCountAndActivatesTheSpanForExtractorAndWrites()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var extractor = new FactsExtractor();
        var store = new FactStore(extractor);
        var auxiliary = new Model();
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), AuxModel = auxiliary, MemoryManager = manager });

        await agent.InvokeAsync("remember");
        await manager.FlushAsync();

        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(nameof(FactsExtractor), extract.GetTagItem("memory.extractor"));
        Assert.Equal(2, extract.GetTagItem("memory.message.count"));
        Assert.Equal(3, extract.GetTagItem("memory.entry.count"));
        Assert.Equal(3, store.Contexts.Count);
        Assert.All(store.Contexts, value => Assert.Equal(extract.SpanId, value));
        Assert.Equal(extract.SpanId, extractor.ObservedContext);
        Assert.Same(auxiliary, extractor.ObservedModel);
    }

    [Fact]
    public async Task CustomTriggersLinkTheirActualAmbientActivityInsteadOfTheOldAgentInvocation()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var trigger = new CustomTrigger();
        var store = new MessageStore(new() { Triggers = [trigger] }, delayed: false);
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });
        await agent.InvokeAsync("remember");
        using var ambient = new Activity("custom scheduling context").SetIdFormat(ActivityIdFormat.W3C).Start();

        trigger.Fire();
        await manager.FlushAsync();

        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Equal(ambient.Context, Assert.Single(extract.Links).Context);
        Assert.Equal(default, extract.ParentSpanId);
        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task UnsampledInvocationDoesNotBorrowAnUnrelatedAmbientLink()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped, unrecordedInvocation: true);
        using var ambient = new Activity("external request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var store = new MessageStore(delayed: false);
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });

        await agent.InvokeAsync("remember");
        await manager.FlushAsync();

        var invocation = Assert.Single(stopped, value => value.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal));
        Assert.False(invocation.IsAllDataRequested);
        var extract = Assert.Single(stopped, value => value.OperationName == "memory.extract");
        Assert.Empty(extract.Links);
        Assert.Null(extract.GetTagItem("memory.parent.trace_id"));
        Assert.Equal(default, extract.ParentSpanId);
    }

    [Fact]
    public async Task FailingExtractionStopListenerCannotCauseAnAlreadyWrittenBatchToRepeat()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var broken = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = value => { if (value.OperationName == "memory.extract") throw new IOException("listener stop"); },
        };
        ActivitySource.AddActivityListener(broken);
        var store = new MessageStore(delayed: false);
        var manager = new MemoryManager(new() { Stores = [store], Injection = null });
        await using var agent = new Agent(new() { Model = new Model(), MemoryManager = manager });

        await agent.InvokeAsync("remember");
        await manager.FlushAsync();
        await manager.FlushAsync();

        Assert.Equal(1, store.Calls);
        Assert.Equal(ActivityStatusCode.Ok, Assert.Single(stopped, value => value.OperationName == "memory.extract").Status);
    }

    [Fact]
    public async Task FailingInjectionDiagnosticSinkStillAllowsTheModelToRunWithoutProjectedMemory()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var original = Trace.Listeners.Cast<TraceListener>().ToArray();
        using var broken = new BrokenDiagnostic();
        try
        {
            Trace.Listeners.Clear();
            Trace.Listeners.Add(broken);
            var model = new Model();
            var manager = new MemoryManager(new() { Stores = [new Store()], Injection = new() { Format = (_, _) => throw new IOException("format") } });
            await using var agent = new Agent(new() { Model = model, MemoryManager = manager });

            await agent.InvokeAsync("question");

            Assert.True(broken.Calls > 0);
            Assert.Equal("question", Assert.Single(model.Requests).Messages[0].Text);
            var inject = Assert.Single(stopped, value => value.OperationName == "memory.inject");
            Assert.Equal(ActivityStatusCode.Ok, inject.Status);
            Assert.Equal(true, inject.GetTagItem("memory.inject.format_error"));
        }
        finally
        {
            Trace.Listeners.Clear();
            foreach (var previous in original) Trace.Listeners.Add(previous);
        }
    }

    [Fact]
    public async Task AgentInvocationKeepsItsCancellationResultContractAndNeverCallsTheModel()
    {
        using var environment = new EnvironmentScope();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var cancellation = new CancellationTokenSource();
        var model = new Model();
        var manager = new MemoryManager(new()
        {
            Stores = [new Store()],
            Injection = new() { Query = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return new("unused"); } },
        });
        await using var agent = new Agent(new() { Model = model, MemoryManager = manager });

        var result = await agent.InvokeAsync("question", cancellationToken: cancellation.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Empty(model.Requests);
        Assert.Equal(ActivityStatusCode.Ok, Assert.Single(stopped, value => value.OperationName == "memory.inject").Status);
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped, ConcurrentQueue<Activity>? started = null, bool unrecordedInvocation = false)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> value) => unrecordedInvocation && value.Name.StartsWith("invoke_agent", StringComparison.Ordinal)
                ? ActivitySamplingResult.PropagationData : ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = value => started?.Enqueue(value),
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class Store(IReadOnlyList<MemoryEntry>? entries = null) : MemoryStore
    {
        public override string Name => "facts";
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
            => new(entries ?? new[] { new MemoryEntry("known fact") });
    }

    private sealed class MessageStore : MemoryStore, IMessageMemoryStore
    {
        private readonly MemoryExtractionOptions _extraction;
        internal MessageStore(MemoryExtractionOptions? extraction = null, bool delayed = true)
        { _extraction = extraction ?? new() { Triggers = [new InvocationTrigger()] }; if (!delayed) Release.TrySetResult(true); }
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ActivitySpanId ObservedContext;
        internal CancellationToken ObservedToken;
        internal int Calls;
        internal Exception? Failure { get; set; }
        public override string Name => "facts";
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => _extraction;
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public async ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default)
        { Calls++; ObservedToken = cancellationToken; await Release.Task.ConfigureAwait(false); ObservedContext = Activity.Current?.SpanId ?? default; if (Failure is { } error) throw error; }
    }

    private sealed class Model : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class FactsExtractor : IExtractor
    {
        internal IModel? ObservedModel;
        internal ActivitySpanId ObservedContext;
        public async ValueTask<IReadOnlyList<ExtractionResult>> ExtractAsync(IReadOnlyList<Message> messages, ExtractionContext? context = null, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ObservedModel = context?.DefaultModel;
            ObservedContext = Activity.Current?.SpanId ?? default;
            return new[] { new ExtractionResult("one"), new ExtractionResult("two"), new ExtractionResult("three") };
        }
    }

    private sealed class FactStore(IExtractor extractor) : MemoryStore, IWritableMemoryStore
    {
        internal ConcurrentQueue<ActivitySpanId> Contexts { get; } = new();
        public override string Name => "facts";
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => new() { Triggers = [new InvocationTrigger()], Extractor = extractor };
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public async ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default)
        { await Task.Yield(); Contexts.Enqueue(Activity.Current?.SpanId ?? default); }
    }

    private sealed class CustomTrigger : ExtractionTrigger
    {
        private Action? _fire;
        public override string Name => "custom";
        public override IDisposable Attach(Agent agent, Action fire) { _fire = fire; return new Subscription(() => _fire = null); }
        internal void Fire() => _fire!();
    }

    private sealed class Subscription(Action callback) : IDisposable
    {
        private Action? _callback = callback;
        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }

    private sealed class BrokenDiagnostic : TraceListener
    {
        internal int Calls;
        public override void Write(string? message) { Calls++; throw new IOException("diagnostic"); }
        public override void WriteLine(string? message) { Calls++; throw new IOException("diagnostic"); }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);
        internal EnvironmentScope()
        {
            foreach (var name in new[] { "OTEL_SEMCONV_STABILITY_OPT_IN", "OTEL_SERVICE_NAME", "LANGFUSE_BASE_URL", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT" })
            { _previous[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, null); }
        }
        public void Dispose()
        { foreach (var item in _previous) Environment.SetEnvironmentVariable(item.Key, item.Value); }
    }
}
