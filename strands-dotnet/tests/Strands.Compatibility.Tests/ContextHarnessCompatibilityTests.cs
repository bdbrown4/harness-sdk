using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Context;
using Strands.Harness;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class ContextHarnessCompatibilityTests
{
    [Theory]
    [InlineData("auto", 1)]
    [InlineData("agentic", 4)]
    public async Task PortableContextModesBindAndKeepStatusEphemeral(string mode, int toolCount)
    {
        var model = new ScriptedModel();
        await using var agent = await HarnessFactory.CreateAsync(Configuration("\"" + mode + "\"").ToOptions(new() { ModelResolver = _ => model }) with { BackgroundTasks = null });
        Assert.NotNull(agent.ContextManager);
        Assert.Equal(toolCount, agent.Tools.Count);
        await agent.InvokeAsync("question");
        Assert.Equal(mode == "agentic", model.Requests[0].Messages.Last().Text.IndexOf("<context-status>", StringComparison.Ordinal) >= 0);
        Assert.DoesNotContain(agent.Messages, message => message.Text.IndexOf("<context-status>", StringComparison.Ordinal) >= 0);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("\"off\"")]
    public async Task PortableOffAddsNoContextManager(string json)
    {
        await using var agent = await HarnessFactory.CreateAsync(Configuration(json).ToOptions(new() { ModelResolver = _ => new ScriptedModel() }) with { BackgroundTasks = null });
        Assert.Null(agent.ContextManager);
        Assert.Empty(agent.Tools);
        await agent.InvokeAsync("question");
    }

    [Fact]
    public async Task ExplicitCoreManagerWinsOverPortableOffAndToolFilterRetainsItsBinding()
    {
        var model = new ScriptedModel();
        var manager = new ContextManager(new() { Mode = ContextMode.Agentic });
        await using var agent = await HarnessFactory.CreateAsync(Configuration("false").ToOptions(new()
        {
            AgentOptions = new() { Model = model, ContextManager = manager, ToolFilter = _ => false },
        }) with
        { BackgroundTasks = null });
        Assert.Same(manager, agent.ContextManager);
        Assert.Empty(agent.Tools);
        await agent.InvokeAsync("question");
        Assert.Contains("<context-status>", model.Requests[0].Messages.Last().Text);
    }

    [Fact]
    public async Task DefaultAutomaticContextReducesRealToolOutputAndRetainsOriginals()
    {
        var text = new string('x', 12_000);
        var model = new ScriptedModel();
        model.Turns.Enqueue(
        [
            new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("large-call", "large", JsonSerializer.SerializeToElement(new { }))),
            new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
        ]);
        await using var agent = await HarnessFactory.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            Tools = [new FunctionTool(new("large", "Return a large value", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>(text))],
        });
        await agent.InvokeAsync("read");
        Assert.Contains("[Truncated:", Assert.Single(model.Requests[1].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Content);
        var originals = new List<ContentBlock?>();
        foreach (var reference in await agent.ContextManager!.Stash!.ListAsync()) originals.Add(await agent.ContextManager.Stash.RetrieveAsync(reference));
        Assert.Contains(originals.OfType<TextBlock>(), block => block.Text == text);
    }

    [Fact]
    public async Task FileSessionsRestoreContextOriginalsIntoANewManager()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = new ScriptedModel();
            var store = new FileSessionStore(directory);
            var options = Options(model) with { AgentOptions = new() { Model = model, SessionStore = store, SessionId = "resume" } };
            string agentId;
            IReadOnlyList<string> references;
            ContextManager original;
            await using (var first = await HarnessFactory.CreateAsync(options with { BackgroundTasks = null }))
            {
                await first.InvokeAsync("first");
                agentId = first.Id;
                original = first.ContextManager!;
                references = await original.Stash!.ListAsync();
                Assert.NotEmpty(references);
            }
            await using var resumed = await HarnessFactory.CreateAsync(options with { BackgroundTasks = null });
            Assert.Equal(agentId, resumed.Id);
            Assert.NotSame(original, resumed.ContextManager);
            Assert.Equal(references.OrderBy(value => value), (await resumed.ContextManager!.Stash!.ListAsync()).OrderBy(value => value));
            await resumed.InvokeAsync("second");
            Assert.Equal(new[] { "first", "answer", "second" }, model.Requests[1].Messages.Select(message => message.Text));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DefaultDelegatesRetainContextHooksWithoutRegainingGeneratedTools()
    {
        var model = new ScriptedModel();
        model.Turns.Enqueue(
        [
            new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("delegate", "subagent", JsonSerializer.SerializeToElement(new { task = "inspect", tools = Array.Empty<string>() }))),
            new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
        ]);
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await HarnessFactory.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks },
            BuiltinTools = ["subagent"],
            Context = new() { Mode = ContextMode.Agentic },
        });
        await parent.InvokeAsync("delegate");
        Assert.NotNull(agents[1].ContextManager);
        Assert.NotSame(parent.ContextManager, agents[1].ContextManager);
        Assert.NotSame(parent.ContextManager!.Stash, agents[1].ContextManager!.Stash);
        Assert.Empty(agents[1].Tools);
        Assert.Contains("<context-status>", model.Requests[1].Messages.Last().Text);
    }

    private static HarnessAgentConfig Configuration(string context) => HarnessAgentConfig.Parse("{\"builtinTools\":[],\"builtinPlugins\":[],\"caching\":false,\"skills\":false,\"memory\":false,\"session\":false,\"contextManager\":" + context + "}");
    private static HarnessOptions Options(IModel model) => new()
    {
        Model = model,
        BuiltinTools = [],
        BuiltinPlugins = [],
        Memory = new() { Enabled = false },
        Skills = new() { Enabled = false },
        Session = new() { Enabled = false },
    };
    private sealed class ScriptedModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public Queue<ModelStreamEvent[]> Turns { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            var events = Turns.Count == 0 ? new ModelStreamEvent[]
            {
                new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: "answer"),
                new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
            } : Turns.Dequeue();
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
}
