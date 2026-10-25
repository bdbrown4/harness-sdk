using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.MultiAgent;
using Xunit;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphConstructionBaselineAdmissionTests
{
    [Theory]
    [InlineData("graph-global", true)]
    [InlineData("graph-uncaptured-node", false)]
    [InlineData("swarm-global", true)]
    [InlineData("swarm-node", false)]
    public async Task UnsupportedConstructionPolicyIsRejectedBeforeBorrowedEffects(string route, bool outOfRange)
    {
        var probe = new Probe();
        await using var agent = probe.Agent;
        var plugin = new MultiAgentPlugin("effects", (_, _, _) => { probe.Plugins++; return default; });

        var error = Record.Exception(() =>
        {
            var node = new AgentNode(agent, "node", new()
            {
                ContextPolicy = route.EndsWith("-node", StringComparison.Ordinal)
                    ? AgentContextPolicy.ResetToConstructionBaselineOnRevisit : null
            });
            if (route.StartsWith("graph-", StringComparison.Ordinal))
                _ = new Graph(new()
                {
                    Nodes = new[] { node },
                    Plugins = new[] { plugin },
                    AgentContext = route == "graph-global" ? AgentContextPolicy.ResetToConstructionBaselineOnRevisit : AgentContextPolicy.Retain
                });
            else
                _ = new Swarm(new()
                {
                    Nodes = new[] { node },
                    Plugins = new[] { plugin },
                    AgentContext = route == "swarm-global" ? AgentContextPolicy.ResetToConstructionBaselineOnRevisit : AgentContextPolicy.Retain
                });
        });

        var argument = outOfRange ? Assert.IsType<ArgumentOutOfRangeException>(error) : Assert.IsType<ArgumentException>(error);
        Assert.Equal("options", argument.ParamName);
        probe.AssertUnchanged(initializations: 0);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("graph")]
    [InlineData("swarm")]
    public async Task UndefinedPolicyIsRejectedBeforeCaptureOrInitialization(string route)
    {
        var probe = new Probe();
        await using var agent = probe.Agent;
        var unknown = (AgentContextPolicy)123456;
        var plugin = new MultiAgentPlugin("effects", (_, _, _) => { probe.Plugins++; return default; });

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (route == "node") _ = new GraphBuilder().AddNode(agent, "node", new() { ContextPolicy = unknown });
            else if (route == "graph") _ = new Graph(new()
            {
                Nodes = new[] { new AgentNode(agent, "node") },
                AgentContext = unknown,
                Plugins = new[] { plugin }
            });
            else _ = new Swarm(new()
            {
                Nodes = new[] { new AgentNode(agent, "node") },
                AgentContext = unknown,
                Plugins = new[] { plugin }
            });
        });

        Assert.Equal("options", error.ParamName);
        probe.AssertUnchanged(initializations: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneHandleRejectsGraphOnlyPolicyBeforeAnyAgentEffect(bool capturedByBuilder)
    {
        var probe = new Probe();
        await using var agent = probe.Agent;
        await agent.InitializeAsync();
        var options = new AgentNodeOptions { ContextPolicy = AgentContextPolicy.ResetToConstructionBaselineOnRevisit };
        var node = capturedByBuilder ? new GraphBuilder().AddNode(agent, "node", options) : new AgentNode(agent, "node", options);
        var context = await PublicContextAsync();

        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in node.HandleAsync(new MultiAgentInput("must not run"), context)) { }
        });

        Assert.IsType<InvalidOperationException>(error);
        probe.AssertUnchanged(initializations: 1);
    }

    private static async Task<NodeContext> PublicContextAsync()
    {
        NodeContext? captured = null;
        var graph = new Graph(new()
        {
            Nodes = new Node[]
        {
            new FunctionNode("node", (_, context, _) => { captured = context; return CompleteAsync(); })
        }
        });
        Assert.Equal(Status.Completed, (await graph.InvokeAsync("context only")).Status);
        Assert.NotNull(captured);
        return captured;
    }

    private static async IAsyncEnumerable<INodeStreamEvent> CompleteAsync()
    {
        yield return new NodeResultUpdateEvent();
        await Task.CompletedTask;
    }

    private sealed class Probe
    {
        private readonly CountingModel _model = new();
        private int _initializations;
        public int Plugins { get; set; }
        public Agent Agent { get; }

        public Probe()
        {
            var state = new Dictionary<string, JsonElement> { ["phase"] = JsonSerializer.SerializeToElement(7) };
            Agent = new(new()
            {
                Id = "borrowed",
                Model = _model,
                OwnsModel = false,
                Messages = new[] { new Message(Role.User, new ContentBlock[] { new TextBlock("seed-7") }) },
                State = state,
                ModelState = state
            });
            Agent.Hooks.Register<InitializedEvent>((_, _) => { _initializations++; return default; });
        }

        public void AssertUnchanged(int initializations)
        {
            Assert.Equal(initializations, _initializations);
            Assert.Equal(0, Plugins);
            Assert.Equal(0, _model.Calls);
            Assert.Equal(7, Agent.State.Get<int>("phase"));
            Assert.Equal(7, Agent.ModelState.Get<int>("phase"));
            var message = Assert.Single(Agent.Messages);
            Assert.Equal(Role.User, message.Role);
            Assert.Equal("seed-7", Assert.IsType<TextBlock>(Assert.Single(message.Content)).Text);
        }
    }

    private sealed class CountingModel : IModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "unexpected answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }
}
