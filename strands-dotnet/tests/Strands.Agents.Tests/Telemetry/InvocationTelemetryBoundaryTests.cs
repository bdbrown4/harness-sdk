using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Strands.Agents.Memory;
using Strands.Agents.Telemetry;

namespace Strands.Agents.Tests.Telemetry;

[Collection("Telemetry listeners")]
public sealed class InvocationTelemetryBoundaryTests
{
    [Fact]
    public async Task AgentPassCreatesDistinctSpansAndMetricsAcrossHookContinuation()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, InvocationTelemetryScope = InvocationTelemetryScope.AgentPass });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (model.Calls == 1) value.Resume = new("continue");
            return default;
        });

        var result = await agent.InvokeAsync("start");

        Assert.Equal(2, AgentSpans(stopped).Length);
        Assert.Equal(2, agent.Metrics.AgentInvocations.Count);
        Assert.Equal(2, result.Turns);
        Assert.Equal(new Usage(2, 4), result.Usage);
        Assert.Single(result.Traces);
        Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task AgentPassEndsBeforeAfterInvocationCallbacks()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        await using var agent = new Agent(new() { Model = new TextModel(), InvocationTelemetryScope = InvocationTelemetryScope.AgentPass });
        var endedAtAfter = false;
        agent.Hooks.Register<AfterInvocationEvent>((_, _) =>
        {
            endedAtAfter = AgentSpans(stopped).Length == 1;
            return default;
        });

        await agent.InvokeAsync("start");

        Assert.True(endedAtAfter);
    }

    [Fact]
    public void InvalidTelemetryScopeIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new() { Model = new TextModel(), InvocationTelemetryScope = (InvocationTelemetryScope)123 }));
    }

    [Theory]
    [InlineData(InvocationTelemetryScope.PublicInvocation, false)]
    [InlineData(InvocationTelemetryScope.PublicInvocation, true)]
    [InlineData(InvocationTelemetryScope.AgentPass, false)]
    [InlineData(InvocationTelemetryScope.AgentPass, true)]
    public async Task TelemetryBoundariesDoNotChangeCumulativeOrExplicitlyResetLimits(InvocationTelemetryScope scope, bool reset)
    {
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, InvocationTelemetryScope = scope });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (model.Calls == 1) value.Resume = new("continue");
            return default;
        });

        var result = await agent.InvokeAsync("start", new() { MaxTurns = 1, ResetLimitsOnHookResume = reset });

        Assert.Equal(reset ? 2 : 1, model.Calls);
        Assert.Equal(reset ? StopReasons.EndTurn : StopReasons.MaxTurns, result.StopReason);
        Assert.Equal(1, result.Turns);
        Assert.Equal(reset ? 2 : 1, agent.Messages.Count(message => message.Role == Role.User));
    }

    [Theory]
    [InlineData(InvocationTelemetryScope.PublicInvocation, 1)]
    [InlineData(InvocationTelemetryScope.AgentPass, 0)]
    public async Task BeforeHookDenialDoesNotCreateAnAcceptedPass(InvocationTelemetryScope scope, int expectedSpans)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, InvocationTelemetryScope = scope });
        agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { value.CancelReason = "denied"; return default; });

        await agent.InvokeAsync("start");

        Assert.Equal(0, model.Calls);
        Assert.Equal(expectedSpans, AgentSpans(stopped).Length);
        Assert.Equal(expectedSpans, agent.Metrics.AgentInvocations.Count);
    }

    [Fact]
    public async Task DefaultPublicInvocationScopeRemainsOpenAcrossAfterHooksAndResumedPasses()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model });
        var observations = new List<int>();
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            observations.Add(AgentSpans(stopped).Length);
            if (model.Calls == 1) value.Resume = new("continue");
            return default;
        });

        await agent.InvokeAsync("start");

        Assert.Equal(new[] { 0, 0 }, observations);
        Assert.Single(AgentSpans(stopped));
        Assert.Single(agent.Metrics.AgentInvocations);
    }

    [Theory]
    [InlineData(InvocationTelemetryScope.PublicInvocation, ActivityStatusCode.Error)]
    [InlineData(InvocationTelemetryScope.AgentPass, ActivityStatusCode.Ok)]
    public async Task AfterHookFailuresRespectTheSelectedCompletionBoundary(InvocationTelemetryScope scope, ActivityStatusCode status)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var failure = new IOException("after callback failed");
        await using var agent = new Agent(new() { Model = new TextModel(), InvocationTelemetryScope = scope });
        agent.Hooks.Register<AfterInvocationEvent>((_, _) => throw failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("start")));

        Assert.Equal(status, Assert.Single(AgentSpans(stopped)).Status);
    }

    [Theory]
    [InlineData(InvocationTelemetryScope.PublicInvocation)]
    [InlineData(InvocationTelemetryScope.AgentPass)]
    public async Task DisposingAtTheAfterBoundaryCannotStartAHiddenContinuation(InvocationTelemetryScope scope)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, InvocationTelemetryScope = scope });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (model.Calls == 1) value.Resume = new("continue");
            return default;
        });

        await foreach (var item in agent.StreamAsync("start")) if (item is AfterInvocationEvent) break;

        Assert.Equal(1, model.Calls);
        Assert.Single(AgentSpans(stopped));
        await agent.InvokeAsync("reuse");
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, AgentSpans(stopped).Length);
    }

    [Theory]
    [InlineData(InvocationTelemetryScope.PublicInvocation)]
    [InlineData(InvocationTelemetryScope.AgentPass)]
    public async Task ProviderFailureKeepsItsOriginalIdentityAndClosesTheSpan(InvocationTelemetryScope scope)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        var failure = new IOException("provider failure");
        await using var agent = new Agent(new() { Model = new TextModel(failure), InvocationTelemetryScope = scope });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("start")));

        Assert.Equal(ActivityStatusCode.Error, Assert.Single(AgentSpans(stopped)).Status);
    }

    [Theory]
    [InlineData(InvocationTelemetryScope.PublicInvocation)]
    [InlineData(InvocationTelemetryScope.AgentPass)]
    public async Task CallerCancellationCannotResumeAnotherPass(InvocationTelemetryScope scope)
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = Listen(stopped);
        using var cancellation = new CancellationTokenSource();
        var model = new TextModel(onStream: cancellation.Cancel);
        await using var agent = new Agent(new() { Model = model, InvocationTelemetryScope = scope });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = new("continue"); return default; });

        var result = await agent.InvokeAsync("start", cancellationToken: cancellation.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(1, model.Calls);
        Assert.Single(AgentSpans(stopped));
    }

    [Theory]
    [InlineData(MemoryTelemetryProfile.TypeScript)]
    [InlineData(MemoryTelemetryProfile.Python)]
    public async Task UnsampledResumedPassCannotReuseThePriorPassMemoryLink(MemoryTelemetryProfile profile)
    {
        var stopped = new ConcurrentQueue<Activity>();
        var invocation = 0;
        using var listener = Listen(stopped, name => name.StartsWith("invoke_agent ", StringComparison.Ordinal) && Interlocked.Increment(ref invocation) > 1
            ? ActivitySamplingResult.PropagationData : ActivitySamplingResult.AllDataAndRecorded);
        var store = new MessageStore();
        var memory = new MemoryManager(new() { Stores = [store], Injection = null, Telemetry = new() { Profile = profile } });
        var model = new TextModel();
        await using var agent = new Agent(new() { Model = model, MemoryManager = memory, InvocationTelemetryScope = InvocationTelemetryScope.AgentPass });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (model.Calls == 1) value.Resume = new("continue");
            return default;
        });
        try { await agent.InvokeAsync("remember"); }
        finally { store.Release.TrySetResult(true); await memory.FlushAsync(); }

        var roots = AgentSpans(stopped);
        var extractions = stopped.Where(span => span.OperationName == "memory.extract").ToArray();
        Assert.Equal(2, roots.Length);
        Assert.Equal(2, extractions.Length);
        Assert.Equal(roots[0].Context, Assert.Single(extractions[0].Links).Context);
        Assert.Empty(extractions[1].Links);
        Assert.Equal(2, store.Calls);
    }

    private static Activity[] AgentSpans(IEnumerable<Activity> spans) => spans.Where(span => span.DisplayName.StartsWith("invoke_agent ", StringComparison.Ordinal)).ToArray();

    private static ActivityListener Listen(ConcurrentQueue<Activity> stopped, Func<string, ActivitySamplingResult>? sampling = null)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Tracer.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> value) => sampling?.Invoke(value.Name) ?? ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class TextModel(Exception? failure = null, Action? onStream = null) : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Calls++;
            onStream?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) throw failure;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            yield return new ModelMetadataEvent(new Usage(1, 2));
        }
    }

    private sealed class MessageStore : MemoryStore, IMessageMemoryStore
    {
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public override string Name => "facts";
        public override bool Writable => true;
        public override MemoryExtractionOptions Extraction => new() { Triggers = [new InvocationTrigger()] };
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default) => new((IReadOnlyList<MemoryEntry>)[]);
        public async ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Release.Task.ConfigureAwait(false);
        }
    }
}
