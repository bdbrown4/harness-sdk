using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class MultiAgentPluginPositiveSessionPhaseTests
{
    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task RepositoryRestoreOccupiesTheSourceInitializedTiePosition(string kind)
    {
        var seed = await SnapshotAsync(kind, SeedMode.Paused);
        var trace = new List<string>();
        var store = new RecordingStore(trace, _ => seed.Snapshot);
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<MultiAgentInitializedEvent>((value, _) =>
        {
            trace.Add("caller:initialized:" + Marker(StateOf(value.Orchestrator)));
            return default;
        });
        var plugin = new MultiAgentPlugin("observer", (_, local, _) =>
        {
            trace.Add("plugin:init:" + store.LoadCalls);
            local.Register<MultiAgentInitializedEvent>((value, _) =>
            {
                trace.Add("plugin:initialized:" + Marker(StateOf(value.Orchestrator)));
                return default;
            });
            return default;
        });
        await using var host = await CreateAsync(kind, hooks, [plugin], MultiAgentSessionRestoreProfile.PythonRepository, store);

        await host.InitializeAsync();

        Assert.Equal(kind == "graph"
            ? new[] { "plugin:init:0", "load", "caller:initialized:7", "plugin:initialized:7" }
            : new[] { "plugin:init:0", "caller:initialized:0", "load", "plugin:initialized:7" }, trace);
        Assert.Equal(7, Marker(host.State));
        Assert.Same(hooks, host.Hooks);
        Assert.Equal(1, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, host.Stats.BodyCalls);
    }

    [Theory]
    [InlineData("graph", MultiAgentSessionRestoreProfile.TypeScriptSnapshot)]
    [InlineData("swarm", MultiAgentSessionRestoreProfile.PythonSnapshot)]
    public async Task FailedLazyReadRetriesSuccessfulPausedStateWithoutPluginReinitialization(string kind, MultiAgentSessionRestoreProfile profile)
    {
        var seed = await SnapshotAsync(kind, SeedMode.Paused);
        var trace = new List<string>();
        var readFailure = new IOException("first read");
        var cause = new object();
        readFailure.Data["cause"] = cause;
        var store = new RecordingStore(trace, call => call == 1 ? throw readFailure : seed.Snapshot);
        var initializations = 0;
        var initialized = 0;
        var after = 0;
        var early = new List<MultiAgentState>();
        var late = new List<MultiAgentState>();
        var hooks = new MultiAgentHookRegistry();
        using var approval = RegisterApproval(hooks);
        hooks.Register<AfterMultiAgentInvocationEvent>((_, _) => { after++; return default; });
        var plugin = new MultiAgentPlugin("retry", (_, local, _) =>
        {
            initializations++;
            local.Register<MultiAgentInitializedEvent>((_, _) => { initialized++; return default; });
            local.Register<BeforeMultiAgentInvocationEvent>((value, _) => { early.Add(value.State); return default; });
            local.Register<BeforeMultiAgentInvocationEvent>((value, _) => { late.Add(value.State); return default; }, priority: 1);
            return default;
        });
        await using var host = await CreateAsync(kind, hooks, [plugin], profile, store);

        var error = await Assert.ThrowsAsync<IOException>(() => host.InvokeAsync(Resume(seed)));

        Assert.Same(readFailure, error);
        Assert.Same(cause, error.Data["cause"]);
        Assert.Equal(0, host.Stats.BodyCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, after);
        Assert.Single(early);
        Assert.Empty(late);

        var result = await host.InvokeAsync(Resume(seed));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(2, store.LoadCalls);
        Assert.Equal(1, initializations);
        Assert.Equal(1, initialized);
        Assert.Equal(1, after);
        Assert.Equal(2, early.Count);
        Assert.Same(early[1], Assert.Single(late));
        Assert.Same(early[1], host.State);
        Assert.Equal(7, Assert.Single(host.Stats.Bodies).Marker);
        Assert.Equal(0, host.Stats.PrefixCalls);
        Assert.Contains("original task", host.Stats.Bodies[0].Input);

        approval.Dispose();
        Assert.Equal(Status.Completed, (await host.InvokeAsync(new("next task"))).Status);
        Assert.Equal(2, store.LoadCalls);
        Assert.Equal(1, initializations);
        Assert.Equal(1, initialized);
    }

    [Theory]
    [InlineData("graph", MultiAgentSessionRestoreProfile.TypeScriptSnapshot)]
    [InlineData("swarm", MultiAgentSessionRestoreProfile.PythonSnapshot)]
    public async Task LazyRestorePublishesThePausedFrontierIntoTheActualBeforeState(string kind, MultiAgentSessionRestoreProfile profile)
    {
        var seed = await SnapshotAsync(kind, SeedMode.Paused);
        var store = new RecordingStore([], _ => seed.Snapshot);
        var hooks = new MultiAgentHookRegistry();
        using var approval = RegisterApproval(hooks);
        MultiAgentState? early = null;
        MultiAgentState? late = null;
        var observedQuestions = Array.Empty<string>();
        var observedResults = 0;
        var plugin = new MultiAgentPlugin("state-observer", (_, local, _) =>
        {
            local.Register<BeforeMultiAgentInvocationEvent>((value, _) => { early = value.State; return default; });
            local.Register<BeforeMultiAgentInvocationEvent>((value, _) =>
            {
                late = value.State;
                observedQuestions = value.State.Nodes["node"].PendingInterrupts.Select(question => question.Id).ToArray();
                observedResults = value.State.Results.Count;
                return default;
            }, priority: 1);
            return default;
        });
        await using var host = await CreateAsync(kind, hooks, [plugin], profile, store);
        await host.InitializeAsync();
        Assert.Equal(0, store.LoadCalls);

        var result = await host.InvokeAsync(Resume(seed));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Same(early, late);
        Assert.Same(early, host.State);
        Assert.Equal(seed.InterruptId, Assert.Single(observedQuestions));
        Assert.Equal(seed.ResultCount, observedResults);
        Assert.Equal(7, Marker(late));
        Assert.Equal(7, Assert.Single(host.Stats.Bodies).Marker);
        Assert.Equal(0, host.Stats.PrefixCalls);
        Assert.Equal(kind == "graph" ? 2 : 1, result.Results.Count);
        Assert.Equal(1, store.LoadCalls);
        Assert.Contains("original task", host.Stats.Bodies[0].Input);
        Assert.False(((IInterruptibleMultiAgent)host.Value).CanResume);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task ExplicitPrioritiesSurroundTheOrdinaryRepositoryManagerPosition(string kind)
    {
        var seed = await SnapshotAsync(kind, SeedMode.Paused);
        var trace = new List<string>();
        var store = new RecordingStore(trace, _ => seed.Snapshot);
        var hooks = new MultiAgentHookRegistry();
        foreach (var priority in new[] { -3, 0, 4 })
        {
            var captured = priority;
            hooks.Register<MultiAgentInitializedEvent>((value, _) =>
            {
                trace.Add("caller:" + captured + ":" + Marker(StateOf(value.Orchestrator)));
                return default;
            }, priority);
        }
        var plugin = new MultiAgentPlugin("priority", (_, local, _) =>
        {
            trace.Add("plugin:init");
            foreach (var priority in new[] { -3, 0, 4 })
            {
                var captured = priority;
                local.Register<MultiAgentInitializedEvent>((value, _) =>
                {
                    trace.Add("plugin:" + captured + ":" + Marker(StateOf(value.Orchestrator)));
                    return default;
                }, priority);
            }
            return default;
        });
        await using var host = await CreateAsync(kind, hooks, [plugin], MultiAgentSessionRestoreProfile.PythonRepository, store);

        await host.InitializeAsync();

        Assert.Equal(kind == "graph"
            ? new[] { "plugin:init", "caller:-3:0", "plugin:-3:0", "load", "caller:0:7", "plugin:0:7", "caller:4:7", "plugin:4:7" }
            : new[] { "plugin:init", "caller:-3:0", "plugin:-3:0", "caller:0:0", "load", "plugin:0:7", "caller:4:7", "plugin:4:7" }, trace);
        Assert.Equal(0, host.Stats.BodyCalls);
    }

    [Theory]
    [InlineData("graph", MultiAgentSessionRestoreProfile.TypeScriptSnapshot)]
    [InlineData("swarm", MultiAgentSessionRestoreProfile.PythonSnapshot)]
    public async Task InitializedTimeBeforeRegistrationObservesTheSelectedWiringPhase(string kind, MultiAgentSessionRestoreProfile profile)
    {
        var seed = await SnapshotAsync(kind, SeedMode.Paused);
        var trace = new List<string>();
        var store = new RecordingStore(trace, _ => seed.Snapshot);
        var hooks = new MultiAgentHookRegistry();
        using var approval = RegisterApproval(hooks);
        hooks.Register<MultiAgentInitializedEvent>((_, _) =>
        {
            hooks.Register<BeforeMultiAgentInvocationEvent>((value, _) =>
            {
                trace.Add("caller-added:" + Marker(value.State));
                return default;
            });
            return default;
        });
        var plugin = new MultiAgentPlugin("dynamic", (_, local, _) =>
        {
            local.Register<BeforeMultiAgentInvocationEvent>((value, _) => { trace.Add("early:" + Marker(value.State)); return default; });
            local.Register<MultiAgentInitializedEvent>((_, _) =>
            {
                local.Register<BeforeMultiAgentInvocationEvent>((value, _) => { trace.Add("plugin-added:" + Marker(value.State)); return default; });
                return default;
            });
            return default;
        });
        await using var host = await CreateAsync(kind, hooks, [plugin], profile, store);
        await host.InitializeAsync();
        Assert.Empty(trace);
        Assert.Equal(0, store.LoadCalls);

        var result = await host.InvokeAsync(Resume(seed));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(kind == "graph"
            ? new[] { "early:0", "load", "caller-added:7", "plugin-added:7" }
            : new[] { "early:0", "caller-added:0", "load", "plugin-added:7" }, trace);
        Assert.Equal(1, store.LoadCalls);
        Assert.Equal(1, host.Stats.BodyCalls);
        Assert.Same(hooks, host.Hooks);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task RepositoryTerminalRestoreIsAlreadyResetAtInitializedAndBefore(string kind)
    {
        // Graph is Completed; Swarm is Cancelled with no frontier. No typed
        // Swarm payload is stripped or claimed compatible by this fixture.
        var seed = await SnapshotAsync(kind, kind == "graph" ? SeedMode.Completed : SeedMode.Denied, knowledge: kind == "swarm");
        var store = new RecordingStore([], _ => seed.Snapshot);
        var hooks = new MultiAgentHookRegistry();
        var callerInitializedMarker = -1;
        var callerInitializedResults = -1;
        var callerInitializedKnowledge = -1;
        var pluginInitializedMarker = -1;
        var pluginInitializedResults = -1;
        var pluginInitializedKnowledge = -1;
        var beforeMarker = -1;
        var beforeResults = -1;
        var beforeKnowledge = -1;
        hooks.Register<MultiAgentInitializedEvent>((value, _) =>
        {
            callerInitializedMarker = Marker(StateOf(value.Orchestrator));
            callerInitializedResults = StateOf(value.Orchestrator)?.Results.Count ?? 0;
            callerInitializedKnowledge = value.Orchestrator is Swarm swarm ? swarm.SharedContext.GetAll().Count : 0;
            return default;
        });
        hooks.Register<BeforeMultiAgentInvocationEvent>((value, _) =>
        {
            beforeMarker = Marker(value.State);
            beforeResults = value.State.Results.Count;
            beforeKnowledge = value.Orchestrator is Swarm swarm ? swarm.SharedContext.GetAll().Count : 0;
            return default;
        });
        var plugin = new MultiAgentPlugin("terminal-observer", (owner, local, _) =>
        {
            if (owner is Swarm swarm) swarm.SharedContext.AddContext("plugin", "value", JsonSerializer.SerializeToElement(9));
            local.Register<MultiAgentInitializedEvent>((value, _) =>
            {
                pluginInitializedMarker = Marker(StateOf(value.Orchestrator));
                pluginInitializedResults = StateOf(value.Orchestrator)?.Results.Count ?? 0;
                pluginInitializedKnowledge = value.Orchestrator is Swarm current ? current.SharedContext.GetAll().Count : 0;
                return default;
            });
            return default;
        });
        await using var host = await CreateAsync(kind, hooks, [plugin], MultiAgentSessionRestoreProfile.PythonRepository, store);

        await host.InitializeAsync();

        Assert.Equal(0, callerInitializedMarker);
        Assert.Equal(0, callerInitializedResults);
        Assert.Equal(kind == "swarm" ? 1 : 0, callerInitializedKnowledge);
        Assert.Equal(0, pluginInitializedMarker);
        Assert.Equal(0, pluginInitializedResults);
        Assert.Equal(0, pluginInitializedKnowledge);
        Assert.Equal(0, Marker(host.State));
        Assert.Equal(0, host.State?.Results.Count ?? 0);
        if (host.Value is Swarm initializedSwarm) Assert.Empty(initializedSwarm.SharedContext.GetAll());
        Assert.False(((IInterruptibleMultiAgent)host.Value).CanResume);

        var result = await host.InvokeAsync(new("new task"));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(0, beforeMarker);
        Assert.Equal(0, beforeResults);
        Assert.Equal(0, beforeKnowledge);
        Assert.Equal(0, Assert.Single(host.Stats.Bodies).Marker);
        Assert.Equal(kind == "graph" ? 2 : 1, result.Results.Count);
        Assert.Equal(1, store.LoadCalls);
    }

    [Theory]
    [InlineData(MultiAgentSessionRestoreProfile.TypeScriptSnapshot, 7)]
    [InlineData(MultiAgentSessionRestoreProfile.PythonSnapshot, 0)]
    public async Task CompletedGraphRestoreKeepsTheSelectedBeforeVersusFreshExecutionState(MultiAgentSessionRestoreProfile profile, int bodyMarker)
    {
        var seed = await SnapshotAsync("graph", SeedMode.Completed);
        var store = new RecordingStore([], _ => seed.Snapshot);
        var hooks = new MultiAgentHookRegistry();
        var beforeMarker = -1;
        var beforeResults = -1;
        hooks.Register<BeforeMultiAgentInvocationEvent>((value, _) =>
        {
            beforeMarker = Marker(value.State);
            beforeResults = value.State.Results.Count;
            return default;
        }, priority: 1);
        await using var host = await CreateAsync("graph", hooks, [], profile, store);

        var result = await host.InvokeAsync(new("new task"));

        Assert.Equal(Status.Completed, result.Status);
        // Python load_snapshot delegates to deserialize_state, so the reset
        // occurs inside the manager, before this later Before observer.
        Assert.Equal(profile == MultiAgentSessionRestoreProfile.TypeScriptSnapshot ? 7 : 0, beforeMarker);
        Assert.Equal(profile == MultiAgentSessionRestoreProfile.TypeScriptSnapshot ? seed.ResultCount : 0, beforeResults);
        Assert.Equal(bodyMarker, Assert.Single(host.Stats.Bodies).Marker);
        Assert.Equal(1, host.Stats.PrefixCalls);
        Assert.Equal(1, store.LoadCalls);
    }

    [Fact]
    public async Task PythonSnapshotTerminalResetClearsSwarmKnowledgeBeforeLaterObserversAndBody()
    {
        var seed = await SnapshotAsync("swarm", SeedMode.Denied, knowledge: true);
        var store = new RecordingStore([], _ => seed.Snapshot);
        var hooks = new MultiAgentHookRegistry();
        var earlyKnowledge = -1;
        var lateKnowledge = -1;
        var bodyKnowledge = -1;
        var plugin = new MultiAgentPlugin("knowledge-observer", (owner, local, _) =>
        {
            var swarm = Assert.IsType<Swarm>(owner);
            swarm.SharedContext.AddContext("plugin", "value", JsonSerializer.SerializeToElement(9));
            local.Register<BeforeMultiAgentInvocationEvent>((_, _) => { earlyKnowledge = swarm.SharedContext.GetAll().Count; return default; });
            local.Register<BeforeMultiAgentInvocationEvent>((_, _) => { lateKnowledge = swarm.SharedContext.GetAll().Count; return default; }, priority: 1);
            local.Register<BeforeNodeCallEvent>((_, _) => { bodyKnowledge = swarm.SharedContext.GetAll().Count; return default; });
            return default;
        });
        await using var host = await CreateAsync("swarm", hooks, [plugin], MultiAgentSessionRestoreProfile.PythonSnapshot, store);

        var result = await host.InvokeAsync(new("new task"));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, earlyKnowledge);
        Assert.Equal(0, lateKnowledge);
        Assert.Equal(0, bodyKnowledge);
        Assert.Empty(Assert.IsType<Swarm>(host.Value).SharedContext.GetAll());
        Assert.Equal(1, host.Stats.BodyCalls);
        Assert.Equal(1, store.LoadCalls);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public void DefaultPhaseOptionsMatchTheParentWireShapeByteForByte(string kind)
    {
        string actual;
        string oldShape;
        string explicitNative;
        if (kind == "graph")
        {
            var options = new GraphOptions();
            actual = JsonSerializer.Serialize(options);
            oldShape = JsonSerializer.Serialize(new
            {
                options.AgentContext,
                options.Hooks,
                options.Telemetry,
                options.Id,
                options.SessionId,
                options.SessionStore,
                options.SaveLatestOn,
                options.RestoredInput,
                options.Nodes,
                options.Edges,
                options.Sources,
                options.MaxConcurrency,
                options.MaxSteps,
                options.Timeout,
                options.NodeTimeout,
                options.Dependencies,
                options.Scheduling,
                options.NodeFailure,
                options.LimitOutcome,
                options.InputFormat
            });
            explicitNative = JsonSerializer.Serialize(options with { SessionRestoreProfile = MultiAgentSessionRestoreProfile.Native });
        }
        else
        {
            var options = new SwarmOptions();
            actual = JsonSerializer.Serialize(options);
            oldShape = JsonSerializer.Serialize(new
            {
                options.Id,
                options.Nodes,
                options.Start,
                options.Hooks,
                options.Telemetry,
                options.SessionId,
                options.SessionStore,
                options.SaveLatestOn,
                options.RestoredInput,
                options.AgentContext,
                options.MaxSteps,
                options.MaxHandoffs,
                options.LimitOutcome,
                options.FailurePolicy,
                options.RepetitiveHandoffDetectionWindow,
                options.RepetitiveHandoffMinUniqueAgents,
                options.RepetitionScope,
                options.Timeout,
                options.TimeoutScope,
                options.NodeTimeout,
                options.Routing,
                options.ContextCommit
            });
            explicitNative = JsonSerializer.Serialize(options with
            {
                SessionRestoreProfile = MultiAgentSessionRestoreProfile.Native,
                BaselineCapturePhase = SwarmBaselineCapturePhase.BeforePlugins
            });
        }

        Assert.Equal(Encoding.UTF8.GetBytes(oldShape), Encoding.UTF8.GetBytes(actual));
        Assert.Equal(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(explicitNative));
        using var document = JsonDocument.Parse(actual);
        Assert.False(document.RootElement.TryGetProperty("SessionRestoreProfile", out _));
        Assert.False(document.RootElement.TryGetProperty("BaselineCapturePhase", out _));
    }

    [Theory]
    [InlineData("graph-session")]
    [InlineData("swarm-session")]
    [InlineData("swarm-baseline")]
    public async Task UndefinedPhaseFlagsRejectBeforeBorrowedEffects(string route)
    {
        var store = new RecordingStore([], _ => null);
        var hooks = new MultiAgentHookRegistry();
        var effects = 0;
        hooks.Register<MultiAgentInitializedEvent>((_, _) => { effects++; return default; });
        var plugin = new MultiAgentPlugin("effect", (_, _, _) => { effects++; return default; });
        if (route == "graph-session")
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Graph(new()
            {
                Nodes = [new FunctionNode("node", (_, _, _) => Output(() => effects++))],
                Hooks = hooks,
                Plugins = [plugin],
                SessionId = "phase",
                SessionStore = store,
                SessionRestoreProfile = (MultiAgentSessionRestoreProfile)123456
            }));
        }
        else
        {
            var stats = new Stats();
            await using var agent = new Agent(new() { Id = "node", Model = new AnswerModel(_ => { effects++; stats.BodyCalls++; }) });
            Assert.Throws<ArgumentOutOfRangeException>(() => new Swarm(new()
            {
                Nodes = [new(agent)],
                Hooks = hooks,
                Plugins = [plugin],
                SessionId = "phase",
                SessionStore = store,
                SessionRestoreProfile = route == "swarm-session" ? (MultiAgentSessionRestoreProfile)123456 : MultiAgentSessionRestoreProfile.Native,
                BaselineCapturePhase = route == "swarm-baseline" ? (SwarmBaselineCapturePhase)123456 : SwarmBaselineCapturePhase.BeforePlugins
            }));
            Assert.Equal(0, stats.BodyCalls);
        }
        Assert.Equal(0, effects);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    private enum SeedMode { Paused, Completed, Denied }
    private sealed record Seed(MultiAgentSnapshot Snapshot, string? InterruptId, int ResultCount);
    private sealed record Body(string Input, int Marker);
    private sealed class Stats
    {
        public int PrefixCalls;
        public int BodyCalls;
        public List<Body> Bodies { get; } = [];
        public void Enter(string input, MultiAgentState state)
        {
            BodyCalls++;
            Bodies.Add(new(input, Marker(state)));
        }
    }

    private static int Marker(MultiAgentState? state) => state?.App.Get<int>("loaded") ?? 0;
    private static MultiAgentState? StateOf(IMultiAgent value) => value is Graph graph ? graph.State : ((Swarm)value).State;
    private static MultiAgentInput Resume(Seed seed) => MultiAgentInput.Resume([new(seed.InterruptId!, JsonSerializer.SerializeToElement(true))]);
    private static IDisposable RegisterApproval(MultiAgentHookRegistry hooks) => hooks.Register<BeforeNodeCallEvent>((value, _) =>
    {
        if (value.NodeId == "node") Assert.True(value.Interrupt("approval").GetBoolean());
        return default;
    });

    private static async Task<Seed> SnapshotAsync(string kind, SeedMode mode, bool knowledge = false)
    {
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeMultiAgentInvocationEvent>((value, _) => { value.State.App.Set("loaded", 7); return default; });
        IDisposable? approval = mode == SeedMode.Paused ? RegisterApproval(hooks) : null;
        using (approval)
        {
            if (mode == SeedMode.Denied)
                hooks.Register<BeforeNodeCallEvent>((value, _) => { value.CancelReason = "terminal no-frontier source"; return default; });
            await using var host = await CreateAsync(kind, hooks, [], MultiAgentSessionRestoreProfile.Native, null);
            if (knowledge) Assert.IsType<Swarm>(host.Value).SharedContext.AddContext("stored", "value", JsonSerializer.SerializeToElement(7));
            var result = await host.InvokeAsync(new("original task"));
            Assert.Equal(mode == SeedMode.Paused ? Status.Interrupted : mode == SeedMode.Denied ? Status.Cancelled : Status.Completed, result.Status);
            Assert.Equal(7, Marker(host.State));
            Assert.Equal(mode == SeedMode.Paused, ((IInterruptibleMultiAgent)host.Value).CanResume);
            Assert.Equal(mode == SeedMode.Completed ? 1 : 0, host.Stats.BodyCalls);
            var question = mode == SeedMode.Paused ? Assert.Single(result.Interrupts).Id : null;
            var snapshot = await ((ICheckpointableMultiAgent)host.Value).TakeSnapshotAsync();
            var detached = JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(snapshot));
            Assert.NotNull(detached);
            return new(detached, question, result.Results.Count);
        }
    }

    private static async Task<Host> CreateAsync(string kind, MultiAgentHookRegistry hooks, IReadOnlyList<MultiAgentPlugin> plugins,
        MultiAgentSessionRestoreProfile profile, RecordingStore? store)
    {
        var stats = new Stats();
        if (kind == "graph")
        {
            var graph = new Graph(new()
            {
                Id = "graph-phase",
                Nodes = [new FunctionNode("prefix", (_, _, _) => Output(() => stats.PrefixCalls++)),
                    new FunctionNode("node", (input, context, _) => Output(() => stats.Enter(input.Text, context.State)))],
                Edges = [new("prefix", "node")],
                Hooks = hooks,
                Plugins = plugins,
                SessionId = store is null ? null : "phase",
                SessionStore = store,
                SessionRestoreProfile = profile
            });
            return new(graph, null, stats);
        }
        Assert.Equal("swarm", kind);
        Swarm? swarm = null;
        var agent = new Agent(new() { Id = "node", Model = new AnswerModel(request => stats.Enter(request.Messages[0].Text, swarm!.State!)) });
        try
        {
            swarm = new(new()
            {
                Id = "swarm-phase",
                Nodes = [new(agent)],
                Routing = SwarmRoutingMode.Tool,
                AgentContext = AgentContextPolicy.Retain,
                Hooks = hooks,
                Plugins = plugins,
                SessionId = store is null ? null : "phase",
                SessionStore = store,
                SessionRestoreProfile = profile
            });
            return new(swarm, agent, stats);
        }
        catch { await agent.DisposeAsync(); throw; }
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(Action enter)
    {
        enter();
        yield return new NodeResultUpdateEvent([new TextBlock("done")]);
        await Task.CompletedTask;
    }

    private sealed class Host(IMultiAgent value, Agent? agent, Stats stats) : IAsyncDisposable
    {
        public IMultiAgent Value => value;
        public Stats Stats => stats;
        public MultiAgentState? State => StateOf(value);
        public MultiAgentHookRegistry Hooks => value is Graph graph ? graph.Hooks : ((Swarm)value).Hooks;
        public Task InitializeAsync() => value is Graph graph ? graph.InitializeAsync() : ((Swarm)value).InitializeAsync();
        public Task<MultiAgentResult> InvokeAsync(MultiAgentInput input) => value.InvokeAsync(input);
        public ValueTask DisposeAsync() => agent?.DisposeAsync() ?? default;
    }

    private sealed class RecordingStore(List<string> trace, Func<int, MultiAgentSnapshot?> read) : IMultiAgentSessionStore
    {
        public int LoadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("phase", sessionId);
            trace.Add("load");
            return new(read(++LoadCalls));
        }
        public ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("phase", sessionId);
            Assert.NotNull(snapshot);
            SaveCalls++;
            return default;
        }
    }

    private sealed class AnswerModel(Action<ModelRequest> enter) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            enter(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
