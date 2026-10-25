using System.Reflection;
using System.Runtime.CompilerServices;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

// Test proposal only. Reflection is limited to the two absent additive policy properties.
// The policy/API proposal must be reviewed before any actual RED/scaffold stage.
public sealed class MultiAgentPluginSessionPhaseTests
{
    [Fact]
    public async Task GraphBuilderSnapshotSessionLeavesReadsUntilFirstInvocation()
    {
        var trace = new List<string>();
        var store = new CountingStore(trace);
        var bodyCalls = 0;
        var plugin = ObservingPlugin(trace, store);
        var options = new GraphOptions { SessionId = "phase", SessionStore = store };
        SelectSession(options, "TypeScriptSnapshot");
        var builder = new GraphBuilder(options).Configure(value => value with { Plugins = [plugin] });
        builder.AddNode(new FunctionNode("node", (_, _, _) => Output(() =>
        {
            bodyCalls++;
            trace.Add("body:" + store.LoadCalls);
        })));
        var graph = builder.Build();

        await graph.InitializeAsync();

        Assert.Equal(new[] { "plugin:init:0", "plugin:initialized:0" }, trace);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, bodyCalls);

        var first = await graph.InvokeAsync("first");
        var second = await graph.InvokeAsync("second");

        Assert.Equal(new[]
        {
            "plugin:init:0", "plugin:initialized:0", "plugin:before:0", "load", "body:1",
            "plugin:before:1", "body:1"
        }, trace);
        Assert.Equal(1, store.LoadCalls);
        Assert.Equal(2, bodyCalls);
        Assert.Equal(Status.Completed, first.Status);
        Assert.Equal(Status.Completed, second.Status);
    }

    [Fact]
    public async Task SwarmCreateAsyncSnapshotSessionLeavesReadsUntilFirstInvocation()
    {
        var trace = new List<string>();
        var store = new CountingStore(trace);
        var model = new AnswerModel(() => trace.Add("body:" + store.LoadCalls));
        await using var agent = new Agent(new() { Id = "node", Model = model });
        var options = new SwarmOptions
        {
            Nodes = [new(agent)],
            Routing = SwarmRoutingMode.Tool,
            AgentContext = AgentContextPolicy.Retain,
            SessionId = "phase",
            SessionStore = store,
            Plugins = [ObservingPlugin(trace, store)]
        };
        SelectSession(options, "PythonSnapshot");

        var swarm = await Swarm.CreateAsync(options);

        Assert.Equal(new[] { "plugin:init:0", "plugin:initialized:0" }, trace);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, model.Calls);

        var result = await swarm.InvokeAsync("task");

        Assert.Equal(new[] { "plugin:init:0", "plugin:initialized:0", "plugin:before:0", "load", "body:1" }, trace);
        Assert.Equal(1, store.LoadCalls);
        Assert.Equal(1, model.Calls);
        Assert.Equal(Status.Completed, result.Status);
    }

    [Theory]
    [InlineData("graph", "TypeScriptSnapshot")]
    [InlineData("swarm", "PythonRepository")]
    public async Task ThrowingUserPluginPreventsSessionReadAndCachesItsOwnFailure(string kind, string profile)
    {
        var trace = new List<string>();
        var readFailure = new IOException("session read must not happen");
        var pluginFailure = new InvalidOperationException("user plugin is first");
        var store = new CountingStore(trace, readFailure);
        var laterPlugins = 0;
        var initialized = 0;
        var bodyCalls = 0;
        IMultiAgent? capturedOwner = null;
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<MultiAgentInitializedEvent>((_, _) => { initialized++; return default; });
        var first = new MultiAgentPlugin("first", (owner, _, _) =>
        {
            capturedOwner = owner;
            throw pluginFailure;
        });
        var later = new MultiAgentPlugin("later", (_, _, _) => { laterPlugins++; return default; });

        if (kind == "graph")
        {
            var options = new GraphOptions
            {
                Nodes = [new FunctionNode("node", (_, _, _) => Output(() => bodyCalls++))],
                SessionId = "phase",
                SessionStore = store,
                Hooks = hooks,
                Plugins = [first, later]
            };
            SelectSession(options, profile);
            var graph = new Graph(options);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.InitializeAsync());
            Assert.Same(pluginFailure, error);
            Assert.Same(graph, capturedOwner);
            Assert.Same(pluginFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => graph.InitializeAsync()));
        }
        else
        {
            Assert.Equal("swarm", kind);
            await using var agent = new Agent(new() { Id = "node", Model = new AnswerModel(() => bodyCalls++) });
            var options = new SwarmOptions
            {
                Nodes = [new(agent)],
                Routing = SwarmRoutingMode.Tool,
                AgentContext = AgentContextPolicy.Retain,
                SessionId = "phase",
                SessionStore = store,
                Hooks = hooks,
                Plugins = [first, later]
            };
            SelectSession(options, profile);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Swarm.CreateAsync(options));
            Assert.Same(pluginFailure, error);
            // A public initializer can retain its actual owner even when CreateAsync fails.
            var swarm = Assert.IsType<Swarm>(capturedOwner);
            Assert.Same(pluginFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => swarm.InitializeAsync()));
        }

        Assert.Empty(trace);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, laterPlugins);
        Assert.Equal(0, initialized);
        Assert.Equal(0, bodyCalls);
    }

    [Fact]
    public async Task PythonSwarmPluginMutationBecomesTheResetBaseline()
    {
        var observed = new List<int>();
        Agent? borrowed = null;
        var model = new AnswerModel(() =>
        {
            observed.Add(borrowed!.State.Get<int>("counter"));
            borrowed.State.Set("counter", 19);
        });
        await using var agent = new Agent(new() { Id = "node", Model = model });
        borrowed = agent;
        agent.State.Set("counter", 7);
        var initializations = 0;
        var plugin = new MultiAgentPlugin("baseline", (_, _, _) =>
        {
            initializations++;
            agent.State.Set("counter", 11);
            return default;
        });
        var options = SwarmOptions.Python with { Nodes = [new(agent)], Plugins = [plugin] };
        Select(options, "BaselineCapturePhase", "SwarmBaselineCapturePhase", "AfterPlugins");

        var swarm = await Swarm.CreateAsync(options);
        agent.State.Set("counter", 99);
        var first = await swarm.InvokeAsync("first");
        var second = await swarm.InvokeAsync("second");

        Assert.Equal(new[] { 11, 11 }, observed);
        Assert.Equal(19, agent.State.Get<int>("counter"));
        Assert.Equal(1, initializations);
        Assert.Equal(2, model.Calls);
        Assert.Equal(Status.Completed, first.Status);
        Assert.Equal(Status.Completed, second.Status);
    }

    private static MultiAgentPlugin ObservingPlugin(List<string> trace, CountingStore store) =>
        new("observer", (_, local, _) =>
        {
            trace.Add("plugin:init:" + store.LoadCalls);
            local.Register<MultiAgentInitializedEvent>((_, _) =>
            {
                trace.Add("plugin:initialized:" + store.LoadCalls);
                return default;
            });
            local.Register<BeforeMultiAgentInvocationEvent>((_, _) =>
            {
                trace.Add("plugin:before:" + store.LoadCalls);
                return default;
            });
            return default;
        });

    private static void SelectSession(object options, string profile) =>
        Select(options, "SessionRestoreProfile", "MultiAgentSessionRestoreProfile", profile);

    private static void Select(object options, string propertyName, string enumName, string value)
    {
        var enumType = typeof(Graph).Assembly.GetType("Strands.Agents.MultiAgent." + enumName);
        Assert.NotNull(enumType);
        Assert.True(enumType.IsEnum);
        var property = options.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal(enumType, property.PropertyType);
        Assert.True(property.CanWrite);
        property.SetValue(options, Enum.Parse(enumType, value, ignoreCase: false));
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(Action onBody)
    {
        onBody();
        yield return new NodeResultUpdateEvent([new TextBlock("done")]);
        await Task.CompletedTask;
    }

    private sealed class CountingStore(List<string> trace, Exception? readFailure = null) : IMultiAgentSessionStore
    {
        public int LoadCalls { get; private set; }
        public ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("phase", sessionId);
            LoadCalls++;
            trace.Add("load");
            if (readFailure is not null) throw readFailure;
            return new((MultiAgentSnapshot?)null);
        }
        public ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("phase", sessionId);
            Assert.NotNull(snapshot);
            return default;
        }
    }

    private sealed class AnswerModel(Action onBody) : IModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            onBody();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
