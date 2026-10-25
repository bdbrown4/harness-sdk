using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#if NETFRAMEWORK
using Strands.Agents.Tests.PrimaryModelPortability;
#endif

namespace Strands.Agents.Tests;

public sealed class PrimaryModelReassignmentConformanceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalLifecycleRejectsBeforeReadingCandidateCapability(bool dispose)
    {
        var original = new Probe("original");
        var candidate = new Probe("unused") { ReadCapability = () => throw new IOException("candidate getter must not run") };
        var agent = new Agent(new() { Model = original });
        try
        {
            if (dispose) await agent.DisposeAsync();
            else await agent.ShutdownAsync();
            Assert.Throws<ObjectDisposedException>(() => agent.SetModel(candidate));
            Assert.Equal(0, candidate.CapabilityReads);
            Assert.Same(original, agent.Model);
            Assert.Empty(candidate.StreamRequests);
        }
        finally { await agent.DisposeAsync(); }
    }

    [Fact]
    public async Task NullReplacementAndInvalidTimingDoNotPublishOrInvokeAFactory()
    {
        var factory = new ScopedOwner(_ => new Probe("unused"));
        await using var agent = new Agent(new() { Model = factory });
        Assert.Throws<ArgumentNullException>(() => agent.SetModel(null!));
        Assert.Same(factory, agent.Model);
        Assert.Equal(0, factory.Creations);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new()
        {
            Model = factory,
            ModelSelectionTiming = (ModelSelectionTiming)999
        }));
        Assert.Equal(0, factory.Creations);
    }

    [Fact]
    public async Task CandidateCapabilityGetterAllowsIndependentLifecycleGateReentry()
    {
        await using var agent = new Agent(new() { Model = new Probe("original") });
        var auxiliary = new Probe("auxiliary");
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        var candidate = new Probe("replacement")
        {
            ReadCapability = () =>
            {
                entered.TrySetResult(true);
                if (!release.Wait(Watchdog)) throw new TimeoutException("Fixture capability gate was not released.");
            }
        };
        Task? reentry = null;
        var setter = Task.Run(() => agent.SetModel(candidate));
        Exception? primary = null;
        try
        {
            await Within(entered.Task);
            reentry = Task.Run(() => agent.SetAuxModel(auxiliary));
            await Within(reentry);
            Assert.Same(auxiliary, agent.AuxModel);
            Assert.False(setter.IsCompleted);
            release.Set();
            await Within(setter);
            Assert.Same(candidate, agent.Model);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.Set();
            await ObserveJoined(setter, primary);
            if (reentry is not null) await ObserveJoined(reentry, primary);
        }
    }

    [Theory]
    [InlineData(ModelSelectionTiming.BeforeModelHooks)]
    [InlineData(ModelSelectionTiming.AfterModelHooks)]
    public async Task FactoryReentryOnRetryKeepsTheCapturedScopeForCountAndProvider(ModelSelectionTiming timing)
    {
        var failure = new IOException("first provider failure");
        var original = new Probe("unused") { Script = _ => throw failure };
        var scope = new Probe("resolved scope");
        var later = new Probe("later selection");
        Agent? active = null;
        var owner = new ScopedOwner(_ => scope) { OnCreate = _ => active!.SetModel(later) };
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) =>
        {
            if (ReferenceEquals(value.Error, failure))
            {
                value.Agent.SetModel(owner);
                value.Retry = true;
            }
            return default;
        });
        await using var agent = new Agent(new() { Model = original, Hooks = hooks, ModelSelectionTiming = timing });
        active = agent;

        var result = await agent.InvokeAsync("retry with another owner");

        Assert.Equal("resolved scope", result.Message!.Text);
        Assert.Single(original.StreamRequests);
        Assert.Equal(1, owner.Creations);
        Assert.Single(scope.CountRequests);
        Assert.Single(scope.StreamRequests);
        Assert.Empty(later.CountRequests);
        Assert.Empty(later.StreamRequests);
        Assert.Same(later, agent.Model);
        Assert.Equal("later selection", (await agent.InvokeAsync("next invocation")).Message!.Text);
        Assert.Equal(0, owner.DisposeCalls);
        Assert.Equal(0, scope.DisposeCalls);
    }

    [Fact]
    public async Task UnchangedOwnerReusesOneInvocationScopeAcrossRetryAndToolTurns()
    {
        var failure = new IOException("retry this scope");
        var scope = new Probe("finished")
        {
            Script = call => call == 1 ? throw failure : call == 2 ? ToolEvents("work") : TextEvents("finished")
        };
        var owner = new ScopedOwner(_ => scope);
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) => { value.Retry = ReferenceEquals(value.Error, failure); return default; });
        var executions = 0;
        var work = Tool("work", (_, _, _) => { executions++; return new ValueTask<string>("done"); });
        var marker = new object();
        await using var agent = new Agent(new() { Model = owner, Hooks = hooks, Tools = [work] });

        var result = await agent.InvokeAsync("retry then use tool", new() { InvocationState = new Dictionary<string, object?> { ["marker"] = marker } });

        Assert.Equal("finished", result.Message!.Text);
        Assert.Equal(2, result.Turns);
        Assert.Equal(1, owner.Creations);
        Assert.Same(marker, Assert.Single(owner.States)["marker"]);
        Assert.Equal(3, scope.CountRequests.Count);
        Assert.Equal(3, scope.StreamRequests.Count);
        Assert.Equal(3, scope.IteratorDisposals);
        Assert.Equal(1, executions);
        Assert.Equal(0, owner.DirectStreamCalls);
        Assert.Equal(0, scope.DisposeCalls);
    }

    [Theory]
    [InlineData(ModelSelectionTiming.BeforeModelHooks, false)]
    [InlineData(ModelSelectionTiming.BeforeModelHooks, true)]
    [InlineData(ModelSelectionTiming.AfterModelHooks, false)]
    [InlineData(ModelSelectionTiming.AfterModelHooks, true)]
    public async Task ActiveCountOrProviderKeepsOneCapturedModelWhileReplacementAffectsTheNextCall(ModelSelectionTiming timing, bool holdProvider)
    {
        var entered = Signal();
        var release = Signal();
        using var caller = new CancellationTokenSource();
        async ValueTask Hold(CancellationToken token)
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(token);
        }
        var original = new Probe("original")
        {
            OnCount = holdProvider ? null : (_, token) => Hold(token),
            OnStream = holdProvider ? Hold : null
        };
        var replacement = new Probe("replacement");
        await using var agent = new Agent(new() { Model = original, ModelSelectionTiming = timing });
        var operation = agent.InvokeAsync("held call", cancellationToken: caller.Token);
        Exception? primary = null;
        try
        {
            await Within(entered.Task);
            agent.SetModel(replacement);
            Assert.Same(replacement, agent.Model);
            Assert.False(operation.IsCompleted);
            release.TrySetResult(true);
            Assert.Equal("original", (await Within(operation)).Message!.Text);
            Assert.Single(original.CountRequests);
            Assert.Single(original.StreamRequests);
            Assert.Equal(1, original.IteratorDisposals);
            Assert.Empty(replacement.CountRequests);
            Assert.Empty(replacement.StreamRequests);
            Assert.Equal("replacement", (await agent.InvokeAsync("next call")).Message!.Text);
            Assert.Single(replacement.CountRequests);
            Assert.Single(replacement.StreamRequests);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            caller.Cancel();
            await ObserveJoined(operation, primary);
        }
    }

    [Theory]
    [InlineData(ModelSelectionTiming.BeforeModelHooks)]
    [InlineData(ModelSelectionTiming.AfterModelHooks)]
    public async Task DeniedHookDefersNewOwnerResolutionUntilTheNextAcceptedInvocation(ModelSelectionTiming timing)
    {
        var originalScope = new Probe("unused");
        var replacementScope = new Probe("accepted");
        var original = new ScopedOwner(_ => originalScope);
        var replacement = new ScopedOwner(_ => replacementScope);
        var denied = false;
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            if (!denied)
            {
                denied = true;
                value.Agent.SetModel(replacement);
                value.CancelReason = "denied before provider";
            }
            return default;
        });
        await using var agent = new Agent(new() { Model = original, Hooks = hooks, ModelSelectionTiming = timing });

        var first = await agent.InvokeAsync("deny");

        Assert.Equal("denied before provider", first.Message!.Text);
        Assert.Equal(1, original.Creations);
        Assert.Equal(0, replacement.Creations);
        Assert.Empty(originalScope.CountRequests);
        Assert.Empty(originalScope.StreamRequests);
        Assert.Equal("accepted", (await agent.InvokeAsync("accept")).Message!.Text);
        Assert.Equal(1, replacement.Creations);
        Assert.Single(replacementScope.CountRequests);
        Assert.Single(replacementScope.StreamRequests);
    }

    [Fact]
    public async Task OriginalOwnedModelCleanupStillRunsAfterAResourceFailureWithoutOwningReplacement()
    {
        var primary = new IOException("resource cleanup");
        var secondary = new InvalidOperationException("original model cleanup");
        var order = new List<string>();
        var original = new Probe("original") { OnDispose = () => { order.Add("original"); throw secondary; } };
        var replacement = new Probe("borrowed");
        var resource = new OwnedResource(() => { order.Add("resource"); throw primary; });
        var agent = new Agent(new() { Model = original, OwnsModel = true, OwnedResources = [resource] });
        agent.SetModel(replacement);

        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.DisposeAsync().AsTask()));

        Assert.Equal(new[] { "resource", "original" }, order);
        Assert.Equal(1, resource.Disposals);
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, replacement.DisposeCalls);
        Assert.Contains(secondary, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]));
    }

    [Fact]
    public async Task ChildAndParentCreateIndependentBorrowedScopesFromTheCurrentOwner()
    {
        var original = new Probe("original");
        var owner = new ScopedOwner(index => new Probe("scope-" + index));
        await using var parent = new Agent(new() { Model = original, OwnsModel = true });
        parent.SetModel(owner);
        await using (var child = parent.CreateChild())
        {
            Assert.Same(owner, child.Model);
            Assert.Equal("scope-1", (await child.InvokeAsync("child")).Message!.Text);
            Assert.Equal("scope-2", (await parent.InvokeAsync("parent")).Message!.Text);
            Assert.Equal(2, owner.Creations);
            Assert.NotSame(owner.Created[0], owner.Created[1]);
            Assert.All(owner.Created.Cast<Probe>(), model => Assert.Single(model.StreamRequests));
        }
        Assert.Equal(0, owner.DisposeCalls);
        Assert.All(owner.Created.Cast<Probe>(), model => Assert.Equal(0, model.DisposeCalls));
        Assert.Equal(0, original.DisposeCalls);
    }

    [Fact]
    public async Task StatefulToolBoundaryReplacementRetainsProviderStateAndOnlySendsPendingOutputs()
    {
        var original = new Probe("first-state", stateful: true) { Script = _ => ToolEvents("replace") };
        var replacement = new Probe("second-state", stateful: true);
        var store = new SnapshotStore();
        var effects = 0;
        var replace = Tool("replace", (_, context, _) =>
        {
            effects++;
            Assert.Equal("first-state", context.Agent.ModelState.Get<string>("responseId"));
            Assert.Equal(2, store.Latest!.ModelHistoryCount);
            var saved = store.Latest;
            context.Agent.SetModel(replacement);
            Assert.Same(saved, store.Latest);
            Assert.Equal(2, store.Latest.ModelHistoryCount);
            Assert.Null(store.Latest.ModelInputReceipt);
            return new ValueTask<string>("saved effect");
        });
        await using var agent = new Agent(new() { Model = original, Tools = [replace], SessionStore = store, SessionId = "stateful-selection" });

        var result = await agent.InvokeAsync("first input");

        Assert.Equal("second-state", result.Message!.Text);
        Assert.Equal(1, effects);
        Assert.Single(original.StreamRequests);
        var pending = Assert.Single(Assert.Single(replacement.StreamRequests).Messages);
        var output = Assert.IsType<ToolResultBlock>(Assert.Single(pending.Content));
        Assert.Equal("model-call", output.ToolUseId);
        Assert.Equal("saved effect", output.Content);
        Assert.Equal("first-state", replacement.StreamRequests[0].ModelState!["responseId"].GetString());
        Assert.Equal("second-state", agent.ModelState.Get<string>("responseId"));
        Assert.Empty(agent.Messages);
        Assert.Equal(0, store.Latest!.ModelHistoryCount);
        Assert.Null(store.Latest.ModelInputReceipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossCapabilityRejectionDoesNotChangeSelectionHistoryOrProviderState(bool originalStateful)
    {
        var original = new Probe("original", originalStateful);
        var candidate = new Probe("candidate", !originalStateful);
        var pending = Message.User("pending input");
        await using var agent = new Agent(new()
        {
            Model = original,
            Messages = [pending],
            ModelState = State("kept")
        });
        var before = agent.ModelState.GetAll();

        Assert.Throws<ArgumentException>(() => agent.SetModel(candidate));

        Assert.Same(original, agent.Model);
        Assert.Same(original, agent.AuxModel);
        Assert.Same(pending, Assert.Single(agent.Messages));
        Assert.Equal(before["responseId"].GetRawText(), agent.ModelState.GetAll()["responseId"].GetRawText());
        Assert.Empty(original.StreamRequests);
        Assert.Empty(candidate.StreamRequests);
        Assert.Equal(0, original.DisposeCalls);
        Assert.Equal(0, candidate.DisposeCalls);
    }

    [Fact]
    public async Task IncompatibleInvocationScopeFailsBeforeInputPublicationOrProviderEntry()
    {
        var invalidScope = new Probe("unused", stateful: true);
        var owner = new ScopedOwner(_ => invalidScope);
        await using var agent = new Agent(new() { Model = owner });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("not published"));

        Assert.Equal(1, owner.Creations);
        Assert.Same(owner, agent.Model);
        Assert.Empty(agent.Messages);
        Assert.Empty(invalidScope.CountRequests);
        Assert.Empty(invalidScope.StreamRequests);
        Assert.Equal(0, invalidScope.DisposeCalls);
    }

    [Theory]
    [InlineData(ModelSelectionTiming.BeforeModelHooks)]
    [InlineData(ModelSelectionTiming.AfterModelHooks)]
    public async Task HookSelectionUsesOneMatchingInvocationScopeForProjectionAndProvider(ModelSelectionTiming timing)
    {
        var first = new Probe("first");
        var second = new Probe("second");
        var original = new ScopedOwner(_ => first);
        var replacement = new ScopedOwner(_ => second);
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) => { value.Agent.SetModel(replacement); return default; });
        await using var agent = new Agent(new() { Model = original, Hooks = hooks, ModelSelectionTiming = timing });

        var result = await agent.InvokeAsync("selected by hook");

        Assert.Equal(1, original.Creations);
        var before = timing == ModelSelectionTiming.BeforeModelHooks;
        Assert.Equal(before ? "first" : "second", result.Message!.Text);
        Assert.Equal(before ? 1 : 0, first.CountRequests.Count);
        Assert.Equal(before ? 1 : 0, first.StreamRequests.Count);
        Assert.Equal(before ? 0 : 1, replacement.Creations);
        Assert.Equal(before ? 0 : 1, second.CountRequests.Count);
        Assert.Equal(before ? 0 : 1, second.StreamRequests.Count);
        Assert.Same(replacement, agent.Model);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) => await task.WaitAsync(Watchdog);
    private static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(Watchdog);
    private static async Task ObserveJoined(Task task, Exception? primary)
    {
        try { await task; }
        catch (Exception cleanup) when (primary is not null)
        {
            if (!ReferenceEquals(primary, cleanup))
                primary.Data["PrimaryModelConformance.JoinFailure"] = cleanup;
        }
    }
    private static IReadOnlyDictionary<string, JsonElement> State(string id)
        => new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement(id) };
    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> body)
        => new(new(name, "Fixture tool.", JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}")), body);
    private static ModelStreamEvent[] TextEvents(string text) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0),
        new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0),
        new ModelMessageStopEvent(StopReasons.EndTurn)
    ];
    private static ModelStreamEvent[] ToolEvents(string name) =>
    [
        new ModelMessageStartEvent(),
        new ModelContentBlockStartEvent(0, new("model-call", name, JsonSerializer.Deserialize<JsonElement>("{}"))),
        new ModelContentBlockDeltaEvent(0, ToolInput: "{}"), new ModelContentBlockStopEvent(0),
        new ModelMessageStopEvent(StopReasons.ToolUse)
    ];

    private sealed class Probe(string text, bool stateful = false) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        internal Action? ReadCapability { get; init; }
        internal Action? OnDispose { get; init; }
        internal Func<int, ModelStreamEvent[]>? Script { get; init; }
        internal Func<ModelRequest, CancellationToken, ValueTask>? OnCount { get; init; }
        internal Func<CancellationToken, ValueTask>? OnStream { get; init; }
        internal int CapabilityReads { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal int IteratorDisposals { get; private set; }
        internal List<ModelRequest> CountRequests { get; } = [];
        internal List<ModelRequest> StreamRequests { get; } = [];
        public bool Stateful { get { CapabilityReads++; ReadCapability?.Invoke(); return stateful; } }
        public async ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountRequests.Add(request);
            if (OnCount is not null) await OnCount(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return 17;
        }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamRequests.Add(request);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OnStream is not null) await OnStream(cancellationToken);
                foreach (var item in Script?.Invoke(StreamRequests.Count) ?? TextEvents(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return stateful && item is ModelMessageStopEvent stopped ? stopped with { ModelState = State(text) } : item;
                    await Task.Yield();
                }
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask DisposeAsync() { DisposeCalls++; OnDispose?.Invoke(); return default; }
    }

    private sealed class ScopedOwner(Func<int, IModel> create) : IInvocationModel, IAsyncDisposable
    {
        internal Action<IReadOnlyDictionary<string, object?>>? OnCreate { get; init; }
        internal int Creations { get; private set; }
        internal int DirectStreamCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        internal List<IModel> Created { get; } = [];
        internal List<IReadOnlyDictionary<string, object?>> States { get; } = [];
        public IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState)
        {
            Creations++;
            States.Add(invocationState);
            OnCreate?.Invoke(invocationState);
            var model = create(Creations);
            Created.Add(model);
            return model;
        }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            DirectStreamCalls++;
            throw new InvalidOperationException("The owner must resolve an invocation-local model.");
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }

    private sealed class OwnedResource(Action cleanup) : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; cleanup(); return default; }
    }

    private sealed class SnapshotStore : ISessionStore
    {
        internal SessionSnapshot? Latest { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new((SessionSnapshot?)null);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Latest = snapshot;
            return default;
        }
    }
}
