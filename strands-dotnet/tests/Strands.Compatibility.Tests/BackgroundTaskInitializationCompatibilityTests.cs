using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class BackgroundTaskInitializationCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationDelegationIsForcedOnlyAfterAcceptedRegistrationsAreKnown(bool conflictingSelectors)
    {
        var model = new Model();
        var accepted = Tool("subagent");
        var denied = new DeniedTool();
        var decisions = new List<ITool>();
        await using var agent = await HarnessFactory.CreateAsync(Options(model) with
        {
            Tools = new ITool[] { accepted, denied },
            BackgroundTasks = new()
            {
                Agentic = conflictingSelectors ? new[] { "subagent" } : Array.Empty<string>(),
                Always = conflictingSelectors ? new[] { "subagent" } : Array.Empty<string>(),
                Never = new[] { "subagent" },
            },
            AgentOptions = new() { Model = model, ToolFilter = tool => { decisions.Add(tool); return !ReferenceEquals(tool, denied); } },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await agent.InvokeAsync("delegate", cancellationToken: deadline.Token);
        Assert.Equal("received", result.ToString());
        Assert.Single(decisions, tool => ReferenceEquals(tool, accepted));
        Assert.Single(decisions, tool => ReferenceEquals(tool, denied));
        Assert.Contains(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), block => block.ToolUseId == "call" && block.Content.Contains("Background task dispatched."));
    }

    [Fact]
    public async Task AConflictingForcedNameIsStillRejectedWhenItsOnlyToolIsDenied()
    {
        var denied = new DeniedTool();
        var decisions = 0;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => HarnessFactory.CreateAsync(Options(new Model()) with
        {
            Tools = new[] { denied },
            BackgroundTasks = new() { Always = new[] { "subagent" }, Never = new[] { "subagent" } },
            AgentOptions = new() { Model = new Model(), ToolFilter = tool => { if (ReferenceEquals(tool, denied)) { decisions++; return false; } return true; } },
        }));
        Assert.Equal(1, decisions);
    }

    [Fact]
    public async Task StandardDelegationIsVisibleBeforeInitializedAndItsFilterRunsOnce()
    {
        var hooks = new HookRegistry();
        var filtered = new List<ITool>();
        var initialized = 0;
        hooks.Register<InitializedEvent>(async (value, _) =>
        {
            Assert.Contains(value.Agent.Tools, tool => tool.Spec.Name == "subagent");
            Assert.Contains(value.Agent.Tools, tool => tool.Spec.Name == "strands_manage_background_task");
            await Assert.ThrowsAsync<InvalidOperationException>(() => value.Agent.InitializeAsync());
            initialized++;
        });
        await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with
        {
            BuiltinTools = new[] { "subagent" },
            AgentOptions = new() { Model = new Model(), Hooks = hooks, ToolFilter = tool => { filtered.Add(tool); return true; } },
        });
        Assert.Equal(1, initialized);
        Assert.Single(filtered, tool => tool.Spec.Name == "subagent");
        Assert.Equal(new[] { "subagent", "strands_manage_background_task" }, agent.Tools.Select(tool => tool.Spec.Name));
    }

    [Fact]
    public async Task DeniedStandardDelegationDoesNotEnterTheNormalizedPolicy()
    {
        var decisions = 0;
        await using var agent = await HarnessFactory.CreateAsync(Options(new Model()) with
        {
            BuiltinTools = new[] { "subagent" },
            AgentOptions = new() { Model = new Model(), ToolFilter = tool => { if (tool.Spec.Name != "subagent") return true; decisions++; return false; } },
        });
        Assert.Equal(1, decisions);
        Assert.DoesNotContain(agent.Tools, tool => tool.Spec.Name == "subagent");
        Assert.Single(agent.Tools);
    }

    [Fact]
    public async Task InitializationFactoryValidatesItsWholeBatchBeforeRegistration()
    {
        var first = Tool("same");
        var second = Tool("same");
        await using var agent = new Agent(new()
        {
            Model = new Model(),
            InitializationTools = _ => new[] { first, second },
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task AChildInheritsAcceptedToolsWithoutRepeatingTheParentFactory()
    {
        var factories = 0;
        var normalizations = 0;
        await using var parent = new Agent(new()
        {
            Model = new Model(),
            BackgroundTasks = new(),
            InitializationTools = _ => { factories++; return new[] { Tool("work") }; },
            BackgroundTasksPolicyNormalizer = (_, config, _) => { normalizations++; return config; },
        });
        await parent.InitializeAsync();
        await using var child = parent.CreateChild();
        await child.InitializeAsync();
        Assert.Equal(1, factories);
        Assert.Equal(2, normalizations);
        Assert.Single(child.Tools, tool => tool.Spec.Name == "work");
    }

    [Fact]
    public async Task AStandardFactoryFailureClosesOnlyTheExplicitlyOwnedModel()
    {
        var model = new OwnedModel();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => HarnessFactory.CreateAsync(Options(model) with
        {
            BuiltinTools = new[] { "subagent" },
            Subagent = new() { MaxDepth = -1 },
            AgentOptions = new() { Model = model, OwnsModel = true },
        }));
        Assert.Equal(1, model.Disposals);
    }

    private static FunctionTool Tool(string name) => new(new(name, "Application tool", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("child result"));
    private static HarnessOptions Options(IModel model) => new()
    {
        Model = model,
        BuiltinTools = Array.Empty<string>(),
        BuiltinPlugins = Array.Empty<string>(),
        Session = new() { Enabled = false },
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
    };
    private sealed class DeniedTool : ITool
    {
        public ToolSpec Spec => throw new InvalidOperationException("Denied tool specifications cannot be observed.");
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class OwnedModel : Model, IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    private class Model : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Interlocked.Increment(ref _calls) == 1;
            var delivered = request.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Any(call => call.Name == "strands_manage_background_task");
            yield return new ModelMessageStartEvent();
            if (first)
            {
                yield return new ModelContentBlockStartEvent(0, new("call", "subagent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: "{}");
            }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: delivered ? "received" : "waiting"); }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(first ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        }
    }
}
