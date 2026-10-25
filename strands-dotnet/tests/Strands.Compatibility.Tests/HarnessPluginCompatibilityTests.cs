using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

public sealed class HarnessPluginCompatibilityTests
{
    private const string Native = "\"builtinTools\":[],\"caching\":false,\"contextManager\":false,\"skills\":false,\"memory\":false";

    [Fact]
    public async Task ConfiguredTodoStateSurvivesARealSessionAndReminderStaysEphemeral()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-plugin-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = HarnessAgentConfig.Parse("{" + Native + ",\"name\":\"configured_harness\",\"builtinPlugins\":[\"todos\"],\"session\":{\"id\":\"Legacy Plugins\",\"dir\":\"sessions\"}}");
            var firstModel = new Model(Call(), Text("saved"));
            var first = await HarnessFactory.CreateAsync(config.ToOptions(new HarnessConfigBindings { ModelResolver = _ => firstModel }, directory) with { BackgroundTasks = null });
            await first.InvokeAsync("plan");
            var ids = first.Messages.Select(message => message.TrackingId).ToArray();
            var resumedModel = new Model(Text("resumed"));
            var resumed = await HarnessFactory.CreateAsync(config.ToOptions(new HarnessConfigBindings { ModelResolver = _ => resumedModel }, directory) with { BackgroundTasks = null });
            await resumed.InvokeAsync("continue");

            Assert.Equal("legacy-plugins", resumed.SessionId);
            Assert.Equal("Plan", resumed.State.Get<JsonElement>("todos")[0].GetProperty("content").GetString());
            var request = Assert.Single(resumedModel.Requests);
            Assert.Equal(ids, request.Messages.Take(ids.Length).Select(message => message.TrackingId));
            Assert.Contains("[pending] Plan", request.Messages.Last().Text);
            Assert.All(resumed.Messages, message => Assert.DoesNotContain("Your current todo list", message.Text));
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "sessions")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TextOnlySandboxUsesDateContextWithoutInspectingHostFiles()
    {
        var model = new Model(Text("answer"));
        var sandbox = new TextOnlySandbox();
        var config = HarnessAgentConfig.Parse("{" + Native + ",\"builtinPlugins\":[\"environment\"],\"session\":false,\"sandbox\":{\"module\":\"application/sandbox\"}}");
        var agent = await HarnessFactory.CreateAsync(config.ToOptions(new HarnessConfigBindings
        {
            ModelResolver = _ => model,
            SandboxResolver = reference => { Assert.Equal("sandbox", reference.Kind); return sandbox; },
        }) with
        { BackgroundTasks = null });
        await agent.InvokeAsync("question");

        var request = Assert.Single(model.Requests);
        Assert.Contains("Date:", request.Messages[0].Text);
        Assert.DoesNotContain("Working directory:", request.Messages[0].Text);
        Assert.DoesNotContain("Platform:", request.Messages[0].Text);
        Assert.Equal(0, sandbox.Reads);
        Assert.Equal("question", agent.Messages[0].Text);
    }

    [Fact]
    public async Task TypedConfigurationBindingsRegisterRealToolsAndPreserveCorePrecedence()
    {
        var model = new Model(Text("answer"));
        var tool = new FunctionTool(new ToolSpec("bound", "A bound native tool", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, _) => new ValueTask<string>("native result"));
        var config = HarnessAgentConfig.Parse("{" + Native + ",\"builtinPlugins\":[],\"session\":false,\"tools\":[{\"module\":\"tools.py\",\"export\":\"native\",\"language\":\"python\"}],\"agentConfig\":{\"state\":{\"source\":\"configured\"}}}");
        var options = config.ToOptions(new HarnessConfigBindings
        {
            AgentOptions = new AgentOptions { Model = model, Name = "core", SystemPrompt = "core instructions", State = new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("core") } },
            ToolResolver = reference => { Assert.Equal("native", reference.Export); Assert.Equal("python", reference.Language); return tool; },
        });
        var agent = await HarnessFactory.CreateAsync(options with { BackgroundTasks = null });
        var input = JsonSerializer.SerializeToElement(new { });
        Assert.Equal("native result", await Assert.Single(agent.Tools).InvokeAsync(input, new ToolContext(agent, new ToolUseBlock("bound", "bound", input), new Dictionary<string, object?>())));
        await agent.InvokeAsync("question");

        Assert.Equal("core", agent.Name);
        Assert.Equal("core", agent.State.Get<string>("source"));
        Assert.Equal("core instructions", Assert.Single(model.Requests).SystemPrompt);
        Assert.Equal(config.ToJson(), HarnessAgentConfig.Parse(config.ToJson(indented: true)).ToJson());
    }

    [Fact]
    public async Task ConfiguredSubagentsUseFreshStateAndKeepSelectedPluginTools()
    {
        var model = new Model(CallTool("subagent", new { task = "plan", tools = new[] { "todo_write" } }), Call(), Text("child report"), Text("parent answer"));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((hook, _) => { agents.Add(hook.Agent); return default; });
        var config = HarnessAgentConfig.Parse("{" + Native.Replace("\"builtinTools\":[]", "\"builtinTools\":{\"*\":false,\"subagent\":{\"maxDepth\":1}}") + ",\"builtinPlugins\":[\"todos\"],\"session\":false}");
        var parent = await HarnessFactory.CreateAsync(config.ToOptions(new HarnessConfigBindings { AgentOptions = new AgentOptions { Model = model, Hooks = hooks } }) with { BackgroundTasks = null });
        Assert.Equal("parent answer", (await parent.InvokeAsync("root")).ToString());

        Assert.Equal(2, agents.Count);
        Assert.NotSame(parent, agents[1]);
        Assert.Null(agents[1].SessionId);
        Assert.Equal(0, agents[1].State.Get<int>("subagentDepth"));
        Assert.True(agents[1].State.GetAll().ContainsKey("todos"));
        Assert.False(parent.State.GetAll().ContainsKey("todos"));
        Assert.Equal("todo_write", Assert.Single(agents[1].Tools).Spec.Name);
        Assert.Equal("child report", Assert.IsType<ToolResultBlock>(parent.Messages[2].Content[0]).Content);
    }

    private sealed class TextOnlySandbox : ISandbox
    {
        public int Reads { get; private set; }
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) { Reads++; throw new IOException("Host reads are not permitted by this test sandbox."); }
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new IOException("Host writes are not permitted by this test sandbox.");
    }

    private static ModelStreamEvent[] Text(string value) => new ModelStreamEvent[]
    {
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    };
    private static ModelStreamEvent[] Call() => new ModelStreamEvent[]
    {
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("todo", "todo_write", JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "Plan", activeForm = "Planning", status = "pending" } } }))),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    };
    private static ModelStreamEvent[] CallTool(string name, object input) => new ModelStreamEvent[]
    {
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("delegate", name, JsonSerializer.SerializeToElement(input))),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    };
    private sealed class Model : IModel
    {
        private readonly ModelStreamEvent[][] _turns;
        public List<ModelRequest> Requests { get; } = new();
        public Model(params ModelStreamEvent[][] turns) => _turns = turns;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var item in _turns[Requests.Count - 1]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }
}
