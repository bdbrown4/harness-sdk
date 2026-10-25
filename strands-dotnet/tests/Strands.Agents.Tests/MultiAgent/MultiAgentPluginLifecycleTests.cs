using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class MultiAgentPluginLifecycleTests
{
    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task AsyncInitializationIsAwaitedOnceBeforeInitializedCallbacksAndBodies(string kind)
    {
        var entered = Gate();
        var release = Gate();
        var order = new List<string>();
        var initializations = 0;
        var initialized = 0;
        var before = 0;
        var plugin = Declaration("ordered", async (owner, hooks, token) =>
        {
            initializations++;
            order.Add("plugin-enter");
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            hooks.Register<MultiAgentInitializedEvent>((value, _) =>
            {
                Assert.Same(owner, value.Orchestrator);
                initialized++;
                order.Add("initialized");
                return default;
            });
            hooks.Register<BeforeMultiAgentInvocationEvent>((value, _) =>
            {
                Assert.Same(owner, value.Orchestrator);
                before++;
                order.Add("before");
                return default;
            });
            order.Add("plugin-ready");
        });
        await using var host = await Host.CreateAsync(kind, Declarations(plugin), new(), () => order.Add("body"));
        var initialization = host.InitializeAsync();
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(initialization.IsCompleted);
            Assert.Equal(0, initialized);
            Assert.Equal(0, host.BodyCalls);
            release.TrySetResult(true);
            await initialization;
            await host.InitializeAsync();

            Assert.Equal("done", (await host.InvokeAsync()).ToString());
            Assert.Equal("done", (await host.InvokeAsync()).ToString());

            Assert.Equal(1, initializations);
            Assert.Equal(1, initialized);
            Assert.Equal(2, before);
            Assert.Equal(2, host.BodyCalls);
            Assert.Equal(new[] { "plugin-enter", "plugin-ready", "initialized", "before", "body", "before", "body" }, order);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            await ObserveJoined(initialization, primary);
        }
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task CopiedDeclarationsBindLocalCallbacksWhileCallerHooksRemainLive(string kind)
    {
        using var callbacks = new BorrowedCallbacks();
        var plugin = Declaration(kind == "graph" ? "" : " ", callbacks.InitializeAsync);
        var declarations = Declarations(plugin);
        var callerHooks = new MultiAgentHookRegistry();
        await using var first = await Host.CreateAsync(kind, declarations, callerHooks);
        await using var second = await Host.CreateAsync(kind, declarations, callerHooks);
        var never = 0;
        declarations.Clear();
        declarations.Add(Declaration("late", (_, _, _) => { never++; return default; }));
        Assert.Same(plugin, Assert.Single(first.CopiedDeclarations));
        Assert.Same(plugin, Assert.Single(second.CopiedDeclarations));
        Assert.Same(callerHooks, first.CallerHooks);
        Assert.Same(callerHooks, second.CallerHooks);

        await first.InitializeAsync();
        await second.InitializeAsync();
        var outside = new List<IMultiAgent>();
        using var subscription = callerHooks.Register<BeforeMultiAgentInvocationEvent>((value, _) =>
        {
            outside.Add(value.Orchestrator);
            return default;
        });
        await first.InvokeAsync();
        await second.InvokeAsync();
        await first.InvokeAsync();

        Assert.Equal(0, never);
        Assert.Equal(2, callbacks.Initializations.Count);
        Assert.Same(first.Value, callbacks.Initializations[0]);
        Assert.Same(second.Value, callbacks.Initializations[1]);
        Assert.Equal(2, callbacks.Calls[first.Value]);
        Assert.Equal(1, callbacks.Calls[second.Value]);
        Assert.Equal(new[] { first.Value, second.Value, first.Value }, outside);
        Assert.Equal(0, callbacks.Disposals);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task DuplicateNamesKeepTheEarlierInitializationPrefixAndCacheTheFailure(string kind)
    {
        var prefix = new List<string>();
        var initialized = 0;
        var first = Declaration("same", (_, hooks, _) =>
        {
            prefix.Add("first");
            hooks.Register<MultiAgentInitializedEvent>((_, _) => { initialized++; return default; });
            return default;
        });
        var duplicate = Declaration("same", (_, _, _) => { prefix.Add("duplicate"); return default; });
        var later = Declaration("later", (_, _, _) => { prefix.Add("later"); return default; });
        await using var host = await Host.CreateAsync(kind, Declarations(first, duplicate, later), new());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => host.InitializeAsync());
        Assert.Same(error, await Assert.ThrowsAsync<ArgumentException>(() => host.InitializeAsync()));
        Assert.Same(error, await Assert.ThrowsAsync<ArgumentException>(() => host.InvokeAsync()));

        Assert.Equal(new[] { "first" }, prefix);
        Assert.Equal(0, initialized);
        Assert.Equal(0, host.BodyCalls);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task FailedAsyncInitializationIsJoinedAndRetainsItsOriginalCachedCause(string kind)
    {
        var entered = Gate();
        var release = Gate();
        var failure = new IOException("plugin initialization failed");
        var attempts = 0;
        var later = 0;
        var initialized = 0;
        var plugin = Declaration("failure", async (_, hooks, _) =>
        {
            attempts++;
            hooks.Register<MultiAgentInitializedEvent>((_, _) => { initialized++; return default; });
            entered.TrySetResult(true);
            await release.Task.ConfigureAwait(false);
            throw failure;
        });
        var following = Declaration("following", (_, _, _) => { later++; return default; });
        await using var host = await Host.CreateAsync(kind, Declarations(plugin, following), new());
        var initialization = host.InitializeAsync();
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(initialization.IsCompleted);
            Assert.Equal(0, host.BodyCalls);
            release.TrySetResult(true);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => initialization));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => host.InitializeAsync()));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => host.InvokeAsync()));
            Assert.Equal(1, attempts);
            Assert.Equal(0, later);
            Assert.Equal(0, initialized);
            Assert.Equal(0, host.BodyCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            await ObserveJoined(initialization, primary, failure);
        }
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task NoPluginOptionsPreserveExistingHooksAndRepeatedInvocation(string kind)
    {
        var callerHooks = new MultiAgentHookRegistry();
        var initialized = 0;
        var before = 0;
        callerHooks.Register<MultiAgentInitializedEvent>((_, _) => { initialized++; return default; });
        callerHooks.Register<BeforeMultiAgentInvocationEvent>((_, _) => { before++; return default; });
        await using var host = await Host.CreateAsync(kind, null, callerHooks);
        Assert.Same(callerHooks, host.CallerHooks);

        await host.InitializeAsync();
        await host.InitializeAsync();
        Assert.Equal("done", (await host.InvokeAsync()).ToString());
        Assert.Equal("done", (await host.InvokeAsync()).ToString());

        Assert.Equal(1, initialized);
        Assert.Equal(2, before);
        Assert.Equal(2, host.BodyCalls);
    }

    // Reflection is limited to the proposed additive declaration and option property.
    // All initialization, invocation, callbacks and fake bodies use existing public APIs.
    private static Type DeclarationType()
    {
        var type = typeof(Graph).Assembly.GetType("Strands.Agents.MultiAgent.MultiAgentPlugin", throwOnError: false);
        Assert.NotNull(type);
        Assert.True(type.IsPublic && type.IsSealed);
        return type;
    }

    private static object Declaration(string name, Func<IMultiAgent, MultiAgentHookRegistry, CancellationToken, ValueTask> initialize)
    {
        var constructor = DeclarationType().GetConstructor(new[]
        {
            typeof(string), typeof(Func<IMultiAgent, MultiAgentHookRegistry, CancellationToken, ValueTask>)
        });
        Assert.NotNull(constructor);
        var result = constructor.Invoke(new object[] { name, initialize });
        Assert.Equal(name, DeclarationType().GetProperty("Name")!.GetValue(result));
        return result;
    }

    private static IList Declarations(params object[] values)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(DeclarationType()))!;
        foreach (var value in values) list.Add(value);
        return list;
    }

    private static void Attach(object options, IList declarations)
    {
        var property = options.GetType().GetProperty("Plugins", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlyList<>).MakeGenericType(DeclarationType()), property.PropertyType);
        property.SetValue(options, declarations);
    }

    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveJoined(Task task, Exception? primary, Exception? expected = null)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (ReferenceEquals(error, expected)) { }
        catch (Exception error) when (primary is not null)
        {
            if (!ReferenceEquals(error, primary)) primary.Data["HL01.JoinError"] = error;
        }
    }

    private sealed class BorrowedCallbacks : IDisposable
    {
        public List<IMultiAgent> Initializations { get; } = [];
        public Dictionary<IMultiAgent, int> Calls { get; } = [];
        public int Disposals { get; private set; }
        public ValueTask InitializeAsync(IMultiAgent owner, MultiAgentHookRegistry hooks, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Initializations.Add(owner);
            Calls.Add(owner, 0);
            hooks.Register<BeforeMultiAgentInvocationEvent>((value, _) =>
            {
                Assert.Same(owner, value.Orchestrator);
                Calls[owner]++;
                return default;
            });
            return default;
        }
        public void Dispose() => Disposals++;
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly Graph? _graph;
        private readonly Swarm? _swarm;
        private readonly Agent? _agent;
        private readonly Counter _counter;
        private Host(Graph graph, Counter counter) { _graph = graph; _counter = counter; Value = graph; }
        private Host(Swarm swarm, Agent agent, Counter counter) { _swarm = swarm; _agent = agent; _counter = counter; Value = swarm; }
        public IMultiAgent Value { get; }
        public int BodyCalls => _counter.Calls;
        public MultiAgentHookRegistry CallerHooks => _graph?.Hooks ?? _swarm!.Hooks;
        public IEnumerable<object> CopiedDeclarations
        {
            get
            {
                object options = _graph is { } graph ? graph.Options : _swarm!.Options;
                return ((IEnumerable)options.GetType().GetProperty("Plugins")!.GetValue(options)!).Cast<object>();
            }
        }
        public Task InitializeAsync() => _graph is { } graph ? graph.InitializeAsync() : _swarm!.InitializeAsync();
        public Task<MultiAgentResult> InvokeAsync() => Value.InvokeAsync(new MultiAgentInput("task"));
        public static async Task<Host> CreateAsync(string kind, IList? declarations, MultiAgentHookRegistry hooks, Action? onBody = null)
        {
            var counter = new Counter(onBody);
            if (kind == "graph")
            {
                var options = new GraphOptions { Nodes = [new FunctionNode("node", (_, _, _) => Output(counter))], Hooks = hooks };
                if (declarations is not null) Attach(options, declarations);
                return new(new Graph(options), counter);
            }
            Assert.Equal("swarm", kind);
            var agent = new Agent(new() { Id = "node", Model = new AnswerModel(counter) });
            try
            {
                var options = new SwarmOptions { Nodes = [new(agent)], Routing = SwarmRoutingMode.Tool, AgentContext = AgentContextPolicy.Retain, Hooks = hooks };
                if (declarations is not null) Attach(options, declarations);
                return new(new Swarm(options), agent, counter);
            }
            catch
            {
                await agent.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_agent is not null) await _agent.DisposeAsync().ConfigureAwait(false);
        }
        private static async IAsyncEnumerable<INodeStreamEvent> Output(Counter counter)
        {
            counter.Enter();
            yield return new NodeResultUpdateEvent([new TextBlock("done")]);
            await Task.CompletedTask;
        }
    }

    private sealed class Counter(Action? onBody)
    {
        public int Calls { get; private set; }
        public void Enter() { Calls++; onBody?.Invoke(); }
    }

    private sealed class AnswerModel(Counter counter) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counter.Enter();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
