using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class MultiAgentPluginCompositionTests
{
    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task MixedPrioritiesAndInterleavedCallerLocalTiesKeepOneChronology(string kind)
    {
        var order = new List<string>();
        var caller = new MultiAgentHookRegistry();
        Pair(caller, "caller-a", 0);
        Pair(caller, "low", -10);
        var plugin = new MultiAgentPlugin("ordered", (owner, local, _) =>
        {
            Pair(local, "local-b", 0);
            Pair(Caller(owner), "caller-c", 0);
            Pair(local, "local-d", 0);
            return default;
        });
        await using var host = await CreateAsync(kind, caller, [plugin], () => order.Add("body"));
        await host.InitializeAsync();
        Pair(caller, "caller-e", 0);

        await host.InvokeAsync();

        Assert.Equal(new[]
        {
            "before-low", "before-caller-a", "before-local-b", "before-caller-c", "before-local-d", "before-caller-e",
            "body",
            "after-low", "after-caller-e", "after-local-d", "after-caller-c", "after-local-b", "after-caller-a"
        }, order);

        void Pair(MultiAgentHookRegistry hooks, string label, int priority)
        {
            hooks.Register<BeforeMultiAgentInvocationEvent>((_, _) => { order.Add("before-" + label); return default; }, priority);
            hooks.Register<AfterMultiAgentInvocationEvent>((_, _) => { order.Add("after-" + label); return default; }, priority);
        }
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task CapturedMembershipSurvivesRemovalAndNewRegistrationsWaitForTheNextEvent(string kind)
    {
        var order = new List<string>();
        var caller = new MultiAgentHookRegistry();
        IDisposable? localHandle = null;
        IDisposable? lateHandle = null;
        Task? mutation = null;
        var changed = false;
        caller.Register<BeforeMultiAgentInvocationEvent>(async (_, _) =>
        {
            order.Add("caller");
            if (changed) return;
            changed = true;
            // A callback holding the registry's monitor would block this distinct thread.
            // The watchdog fails that assertion; finally below still joins the original task.
            mutation = Task.Run(() =>
            {
                localHandle!.Dispose();
                lateHandle = caller.Register<BeforeMultiAgentInvocationEvent>((_, _) => { order.Add("late"); return default; });
            });
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
        });
        var plugin = new MultiAgentPlugin("snapshot", (_, local, _) =>
        {
            localHandle = local.Register<BeforeMultiAgentInvocationEvent>((_, _) => { order.Add("captured-local"); return default; });
            return default;
        });
        await using var host = await CreateAsync(kind, caller, [plugin], () => order.Add("body"));
        Exception? primary = null;
        try
        {
            await host.InvokeAsync();
            await host.InvokeAsync();
            Assert.Equal(new[] { "caller", "captured-local", "body", "caller", "late", "body" }, order);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            if (mutation is not null) await Join(mutation, primary);
            localHandle?.Dispose();
            lateHandle?.Dispose();
        }
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task SharedCallerAndLocalHandlesRemoveOnlyTheirOwnMembership(string kind)
    {
        var caller = new MultiAgentHookRegistry();
        var outside = new List<IMultiAgent>();
        using var shared = caller.Register<BeforeMultiAgentInvocationEvent>((value, _) => { outside.Add(value.Orchestrator); return default; });
        var locals = new Dictionary<IMultiAgent, List<IMultiAgent>>();
        var handles = new Dictionary<IMultiAgent, IDisposable>();
        var plugin = new MultiAgentPlugin("reused", (owner, local, _) =>
        {
            locals.Add(owner, []);
            handles.Add(owner, local.Register<BeforeMultiAgentInvocationEvent>((value, _) => { locals[owner].Add(value.Orchestrator); return default; }));
            return default;
        });
        await using var first = await CreateAsync(kind, caller, [plugin]);
        await using var second = await CreateAsync(kind, caller, [plugin]);
        await first.InvokeAsync();
        await second.InvokeAsync();
        handles[first.Value].Dispose();
        shared.Dispose();
        await first.InvokeAsync();
        await second.InvokeAsync();

        Assert.Equal(new[] { first.Value, second.Value }, outside);
        Assert.Equal(new[] { first.Value }, locals[first.Value]);
        Assert.Equal(new[] { second.Value, second.Value }, locals[second.Value]);
        Assert.Same(caller, Caller(first.Value));
        Assert.Same(caller, Caller(second.Value));
        handles[second.Value].Dispose();
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task ExistingAdmissionRejectsInitializationAndCallbackReentryWithoutStartingABody(string kind)
    {
        var initializerReentries = 0;
        var callbackReentries = 0;
        var plugin = new MultiAgentPlugin("reentry", async (owner, local, _) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.InvokeAsync(new MultiAgentInput("nested-init")));
            initializerReentries++;
            local.Register<BeforeMultiAgentInvocationEvent>(async (value, _) =>
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => value.Orchestrator.InvokeAsync(new MultiAgentInput("nested-callback")));
                callbackReentries++;
            });
        });
        await using var host = await CreateAsync(kind, new(), [plugin]);

        await host.InvokeAsync();

        Assert.Equal(1, initializerReentries);
        Assert.Equal(1, callbackReentries);
        Assert.Equal(1, host.BodyCalls);
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task CancellationAfterAnInitializerReturnsPreventsInitializedAndCachesTheCanceledAttempt(string kind)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var initialized = 0;
        var plugin = new MultiAgentPlugin("cancellation", async (_, local, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            attempts++;
            local.Register<MultiAgentInitializedEvent>((_, _) => { initialized++; return default; });
            entered.TrySetResult(true);
            await release.Task;
        });
        await using var host = await CreateAsync(kind, new(), [plugin]);
        var initialization = host.InitializeAsync(cancellation.Token);
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            release.TrySetResult(true);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.InitializeAsync());
            Assert.Equal(1, attempts);
            Assert.Equal(0, initialized);
            Assert.Equal(0, host.BodyCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.TrySetResult(true);
            try { await initialization.ConfigureAwait(true); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (primary is not null)
            {
                if (!ReferenceEquals(primary, error)) primary.Data["HL01.CancellationJoinError"] = error;
            }
        }
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public void PluginDelegatesAreExcludedFromDefaultOptionsJson(string kind)
    {
        var plugin = new MultiAgentPlugin("borrowed", (_, _, _) => throw new InvalidOperationException("Serialization must not invoke this callback."));
        if (kind == "graph") Assert.Equal(JsonSerializer.Serialize(new GraphOptions()), JsonSerializer.Serialize(new GraphOptions { Plugins = [plugin] }));
        else Assert.Equal(JsonSerializer.Serialize(new SwarmOptions()), JsonSerializer.Serialize(new SwarmOptions { Plugins = [plugin] }));
    }

    [Theory]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task MixedAfterFailuresRetainTheFirstPriorityCauseAndStillAttemptTheLocalCallback(string kind)
    {
        var first = new IOException("caller after failure");
        var second = new IOException("local after failure");
        var seen = new List<string>();
        var caller = new MultiAgentHookRegistry();
        caller.Register<AfterMultiAgentInvocationEvent>((_, _) => { seen.Add("caller"); throw first; }, -10);
        var plugin = new MultiAgentPlugin("after", (_, local, _) =>
        {
            local.Register<AfterMultiAgentInvocationEvent>((_, _) => { seen.Add("local"); throw second; });
            return default;
        });
        await using var host = await CreateAsync(kind, caller, [plugin]);

        Assert.Same(first, await Assert.ThrowsAsync<IOException>(() => host.InvokeAsync()));
        Assert.Equal(new[] { "caller", "local" }, seen);
        Assert.Same(second, Assert.Single(Assert.IsType<List<Exception>>(first.Data["Strands.CleanupErrors"])));
        Assert.Equal(1, host.BodyCalls);
    }

    [Fact]
    public void DeclarationRequiresOnlyNonNullNameAndInitializer()
    {
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => new MultiAgentPlugin(null!, (_, _, _) => default)).ParamName);
        Assert.Equal("initialize", Assert.Throws<ArgumentNullException>(() => new MultiAgentPlugin("x", null!)).ParamName);
        Assert.Equal("", new MultiAgentPlugin("", (_, _, _) => default).Name);
        Assert.Equal(" ", new MultiAgentPlugin(" ", (_, _, _) => default).Name);
    }

    private static MultiAgentHookRegistry Caller(IMultiAgent owner) => owner is Graph graph ? graph.Hooks : ((Swarm)owner).Hooks;

    private static async Task Join(Task task, Exception? primary)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (primary is not null)
        {
            if (!ReferenceEquals(primary, error)) primary.Data["HL01.MutationJoinError"] = error;
        }
    }

    private static async Task<Host> CreateAsync(string kind, MultiAgentHookRegistry hooks, IReadOnlyList<MultiAgentPlugin> plugins, Action? onBody = null)
    {
        var counter = new Counter(onBody);
        if (kind == "graph") return new(new Graph(new() { Nodes = [new FunctionNode("node", (_, _, _) => Output(counter))], Hooks = hooks, Plugins = plugins }), null, counter);
        Assert.Equal("swarm", kind);
        var agent = new Agent(new() { Id = "node", Model = new AnswerModel(counter) });
        try { return new(new Swarm(new() { Nodes = [new(agent)], Routing = SwarmRoutingMode.Tool, AgentContext = AgentContextPolicy.Retain, Hooks = hooks, Plugins = plugins }), agent, counter); }
        catch { await agent.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(Counter counter)
    {
        counter.Enter();
        yield return new NodeResultUpdateEvent([new TextBlock("done")]);
        await Task.CompletedTask;
    }

    private sealed class Host(IMultiAgent value, Agent? agent, Counter counter) : IAsyncDisposable
    {
        public IMultiAgent Value => value;
        public int BodyCalls => counter.Calls;
        public Task InitializeAsync(CancellationToken token = default) => value is Graph graph ? graph.InitializeAsync(token) : ((Swarm)value).InitializeAsync(token);
        public Task<MultiAgentResult> InvokeAsync() => value.InvokeAsync(new MultiAgentInput("task"));
        public ValueTask DisposeAsync() => agent?.DisposeAsync() ?? default;
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
