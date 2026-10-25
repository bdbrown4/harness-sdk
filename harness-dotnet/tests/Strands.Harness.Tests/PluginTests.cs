using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Plugins;

namespace Strands.Harness.Tests;

public sealed class PluginTests
{
    [Fact]
    public async Task TodosPersistInAgentStateAndInjectOnlyIntoModelInput()
    {
        var todos = new[] { new { content = "Run tests", activeForm = "Running tests", status = "in_progress" } };
        var model = new ScriptedModel(Call("todo_write", new { todos }), Call("inspect", new { }), Text("done"));
        var inspect = Tool("inspect", (context, _) =>
        {
            Assert.True(context.Agent.State.GetAll().ContainsKey("todos"));
            return new ValueTask<string>("inspected");
        });
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            BuiltinPlugins = ["todos"],
            Tools = [inspect],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.Equal("done", (await agent.InvokeAsync("work")).ToString());

        Assert.DoesNotContain("Your current todo list", model.Requests[0].Messages[0].Text);
        Assert.Contains("[in_progress] Running tests", model.Requests[1].Messages[2].Text);
        Assert.Contains("[in_progress] Running tests", model.Requests[2].Messages[4].Text);
        Assert.All(agent.Messages, message => Assert.DoesNotContain("Your current todo list", message.Text));
        Assert.Equal(agent.Messages[2].TrackingId, model.Requests[1].Messages[2].TrackingId);
        Assert.Equal("1 todos remaining\n  [in_progress] Running tests", Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Content);
    }

    [Fact]
    public async Task ClearingTodosDeletesStateAndStopsInjection()
    {
        var model = new ScriptedModel(Call("todo_write", new { todos = new[] { new { content = "Done", activeForm = "Doing", status = "pending" } } }),
            Call("todo_write", new { todos = Array.Empty<object>() }), Text("cleared"));
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            BuiltinPlugins = ["todos"],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        await agent.InvokeAsync("work");

        Assert.False(agent.State.GetAll().ContainsKey("todos"));
        Assert.DoesNotContain("Your current todo list", model.Requests[2].Messages[4].Text);
        Assert.Equal("Todo list cleared", Assert.IsType<ToolResultBlock>(agent.Messages[4].Content[0]).Content);
    }

    [Fact]
    public async Task InvalidTodoStatusReturnsAnErrorWithoutChangingState()
    {
        var model = new ScriptedModel(Call("todo_write", new { todos = new[] { new { content = "Work", activeForm = "Working", status = "invalid" } } }), Text("recovered"));
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            BuiltinPlugins = ["todos"],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        await agent.InvokeAsync("work");

        Assert.False(agent.State.GetAll().ContainsKey("todos"));
        Assert.Equal(ToolResultStatus.Error, Assert.IsType<ToolResultBlock>(agent.Messages[2].Content[0]).Status);
    }

    [Fact]
    public async Task SharedTodosPluginDoesNotShareAgentState()
    {
        var plugin = new Todos();
        var firstModel = new ScriptedModel(Text("first"));
        var secondModel = new ScriptedModel(Text("second"));
        var first = await BareAgent(firstModel, plugin);
        var second = await BareAgent(secondModel, plugin);
        var input = JsonSerializer.SerializeToElement(new { todos = new[] { new { content = "First", activeForm = "Working first", status = "pending" } } });
        var tool = first.Tools.Single(candidate => candidate.Spec.Name == "todo_write");
        await tool.InvokeAsync(input, new(first, new("todo", "todo_write", input), new Dictionary<string, object?>()));

        await first.InvokeAsync("one");
        await second.InvokeAsync("two");

        Assert.Contains("Your current todo list", firstModel.Requests[0].Messages[0].Text);
        Assert.DoesNotContain("Your current todo list", secondModel.Requests[0].Messages[0].Text);
        Assert.Empty(second.State.GetAll());
    }

    [Fact]
    public async Task TodosStateSurvivesSessionResumeWithoutPersistingTheReminder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-todos-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = await Harness.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = new ScriptedModel(Call("todo_write", new { todos = new[] { new { content = "Remember", activeForm = "Remembering", status = "pending" } } }), Text("saved")),
                BuiltinTools = [],
                BuiltinPlugins = ["todos"],
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new() { Directory = directory, Id = "todo-session" },
            });
            await first.InvokeAsync("plan");
            var model = new ScriptedModel(Text("resumed"));
            var resumed = await Harness.CreateAsync(new HarnessOptions
            {
                BackgroundTasks = null,
                Model = model,
                BuiltinTools = [],
                BuiltinPlugins = ["todos"],
                Memory = new() { Enabled = false },
                Context = new() { Enabled = false },
                Session = new() { Directory = directory, Id = "todo-session" },
            });
            await resumed.InvokeAsync("continue");

            Assert.Contains("[pending] Remember", model.Requests[0].Messages.Last().Text);
            Assert.All(resumed.Messages, message => Assert.DoesNotContain("Your current todo list", message.Text));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DefaultsAddPluginsAndExplicitInstancesAreNotDuplicated()
    {
        var todos = new Todos(new TodosConfig { Name = "custom-todos" });
        var model = new ScriptedModel(Text("ready"));
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            Plugins = [todos],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.Single(agent.Tools, tool => tool.Spec.Name == "todo_write");
        await agent.InvokeAsync("hello");
        Assert.Contains("<environment>", model.Requests[0].Messages[0].Text);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Todos")]
    public async Task UnknownBuiltinPluginNamesFail(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new ScriptedModel(Text("unused")),
            BuiltinPlugins = [name],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));
    }

    [Fact]
    public async Task PluginToolNamesParticipateInNormalizedCollisionChecks()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = new ScriptedModel(Text("unused")),
            BuiltinTools = [],
            BuiltinPlugins = ["todos"],
            Tools = [Tool("todo-write", (_, _) => new ValueTask<string>("collision"))],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));
    }

    [Fact]
    public async Task EnvironmentContextDiscoversSandboxDocumentsAndMemoizesPerAgent()
    {
        var sandbox = new FakeEnvironment();
        var day = new DateTime(2026, 10, 20);
        var plugin = new EnvironmentContext(sandbox, () => day, "test-environment");
        var model = new ScriptedModel(Call("inspect", new { }), Text("first"), Text("second"));
        var agent = await Harness.CreateAsync(new HarnessOptions
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Plugins = [plugin],
            Tools = [Tool("inspect", (_, _) => new ValueTask<string>("result"))],
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });
        await agent.InvokeAsync("question");
        var discoveryCalls = sandbox.ListCalls;
        day = day.AddDays(1);
        await agent.InvokeAsync("next");

        var first = model.Requests[0].Messages[0].Text;
        Assert.Contains("Platform: RemoteOS", first);
        Assert.Contains("Date: 2026-10-20", first);
        Assert.Contains("Working directory: /remote", first);
        Assert.Contains("<AGENTS.md>\nroot instructions\n</AGENTS.md>", first);
        Assert.Contains("project/src/AGENTS.md", first);
        Assert.Contains("project/README.md", first);
        Assert.DoesNotContain("node_modules", first);
        Assert.DoesNotContain("too-deep", first);
        Assert.DoesNotContain("<environment>", model.Requests[1].Messages[0].Text);
        Assert.Contains("Date: 2026-10-21", model.Requests[2].Messages.Last().Text);
        Assert.Equal(discoveryCalls, sandbox.ListCalls);
        Assert.All(agent.Messages, message => Assert.DoesNotContain("<environment>", message.Text));
    }

    [Fact]
    public async Task EnvironmentContextTruncatesLargeInstructionsAndHandlesMissingDiscovery()
    {
        var sandbox = new FakeEnvironment { AgentsText = new string('a', 17000) };
        var model = new ScriptedModel(Text("one"));
        var agent = await BareAgent(model, new EnvironmentContext(sandbox));
        await agent.InvokeAsync("question");

        Assert.Contains("truncated", model.Requests[0].Messages[0].Text);
        Assert.DoesNotContain(new string('a', 16001), model.Requests[0].Messages[0].Text);

        var fallbackModel = new ScriptedModel(Text("two"));
        var fallback = await BareAgent(fallbackModel, new EnvironmentContext(new TextOnlySandbox()));
        await fallback.InvokeAsync("question");
        Assert.Contains("Date:", fallbackModel.Requests[0].Messages[0].Text);
        Assert.DoesNotContain("Working directory:", fallbackModel.Requests[0].Messages[0].Text);
        Assert.DoesNotContain("Platform:", fallbackModel.Requests[0].Messages[0].Text);
    }

    private static Task<Agent> BareAgent(IModel model, IPlugin plugin) => Harness.CreateAsync(new HarnessOptions
    {
        BackgroundTasks = null,
        Model = model,
        BuiltinTools = [],
        BuiltinPlugins = [],
        Plugins = [plugin],
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
    });

    private static FunctionTool Tool(string name, Func<ToolContext, CancellationToken, ValueTask<string>> body) => new(
        new(name, "A deterministic helper", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, token) => body(context, token));

    private static ModelStreamEvent[] Text(string text) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    ];

    private static ModelStreamEvent[] Call(string name, object arguments) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call", name, JsonSerializer.SerializeToElement(arguments))),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    ];

    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }

    private sealed class TextOnlySandbox : ISandbox
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => new("unused");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => default;
    }

    private sealed class FakeEnvironment : IEnvironmentSandbox
    {
        public string AgentsText { get; init; } = "root instructions";
        public int ListCalls { get; private set; }
        public ValueTask<string> GetWorkingDirectoryAsync(CancellationToken cancellationToken = default) => new("/remote");
        public ValueTask<string?> GetPlatformAsync(CancellationToken cancellationToken = default) => new("RemoteOS");
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => path == "/remote/AGENTS.md" ? new(AgentsText) : throw new FileNotFoundException();
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<SandboxEntry>> ListFilesAsync(string path, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            IReadOnlyList<SandboxEntry> entries = path switch
            {
                "/remote" => [new("AGENTS.md", false), new("README.md", false), new("project", true), new("node_modules", true), new(".git", true)],
                "/remote/project" => [new("README.md", false), new("src", true)],
                "/remote/project/src" => [new("AGENTS.md", false), new("too-deep", true)],
                _ => throw new IOException("Unexpected discovery path: " + path),
            };
            return new(entries);
        }
    }
}
