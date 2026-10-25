using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Files;
using Strands.Agents.Interventions;
using Strands.Agents.Interventions.Steering;

namespace Strands.Harness.Tests;

public sealed class HarnessInterventionTests
{
    [Fact]
    public async Task OffAndNativeHandlersPreserveIdentityWithoutReadingFiles()
    {
        var handler = new NamedHandler("native");
        var files = new Files();
        var result = await HarnessInterventions.ResolveAsync([" off ", handler], new() { FileSystem = files });
        Assert.Same(handler, Assert.Single(result));
        Assert.Empty(files.Reads);
        Assert.Empty(await HarnessInterventions.ResolveAsync());
    }

    [Theory]
    [InlineData(" ask ", false)]
    [InlineData("smart", true)]
    [InlineData("  Only allow safe reads  ", true)]
    [InlineData("permit(principal, action, resource);", true)]
    [InlineData("rules.CEDAR", true)]
    [InlineData("ASK", true)]
    public async Task PresetsAndProseUseRealApprovalAndAuxiliaryClassification(string setting, bool classifies)
    {
        var auxiliary = new DecisionModel();
        await using var agent = new Agent(new() { Model = new ScriptModel(), AuxModel = auxiliary });
        var handler = Assert.IsType<HumanInTheLoop>(Assert.Single(await HarnessInterventions.ResolveAsync([setting])));
        var action = await handler.BeforeToolCallAsync(new(agent, new("call", "read", JsonSerializer.SerializeToElement(new { path = "public" })), new Dictionary<string, object?>()));
        if (classifies)
        {
            Assert.IsType<Proceed>(action);
            var request = Assert.Single(auxiliary.Requests);
            Assert.Single(request.Messages);
            Assert.Equal(StructuredOutputTool<object>.ToolName, Assert.Single(request.Tools!).Name);
            if (setting == "smart") Assert.Contains("risk evaluator", request.SystemPrompt);
            else Assert.Equal(setting, request.SystemPrompt);
        }
        else { Assert.IsType<Confirm>(action); Assert.Empty(auxiliary.Requests); }
    }

    [Fact]
    public async Task DuplicateNamesFailWithoutSilentlyReplacingHandlers()
    {
        Assert.Contains("strands:human-in-the-loop", (await Assert.ThrowsAsync<ArgumentException>(() => HarnessInterventions.ResolveAsync(["ask", "smart"]))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessInterventions.ResolveAsync([new NamedHandler("same"), new NamedHandler("same")]));
    }

    [Fact]
    public async Task CedarPathsReadOnlyTheSelectedFilesystemAndPassPolicyTextToTheRealBinding()
    {
        var files = new Files();
        var handler = new NamedHandler("cedar");
        string? policy = null;
        var result = await HarnessInterventions.ResolveAsync(["  ./rules.cedar  "], new()
        {
            FileSystem = files,
            CedarPolicyFactory = text => { policy = text; return handler; },
        });
        Assert.Same(handler, Assert.Single(result));
        Assert.Equal(files.Content, policy);
        Assert.Equal(new[] { "./rules.cedar" }, files.Reads);
    }

    [Fact]
    public async Task MissingCedarBindingFailsBeforeAnyFilesystemRead()
    {
        var files = new Files();
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => HarnessInterventions.ResolveAsync(["rules.cedar"], new() { FileSystem = files }));
        Assert.Contains("CedarPolicyFactory", error.Message);
        Assert.Empty(files.Reads);
    }

    [Fact]
    public async Task AskPresetPausesAndResumesAnActualToolExactlyOnce()
    {
        var effects = 0;
        await using var agent = await Harness.CreateAsync(Options(new ScriptModel("write")) with
        {
            BackgroundTasks = null,
            Interventions = ["ask"],
            Tools = [Tool(() => effects++)],
        });
        var paused = await agent.InvokeAsync("task");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        Assert.Equal(0, effects);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))] });
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task CoreAndHarnessHandlersAreAdditiveWithStrictNameCollisions()
    {
        var core = new NamedHandler("core");
        var added = new NamedHandler("harness");
        var options = Options(new ScriptModel()) with { AgentOptions = new() { Model = new ScriptModel(), Interventions = [core] }, Interventions = [added] };
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal(new InterventionHandler[] { core, added }, agent.Interventions);
        await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(options with { BackgroundTasks = null, Interventions = [new NamedHandler("core")] }));
    }

    [Fact]
    public async Task PortableInterventionModulesCanProvideRealSteeringHandlers()
    {
        var effect = 0;
        var auxiliary = new DecisionModel(new { type = "guide", reason = "Use a narrower action." });
        var steering = new LlmSteeringHandler(new() { SystemPrompt = "Only narrow actions", Model = auxiliary, ContextProviders = [] });
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false,\"interventions\":\"off\",\"interventionModules\":[{\"module\":\"project/steering\"}]}");
        var options = config.ToOptions(new()
        {
            ModelResolver = _ => new ScriptModel("write"),
            InterventionModuleResolver = reference => { Assert.Equal("intervention", reference.Kind); Assert.Equal("project/steering", reference.Module); return [steering]; },
        });
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null, Tools = [Tool(() => effect++)] });
        Assert.Same(steering, Assert.Single(agent.Interventions));
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("task")).StopReason);
        Assert.Equal(0, effect);
        Assert.Equal("Only narrow actions", Assert.Single(auxiliary.Requests).SystemPrompt);
        Assert.Contains("GUIDANCE", Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingInterventionModuleBindingsFailBeforeConstructingModels()
    {
        var calls = 0;
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"interventionModules\":[{\"module\":\"policy\"}]}");
        var failure = Assert.Throws<InvalidOperationException>(() => config.ToOptions(new()
        {
            ModelFactory = _ => { calls++; return new ScriptModel(); },
        }));
        Assert.Contains("InterventionModuleResolver", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PortableCedarPathsUseProjectRootAndTheCoreSelectedFilesystem()
    {
        var files = new Files();
        var root = Path.Combine(Path.GetTempPath(), "strands-cedar-" + Guid.NewGuid().ToString("N"));
        var config = HarnessAgentConfig.Parse("{\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false,\"interventions\":[\" rules.cedar \",\"off\"],\"interventionModules\":[{\"module\":\"policy\"}]}");
        var options = config.ToOptions(new()
        {
            AgentOptions = new() { Model = new ScriptModel(), FileSystem = files },
            ModelResolver = _ => throw new InvalidOperationException("Core model wins."),
            InterventionModuleResolver = _ => [new NamedHandler("module")],
            InterventionOptions = new() { FileSystem = new BlockingFiles(), CedarPolicyFactory = text => { Assert.Equal(files.Content, text); return new NamedHandler("cedar"); } },
        }, root);
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal(new[] { "cedar", "module" }, agent.Interventions.Select(handler => handler.Name));
        Assert.Equal(new[] { Path.Combine(root, "rules.cedar") }, files.Reads);
    }

    [Fact]
    public async Task ExplicitStdioApprovalKeepsBorrowedStreamsOpen()
    {
        var effects = 0;
        using var reader = new StringReader("yes\n");
        using var writer = new StringWriter();
        await using (var agent = await Harness.CreateAsync(Options(new ScriptModel("write")) with
        {
            BackgroundTasks = null,
            Interventions = ["ask"],
            Tools = [Tool(() => effects++)],
            InterventionOptions = new() { Ask = HumanApprovalPrompts.Stdio(reader: reader, writer: writer) },
        }))
        {
            Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("task")).StopReason);
            Assert.Null(agent.InterruptState);
        }
        Assert.Equal(1, effects);
        Assert.Contains("Approve \"write\"?", writer.ToString(), StringComparison.Ordinal);
        writer.Write("still open");
        Assert.Equal(-1, reader.Peek());
    }

    [Fact]
    public async Task CancellationDuringSelectedCedarReadNeverConstructsAHandler()
    {
        var files = new BlockingFiles();
        var constructed = 0;
        using var cancellation = new CancellationTokenSource();
        var creating = Harness.CreateAsync(Options(new ScriptModel()) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = new ScriptModel(), FileSystem = files },
            Interventions = ["rules.cedar"],
            InterventionOptions = new() { CedarPolicyFactory = _ => { constructed++; return new NamedHandler("cedar"); } },
        }, cancellation.Token);
        await Bounded(files.Started.Task);
        cancellation.Cancel();
        Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating)).CancellationToken);
        Assert.Equal(0, constructed);
    }

    [Fact]
    public async Task FailedSteeringObservationRollsBackItsOwnedSubscriptions()
    {
        var provider = new TrackingProvider();
        var primary = new IOException("second provider failed");
        var steering = new DenyingSteering(new() { ContextProviders = [provider, new FailingProvider(primary)] });
        var failure = await Assert.ThrowsAsync<IOException>(() => Harness.CreateAsync(Options(new ScriptModel()) with { BackgroundTasks = null, Interventions = [steering] }));
        Assert.Same(primary, failure);
        Assert.Equal(1, provider.Disposals);
        Assert.NotNull(provider.Agent);
        await provider.Agent.Hooks.InvokeAsync(new BeforeInvocationEvent(provider.Agent, new Dictionary<string, object?>()));
        Assert.Equal(0, provider.Notifications);
    }

    [Fact]
    public async Task StandardDelegatesInheritRealApprovalHandlers()
    {
        var effects = 0;
        var prompts = new List<string>();
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        var approval = new HumanInTheLoop(new()
        {
            AllowedTools = ["subagent"],
            Ask = (prompt, _) => { prompts.Add(prompt); return new ValueTask<JsonElement?>(JsonSerializer.SerializeToElement(false)); },
        });
        var model = new QueueModel(Call("subagent", new { task = "inspect" }), Call("write", new { }), Text("child"), Text("parent"));
        await using var agent = await Harness.CreateAsync(Options(model) with
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks },
            BuiltinTools = ["subagent"],
            Interventions = [approval],
            Tools = [Tool(() => effects++)],
        });
        Assert.Equal("parent", (await agent.InvokeAsync("delegate")).ToString());
        Assert.Equal(0, effects);
        Assert.Contains("\"write\"", Assert.Single(prompts), StringComparison.Ordinal);
        Assert.Equal(2, agents.Count);
        Assert.NotSame(agents[0], agents[1]);
        Assert.All(agents, value => Assert.Same(approval, Assert.Single(value.Interventions)));
    }

    private static async Task Bounded(Task operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(10))));
        await operation;
    }

    private static ModelStreamEvent[] Call(string name, object input) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call-" + name, name, JsonSerializer.SerializeToElement(input))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
    private static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];

    private static FunctionTool Tool(Action effect) => new(new("write", "Effect", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { effect(); return new ValueTask<string>("done"); });
    private static HarnessOptions Options(IModel model) => new()
    {
        Model = model,
        BuiltinTools = [],
        BuiltinPlugins = [],
        Memory = new() { Enabled = false },
        Skills = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
    };
    private sealed class Files : IReadOnlyFileSystem
    {
        public string Content { get; } = "permit(principal, action, resource);";
        public List<string> Reads { get; } = [];
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Reads.Add(path); return new(Content); }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No directory discovery was authorized.");
    }
    private sealed class NamedHandler(string name) : InterventionHandler { public override string Name => name; }
    private sealed class DenyingSteering(SteeringHandlerOptions? options = null) : SteeringHandler(options)
    {
        public override string Name => "steering";
        public override ValueTask<IToolSteeringAction> SteerBeforeToolAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default) => new(new Guide("Use a narrower action."));
    }
    private sealed class DecisionModel(object? decision = null) : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request); await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new("decision", StructuredOutputTool<object>.ToolName, JsonSerializer.SerializeToElement(decision ?? new { requiresApproval = false, reason = "safe read" })));
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }

    private sealed class BlockingFiles : IReadOnlyFileSystem
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { Started.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); return "unused"; }
        public ValueTask<IReadOnlyList<FileEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class TrackingProvider : ISteeringContextProvider
    {
        public string Name => "tracked";
        public int Disposals { get; private set; }
        public int Notifications { get; private set; }
        public Agent? Agent { get; private set; }
        public ValueTask<ISteeringContext> CreateAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            Agent = agent;
            var registration = agent.Hooks.Register<BeforeInvocationEvent>((_, _) => { Notifications++; return default; });
            return new(new Binding(registration, () => Disposals++));
        }
        private sealed class Binding(IDisposable registration, Action disposed) : ISteeringContext
        {
            public JsonElement Context => JsonSerializer.SerializeToElement(new { type = "tracked" });
            public ValueTask DisposeAsync() { registration.Dispose(); disposed(); return default; }
        }
    }
    private sealed class FailingProvider(Exception failure) : ISteeringContextProvider
    {
        public string Name => "failure";
        public ValueTask<ISteeringContext> CreateAsync(Agent agent, CancellationToken cancellationToken = default) => throw failure;
    }
    private sealed class QueueModel(params ModelStreamEvent[][] turns) : IModel
    {
        private int _index;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; foreach (var value in turns[_index++]) { cancellationToken.ThrowIfCancellationRequested(); yield return value; } }
    }
    private sealed class ScriptModel(string? tool = null) : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (tool is not null && Interlocked.Increment(ref _calls) == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("call", tool, JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }
}
