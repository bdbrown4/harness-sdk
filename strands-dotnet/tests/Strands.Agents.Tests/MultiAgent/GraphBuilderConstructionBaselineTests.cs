using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.MultiAgent;
using Xunit;

namespace Strands.Agents.Tests.MultiAgent;

// First capability characterization: only the explicit enum scaffold accompanies these tests.
// The borrowed Agent is ready, inactive and stateless before AddNode. No session/backend I/O.
public sealed class GraphBuilderConstructionBaselineTests
{
    [Fact]
    public async Task PythonRevisitUsesAddNodeBaselineWhileFirstVisitKeepsPostPluginState()
    {
        var observed = await RunAsync(AgentContextPolicy.ResetToConstructionBaselineOnRevisit);

        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 1), new Observation(7, 7, "seed-7", 1) }, observed.BeforeNode);
        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 2), new Observation(7, 7, "seed-7", 2) }, observed.Provider);
        Assert.Equal(new Observation(99, 99, "seed-7", 3), observed.Final);
    }

    [Fact]
    public async Task PythonRevisitRestoresBeforeNodeHookAndPreservesThatHooksReplacement()
    {
        var observed = await RunAsync(AgentContextPolicy.ResetToConstructionBaselineOnRevisit, replaceSecondHookState: true);

        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 1), new Observation(7, 7, "seed-7", 1) }, observed.BeforeNode);
        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 2), new Observation(17, 17, "seed-17", 2) }, observed.Provider);
        Assert.Equal(new Observation(99, 99, "seed-17", 3), observed.Final);
    }

    [Fact]
    public async Task NativeResetRetainsItsFirstEntryBaselineAndExistingHookOrder()
    {
        var observed = await RunAsync(AgentContextPolicy.ResetOnRevisit);

        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 1), new Observation(99, 99, "seed-13", 3) }, observed.BeforeNode);
        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 2), new Observation(13, 13, "seed-13", 2) }, observed.Provider);
        Assert.Equal(new Observation(99, 99, "seed-13", 3), observed.Final);
    }

    [Fact]
    public async Task TypeScriptDefaultStillRestoresEachVisitToItsImmediatePreRunState()
    {
        var observed = await RunAsync(policy: null);

        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 1), new Observation(13, 13, "seed-13", 1) }, observed.BeforeNode);
        Assert.Equal(new[] { new Observation(13, 13, "seed-13", 2), new Observation(13, 13, "seed-13", 2) }, observed.Provider);
        Assert.Equal(new Observation(13, 13, "seed-13", 1), observed.Final);
    }

    private static async Task<Observed> RunAsync(AgentContextPolicy? policy, bool replaceSecondHookState = false)
    {
        var trace = new List<string>();
        var beforeNode = new List<Observation>();
        var provider = new List<Observation>();
        var agentInitializations = 0;
        var pluginInitializations = 0;
        var graphInitialized = 0;
        using var model = new RecordingModel();
        Observation final;
        await using (var agent = new Agent(new()
        {
            Id = "borrowed",
            Model = model,
            OwnsModel = false,
            Messages = new[] { SeedMessage(7) },
            State = Phase(7),
            ModelState = Phase(7)
        }))
        {
            using var initializedRegistration = agent.Hooks.Register<InitializedEvent>((_, _) =>
            {
                agentInitializations++;
                return default;
            });
            using var afterRegistration = agent.Hooks.Register<AfterInvocationEvent>((_, _) =>
            {
                agent.State.Set("phase", 99);
                agent.ModelState.Set("phase", 99);
                trace.Add("after:" + model.Calls);
                return default;
            });
            model.OnRequest = request =>
            {
                Assert.NotNull(request.ModelState);
                provider.Add(new(agent.State.Get<int>("phase"), request.ModelState!["phase"].GetInt32(), Seed(request.Messages), request.Messages.Count));
                trace.Add("model:" + model.Calls);
            };

            await agent.InitializeAsync();
            Assert.Equal(1, agentInitializations);
            Assert.Equal(new Observation(7, 7, "seed-7", 1), Capture(agent));
            trace.Add("ready:7");

            var hooks = new MultiAgentHookRegistry();
            using var beforeRegistration = hooks.Register<BeforeNodeCallEvent>(async (value, token) =>
            {
                Assert.Equal("node", value.NodeId);
                beforeNode.Add(Capture(agent));
                trace.Add("before:" + beforeNode.Count);
                if (replaceSecondHookState && beforeNode.Count == 2)
                    await ReplaceAsync(agent, 17, token);
            });
            var plugin = new MultiAgentPlugin("phase", async (_, local, token) =>
            {
                pluginInitializations++;
                await ReplaceAsync(agent, 11, token);
                trace.Add("plugin:11");
                local.Register<MultiAgentInitializedEvent>(async (_, initializedToken) =>
                {
                    graphInitialized++;
                    await ReplaceAsync(agent, 13, initializedToken);
                    trace.Add("initialized:13");
                });
            });
            var builder = new GraphBuilder(new GraphOptions { Hooks = hooks, Plugins = new[] { plugin }, MaxSteps = 3 });
            var node = builder.AddNode(agent, "node", policy is { } selected ? new AgentNodeOptions { ContextPolicy = selected } : null);
            Assert.Same(agent, node.Agent);
            Assert.Equal(new Observation(7, 7, "seed-7", 1), Capture(agent));
            Assert.Equal(1, agentInitializations);
            Assert.Equal(0, pluginInitializations);
            Assert.Equal(0, graphInitialized);
            Assert.Equal(0, model.Calls);
            trace.Add("added:7");

            await ReplaceAsync(agent, 9, CancellationToken.None);
            trace.Add("pre-build:9");
            builder.SetEntryPoint("node").AddEdge("node", "node", (state, _, _) => new ValueTask<bool>(state.Steps < 2));
            var graph = builder.Build();
            Assert.Same(node, graph.Nodes["node"]);
            Assert.Equal(new Observation(9, 9, "seed-9", 1), Capture(agent));
            Assert.Equal(1, agentInitializations);
            Assert.Equal(0, pluginInitializations);
            Assert.Equal(0, graphInitialized);
            Assert.Equal(0, model.Calls);
            trace.Add("built:9");

            await graph.InitializeAsync();
            Assert.Equal(new Observation(13, 13, "seed-13", 1), Capture(agent));
            Assert.Equal(0, model.Calls);
            var result = await graph.InvokeAsync("task");

            Assert.Equal(Status.Completed, result.Status);
            Assert.Equal(new[] { "node", "node" }, result.Results.Select(value => value.NodeId));
            Assert.All(result.Results, value => Assert.Equal(Status.Completed, value.Status));
            Assert.Equal(2, model.Calls);
            Assert.Equal(1, agentInitializations);
            Assert.Equal(1, pluginInitializations);
            Assert.Equal(1, graphInitialized);
            Assert.Same(model, agent.Model);
            Assert.False(model.Disposed);
            Assert.Equal(new[]
            {
                "ready:7", "added:7", "pre-build:9", "built:9", "plugin:11", "initialized:13",
                "before:1", "model:1", "after:1", "before:2", "model:2", "after:2"
            }, trace);
            final = Capture(agent);
        }
        Assert.False(model.Disposed);
        return new(beforeNode.ToArray(), provider.ToArray(), final);
    }

    private static Observation Capture(Agent agent) =>
        new(agent.State.Get<int>("phase"), agent.ModelState.Get<int>("phase"), Seed(agent.Messages), agent.Messages.Count);

    private static string Seed(IReadOnlyList<Message> messages)
    {
        Assert.NotEmpty(messages);
        Assert.Equal(Role.User, messages[0].Role);
        return Assert.IsType<TextBlock>(Assert.Single(messages[0].Content)).Text;
    }

    private static Message SeedMessage(int phase) => new(Role.User, new ContentBlock[] { new TextBlock("seed-" + phase) });

    private static IReadOnlyDictionary<string, JsonElement> Phase(int phase) =>
        new Dictionary<string, JsonElement> { ["phase"] = JsonSerializer.SerializeToElement(phase) };

    private static async ValueTask ReplaceAsync(Agent agent, int phase, CancellationToken token)
    {
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { SeedMessage(phase) }),
            ["state"] = JsonSerializer.SerializeToElement(Phase(phase)),
            ["modelState"] = JsonSerializer.SerializeToElement(Phase(phase))
        });
        await agent.LoadSnapshotAsync(snapshot, token);
    }

    private sealed record Observation(int ApplicationPhase, int ModelPhase, string Seed, int MessageCount);
    private sealed record Observed(IReadOnlyList<Observation> BeforeNode, IReadOnlyList<Observation> Provider, Observation Final);

    private sealed class RecordingModel : IModel, IDisposable
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public Action<ModelRequest>? OnRequest { get; set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            Calls++;
            OnRequest?.Invoke(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }
}
