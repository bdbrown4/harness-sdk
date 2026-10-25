using System.Runtime.CompilerServices;
using Strands.Agents;
using Strands.Agents.Context;

namespace Strands.Harness.Tests;

public sealed class ContextHarnessTests
{
    [Theory]
    [InlineData(ContextMode.Auto)]
    [InlineData(ContextMode.Agentic)]
    public async Task HarnessBuildsTheSelectedContextManagerAndItsTools(ContextMode mode)
    {
        var model = new RecordingModel();
        await using var agent = await Harness.CreateAsync(Options(model) with { BackgroundTasks = null, Context = new() { Mode = mode } });

        Assert.NotNull(agent.ContextManager);
        Assert.NotNull(agent.ContextManager.Stash);
        var expected = mode == ContextMode.Auto ? new[] { "retrieve_context" } : new[] { "pin_context", "truncate_context", "summarize_context", "retrieve_context" };
        Assert.Equal(expected, agent.Tools.Select(tool => tool.Spec.Name));
        Assert.Equal("answer", (await agent.InvokeAsync("question")).ToString());
        Assert.Equal(mode == ContextMode.Agentic, Assert.Single(model.Requests).Messages.Last().Text.Contains("<context-status>", StringComparison.Ordinal));
        Assert.DoesNotContain(agent.Messages, message => message.Text.Contains("<context-status>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultContextUsesAutomaticReductionAndRetainsOriginals()
    {
        var model = new RecordingModel();
        var original = new string('x', 12_000);
        var tool = new FunctionTool(new("large", "Return a large result", System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new(original));
        model.Turns.Enqueue(
        [
            new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("large-call", "large", System.Text.Json.JsonSerializer.SerializeToElement(new { }))),
            new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
        ]);
        await using var agent = await Harness.CreateAsync(Options(model) with { BackgroundTasks = null, Tools = [tool] });
        Assert.NotNull(agent.ContextManager);

        await agent.InvokeAsync("use the large result");

        var result = Assert.Single(model.Requests[1].Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Contains("[Truncated:", result.Content, StringComparison.Ordinal);
        var originals = new List<ContentBlock?>();
        foreach (var reference in await agent.ContextManager.Stash!.ListAsync()) originals.Add(await agent.ContextManager.Stash.RetrieveAsync(reference));
        Assert.Contains(originals.OfType<TextBlock>(), block => block.Text == original);
    }

    [Fact]
    public async Task DisabledContextAddsNoToolsOrStatus()
    {
        var model = new RecordingModel();
        await using var agent = await Harness.CreateAsync(Options(model) with { BackgroundTasks = null, Context = new() { Enabled = false } });
        Assert.Null(agent.ContextManager);
        Assert.Empty(agent.Tools);
        await agent.InvokeAsync("question");
        Assert.Equal("question", Assert.Single(model.Requests).Messages.Last().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreAndApplicationPluginManagersWinWithoutAddingAnotherManager(bool asPlugin)
    {
        var model = new RecordingModel();
        var manager = new ContextManager(new() { StashEnabled = false });
        await using var agent = await Harness.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, ContextManager = asPlugin ? null : manager, Plugins = asPlugin ? [manager] : [] },
            Context = new() { Enabled = false, Mode = (ContextMode)999 },
        });
        Assert.Same(manager, agent.ContextManager);
        Assert.Empty(agent.Tools);
        await agent.InvokeAsync("question");
    }

    [Fact]
    public async Task ExplicitConvenienceManagerWinsOverDisabledOrInvalidMode()
    {
        var manager = new ContextManager(new() { StashEnabled = false });
        await using var agent = await Harness.CreateAsync(Options(new RecordingModel()) with { BackgroundTasks = null, Context = new() { Enabled = false, Mode = (ContextMode)999, Manager = manager } });
        Assert.Same(manager, agent.ContextManager);
        Assert.Empty(agent.Tools);
    }

    [Fact]
    public async Task ExplicitConversationManagerSuppressesTheConvenienceDefault()
    {
        var conversation = new Conversation();
        await using var agent = await Harness.CreateAsync(Options(new RecordingModel()) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = new RecordingModel(), ConversationManager = conversation },
        });
        Assert.Null(agent.ContextManager);
        await agent.InvokeAsync("question");
        Assert.Equal(1, conversation.Calls);
    }

    [Fact]
    public async Task RegistrationFilteringRetainsTypedManagerAndItsHooks()
    {
        var model = new RecordingModel();
        var filtered = new List<string>();
        await using var agent = await Harness.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, ToolFilter = tool => { filtered.Add(tool.Spec.Name); return false; } },
            Context = new() { Mode = ContextMode.Agentic },
        });
        Assert.NotNull(agent.ContextManager);
        Assert.Empty(agent.Tools);
        Assert.Equal(new[] { "pin_context", "truncate_context", "summarize_context", "retrieve_context" }, filtered);
        await agent.InvokeAsync("question");
        Assert.Contains("<context-status>", Assert.Single(model.Requests).Messages.Last().Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatefulModelsRequireContextToBeDisabled(bool disabled)
    {
        var options = Options(new StatefulModel()) with { Context = new() { Enabled = !disabled } };
        if (!disabled)
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(options with { BackgroundTasks = null }));
            Assert.Contains("stateful", error.Message, StringComparison.Ordinal);
        }
        else
        {
            await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
            Assert.Equal("answer", (await agent.InvokeAsync("question")).ToString());
        }
    }

    [Theory]
    [InlineData("\"auto\"", true, ContextMode.Auto)]
    [InlineData("\"agentic\"", true, ContextMode.Agentic)]
    [InlineData("\"off\"", false, ContextMode.Auto)]
    [InlineData("false", false, ContextMode.Auto)]
    public async Task PortableConfigurationBindsTheTypedContextPolicy(string jsonValue, bool enabled, ContextMode mode)
    {
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"builtinPlugins\":[],\"caching\":false,\"skills\":false,\"memory\":false,\"session\":false,\"contextManager\":" + jsonValue + "}");
        var options = config.ToOptions(new() { ModelResolver = _ => new RecordingModel() });
        Assert.Equal(enabled, options.Context.Enabled);
        Assert.Equal(mode, options.Context.Mode);
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal(enabled, agent.ContextManager is not null);
        Assert.Equal(enabled ? mode == ContextMode.Auto ? 1 : 4 : 0, agent.Tools.Count);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"AUTO\"")]
    [InlineData("{}")]
    public void InvalidPortableContextFailsBeforeAnyComponentResolution(string jsonValue)
    {
        var resolved = 0;
        var error = Assert.Throws<ArgumentException>(() => HarnessAgentConfig.Parse("{\"contextManager\":" + jsonValue + "}").ToOptions(new() { ModelResolver = _ => { resolved++; return new RecordingModel(); } }));
        Assert.Contains("contextManager", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, resolved);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DefaultDelegatesUseFreshManagersAndKeepGeneratedToolsNarrowed(int selectedMode)
    {
        var model = new RecordingModel();
        model.Turns.Enqueue(Call(new { task = "inspect", tools = Array.Empty<string>() }));
        model.Turns.Enqueue(Text("child"));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await Harness.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks },
            BuiltinTools = ["subagent"],
            Context = new() { Enabled = selectedMode >= 0, Mode = selectedMode == 1 ? ContextMode.Agentic : ContextMode.Auto },
        });
        await parent.InvokeAsync("delegate");
        var child = agents[1];
        Assert.Empty(child.Tools);
        Assert.Equal(selectedMode >= 0, child.ContextManager is not null);
        Assert.Equal(selectedMode == 1, model.Requests[1].Messages.Last().Text.Contains("<context-status>", StringComparison.Ordinal));
        if (child.ContextManager is not null)
        {
            Assert.NotSame(parent.ContextManager, child.ContextManager);
            Assert.NotSame(parent.ContextManager!.Stash, child.ContextManager.Stash);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExplicitManagerDelegatesFallBackToFreshAutomaticPolicy(int source)
    {
        var model = new RecordingModel();
        model.Turns.Enqueue(Call(new { task = "inspect" }));
        model.Turns.Enqueue(Text("child"));
        var manager = new ContextManager(new() { Mode = ContextMode.Agentic });
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await Harness.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks, ContextManager = source == 0 ? manager : null, Plugins = source == 1 ? [manager] : [] },
            BuiltinTools = ["subagent"],
            Context = new() { Manager = source == 2 ? manager : null },
        });
        await parent.InvokeAsync("delegate");
        Assert.Same(manager, parent.ContextManager);
        Assert.NotNull(agents[1].ContextManager);
        Assert.NotSame(manager, agents[1].ContextManager);
        Assert.Contains(agents[1].Tools, tool => tool.Spec.Name == "retrieve_context");
        Assert.DoesNotContain(agents[1].Tools, tool => tool.Spec.Name is "summarize_context" or "truncate_context" or "pin_context");
        Assert.DoesNotContain("<context-status>", model.Requests[1].Messages.Last().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrandchildrenCannotRegainExcludedContextToolsAndEachManagerBindsSeparately()
    {
        var model = new RecordingModel();
        model.Turns.Enqueue(Call(new { task = "delegate again", tools = new[] { "subagent" } }));
        model.Turns.Enqueue(Call(new { task = "inspect", tools = Array.Empty<string>() }));
        model.Turns.Enqueue(Text("grandchild"));
        model.Turns.Enqueue(Text("child"));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await Harness.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks },
            BuiltinTools = ["subagent"],
            Context = new() { Mode = ContextMode.Agentic },
        });
        await parent.InvokeAsync("delegate");
        Assert.Equal(3, agents.Count);
        Assert.Equal("subagent", Assert.Single(agents[1].Tools).Spec.Name);
        Assert.Empty(agents[2].Tools);
        Assert.NotSame(agents[0].ContextManager, agents[1].ContextManager);
        Assert.NotSame(agents[1].ContextManager, agents[2].ContextManager);
        Assert.All(agents, agent => Assert.NotNull(agent.ContextManager!.Stash));
        Assert.Contains("<context-status>", model.Requests[2].Messages.Last().Text, StringComparison.Ordinal);
    }

    private static HarnessOptions Options(IModel model) => new()
    {
        Model = model,
        BuiltinTools = [],
        BuiltinPlugins = [],
        Skills = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Session = new() { Enabled = false },
    };

    private sealed class RecordingModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public Queue<ModelStreamEvent[]> Turns { get; } = new();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            var events = Turns.Count == 0 ? Text("answer") : Turns.Dequeue();
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }

    private sealed class StatefulModel : IStatefulModel
    {
        public bool Stateful => true;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var item in Text("answer"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item is ModelMessageStopEvent stop ? stop with { ModelState = new Dictionary<string, System.Text.Json.JsonElement> { ["cursor"] = System.Text.Json.JsonSerializer.SerializeToElement("response") } } : item;
            }
        }
    }

    private sealed class Conversation : IConversationManager
    {
        public int Calls { get; private set; }
        public IReadOnlyList<Message> Reduce(IReadOnlyList<Message> messages) { Calls++; return messages; }
    }

    private static ModelStreamEvent[] Call(object input) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("delegate", "subagent", System.Text.Json.JsonSerializer.SerializeToElement(input))),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    ];

    private static ModelStreamEvent[] Text(string text) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    ];
}
