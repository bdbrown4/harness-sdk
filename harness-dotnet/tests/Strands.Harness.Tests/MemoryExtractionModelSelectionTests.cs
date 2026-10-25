using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Memory;
using Strands.Agents.Storage;
using Strands.Harness.Testing;

namespace Strands.Harness.Tests;

public sealed class MemoryExtractionModelSelectionTests
{
    [Fact]
    public async Task DefaultMemoryExtractsWithTheExplicitWebFetchModelBeforeTheAuxiliaryModel()
    {
        var storage = new InMemoryStorage();
        var main = new TextModel("answer");
        var auxiliary = new TextModel("[{\"content\":\"# Wrong\nWrong model selected.\"}]".Replace("\n", "\\n"));
        var selected = new TextModel("[{\"content\":\"# Preferences\nPrefers coffee.\"}]".Replace("\n", "\\n"));
        await using var agent = await Harness.CreateAsync(Options(main, storage) with
        {
            AuxModel = auxiliary,
            WebFetch = new() { Model = selected },
        });

        Assert.Equal("answer", (await agent.InvokeAsync("Remember that I prefer coffee.")).ToString());
        await agent.ShutdownAsync();

        Assert.Single(selected.Requests);
        Assert.Empty(auxiliary.Requests);
        Assert.Same(selected, ExtractionModel(agent));
        Assert.Contains("Prefers coffee.", Encoding.UTF8.GetString((await storage.ReadAsync("preferences.md"))!), StringComparison.Ordinal);
        await agent.DisposeAsync();
        Assert.Equal(0, selected.DisposeCalls);
        Assert.Equal(0, auxiliary.DisposeCalls);
        Assert.Equal(0, main.DisposeCalls);
    }

    [Fact]
    public async Task PortableWebFetchIdentifierSelectsTheDefaultMemoryExtractorThroughTheResolver()
    {
        var models = new List<(string Identifier, TextModel Model)>();
        var config = HarnessAgentConfig.Parse(PortableConfig("application/selected"));
        var options = config.ToOptions(new()
        {
            ModelResolver = identifier =>
            {
                var model = new TextModel(identifier == Harness.DefaultModel ? "answer" : Facts("Selected", "Selected memory fact."));
                models.Add((identifier, model));
                return model;
            },
        });
        var storage = new InMemoryStorage();
        await using var agent = await Harness.CreateAsync(options with
        {
            UseEnvironmentTelemetry = false,
            BackgroundTasks = null,
            BuiltinTools = [],
            Memory = options.Memory with { Storage = storage },
        });
        await agent.InvokeAsync("Remember this preference.");
        await agent.ShutdownAsync();

        Assert.Equal(new[] { Harness.DefaultModel, "application/selected" }, models.Select(value => value.Identifier));
        Assert.Same(models[1].Model, ExtractionModel(agent));
        Assert.Contains("Selected memory fact.", Encoding.UTF8.GetString((await storage.ReadAsync("selected.md"))!), StringComparison.Ordinal);
        await agent.DisposeAsync();
        Assert.All(models, value => Assert.Equal(0, value.Model.DisposeCalls));
    }

    [Fact]
    public async Task PortableContentIdentifierKeepsFactoryPrecedenceAndExistingMappedSettings()
    {
        const string selectedId = "bedrock/amazon.nova-lite-v1:0";
        var configurations = new List<HarnessModelConfiguration>();
        var models = new List<TextModel>();
        var options = HarnessAgentConfig.Parse(PortableConfig(selectedId)).ToOptions(new()
        {
            ModelFactory = configuration =>
            {
                configurations.Add(configuration);
                var model = new TextModel(configuration.Identifier == Harness.DefaultModel ? "answer" : Facts("Factory", "Factory selected fact."));
                models.Add(model);
                return model;
            },
            ModelResolver = _ => throw new InvalidOperationException("Factory precedence was lost."),
        });
        await using var agent = await Harness.CreateAsync(options with
        {
            UseEnvironmentTelemetry = false,
            BackgroundTasks = null,
            BuiltinTools = [],
            Memory = options.Memory with { Storage = new InMemoryStorage() },
        });

        Assert.Equal(new[] { Harness.DefaultModel, selectedId }, configurations.Select(value => value.Identifier));
        Assert.Same(models[1], ExtractionModel(agent));
        Assert.False(configurations[1].Caching);
        Assert.False(configurations[1].NativeWebSearch);
        Assert.Null(configurations[1].Effort);
        await agent.DisposeAsync();
        Assert.All(models, model => Assert.Equal(0, model.DisposeCalls));
    }

    [Fact]
    public async Task MemoryAndWebFetchUseOneResolvedContentModelAndKeepItBorrowed()
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("source preference")));
        var main = new TextModel("answer");
        var resolved = new List<(string Identifier, TextModel Model)>();
        var options = HarnessAgentConfig.Parse(PortableConfig("application/shared")).ToOptions(new()
        {
            ModelResolver = identifier =>
            {
                var model = identifier == Harness.DefaultModel ? main : new TextModel(Facts("Shared", "Shared extracted fact."));
                resolved.Add((identifier, model));
                return model;
            },
        });
        var storage = new InMemoryStorage();
        await using var agent = await Harness.CreateAsync(options with
        {
            UseEnvironmentTelemetry = false,
            BackgroundTasks = null,
            Memory = options.Memory with { Storage = storage },
        });
        var input = JsonSerializer.SerializeToElement(new { url = server.Endpoint.ToString(), prompt = "Summarize this page." });
        var tool = Assert.Single(agent.Tools, value => value.Spec.Name == "web_fetch");
        var summary = await tool.InvokeAsync(input, new(agent, new("fetch", "web_fetch", input), new Dictionary<string, object?>()));
        Assert.Contains("Shared extracted fact.", summary, StringComparison.Ordinal);
        await agent.InvokeAsync("Remember this source preference.");
        await agent.ShutdownAsync();

        Assert.Equal(new[] { Harness.DefaultModel, "application/shared" }, resolved.Select(value => value.Identifier));
        var shared = resolved[1].Model;
        Assert.Same(shared, ExtractionModel(agent));
        Assert.Equal(2, shared.Requests.Count);
        Assert.Contains("source preference", shared.Requests[0].Messages.Single().Text, StringComparison.Ordinal);
        Assert.Contains("Remember this source preference.", shared.Requests[1].Messages.Single().Text, StringComparison.Ordinal);
        Assert.Equal(1, server.Calls);
        await agent.DisposeAsync();
        Assert.All(resolved, value => Assert.Equal(0, value.Model.DisposeCalls));
    }

    [Fact]
    public async Task ExplicitStoreExtractionKeepsItsModelEvenWhenWebFetchOverridesTheDefault()
    {
        var main = new TextModel("answer");
        var selected = new TextModel(Facts("Custom", "Custom store fact."));
        var webFetch = new TextModel(Facts("Wrong", "Do not use this model."));
        var storage = new InMemoryStorage();
        var store = new FileMemoryStore(new() { Name = "custom", Storage = storage.Namespace(""), Extraction = new() { Model = selected } });
        await using var agent = await Harness.CreateAsync(Options(main, storage) with
        {
            WebFetch = new() { Model = webFetch },
            Memory = new() { Stores = [store] },
        });

        await agent.InvokeAsync("Remember the custom store fact.");
        await agent.ShutdownAsync();

        Assert.Same(store, Assert.Single(agent.MemoryManager!.Stores));
        Assert.Same(selected, ExtractionModel(agent));
        Assert.Single(selected.Requests);
        Assert.Empty(webFetch.Requests);
        Assert.Contains("Custom store fact.", Encoding.UTF8.GetString((await storage.ReadAsync("custom.md"))!), StringComparison.Ordinal);
        await agent.DisposeAsync();
        Assert.Equal(0, selected.DisposeCalls);
        Assert.Equal(0, webFetch.DisposeCalls);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("options")]
    [InlineData("core")]
    public async Task NoWebFetchOverridePreservesTheExistingConcreteAuxiliaryPrecedence(string selection)
    {
        var main = new TextModel("answer");
        var auxiliary = new TextModel(Facts("Options", "Options auxiliary."));
        var coreAuxiliary = new TextModel(Facts("Core", "Core auxiliary."));
        var options = Options(main, new InMemoryStorage()) with
        {
            AuxModel = selection == "main" ? null : auxiliary,
            AgentOptions = selection == "core" ? new() { Model = main, AuxModel = coreAuxiliary } : null,
        };
        await using var agent = await Harness.CreateAsync(options);

        Assert.Same(selection == "main" ? main : selection == "options" ? auxiliary : coreAuxiliary, ExtractionModel(agent));
        await agent.DisposeAsync();
        Assert.Equal(0, main.DisposeCalls);
        Assert.Equal(0, auxiliary.DisposeCalls);
        Assert.Equal(0, coreAuxiliary.DisposeCalls);
    }

    [Theory]
    [InlineData("core")]
    [InlineData("plugin")]
    [InlineData("manager")]
    [InlineData("stores")]
    [InlineData("disabled")]
    public async Task NonDefaultMemoryPathsDoNotResolveTheConfiguredContentModel(string selection)
    {
        var main = new TextModel("answer");
        var store = new RecallStore();
        var manager = new MemoryManager(new() { Stores = [store] });
        var options = Options(main, new InMemoryStorage()) with
        {
            ModelFactory = _ => throw new InvalidOperationException("Memory must not construct a content model."),
            ModelResolver = _ => throw new InvalidOperationException("Memory must not resolve a content model."),
            WebFetch = new() { ModelIdentifier = "application/must-not-resolve" },
            AgentOptions = selection == "core" ? new() { Model = main, MemoryManager = manager }
                : selection == "plugin" ? new() { Model = main, Plugins = [manager] } : null,
            Memory = selection switch
            {
                "manager" => new() { Manager = manager, Directory = "", Stores = null! },
                "stores" => new() { Stores = [store], Directory = "" },
                "disabled" => new() { Enabled = false, Directory = "", Stores = null! },
                _ => new() { Directory = "", Stores = null! },
            },
        };
        await using var agent = await Harness.CreateAsync(options);

        if (selection == "disabled") Assert.Null(agent.MemoryManager);
        else
        {
            if (selection != "stores") Assert.Same(manager, agent.MemoryManager);
            Assert.Same(store, Assert.Single(agent.MemoryManager!.Stores));
        }
        await agent.DisposeAsync();
        Assert.Equal(0, main.DisposeCalls);
    }

    [Fact]
    public async Task InheritedChildRecallsWithoutExtractingOrAcquiringAnotherContentModel()
    {
        var main = new SequenceModel(Call("subagent", new { task = "Inspect the child task." }), Text("Child-only observation."), Text("parent answer"));
        var selected = new TextModel(Facts("Parent", "Parent memory fact."));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var agent = await Harness.CreateAsync(Options(main, new InMemoryStorage()) with
        {
            AgentOptions = new() { Model = main, Hooks = hooks },
            BuiltinTools = ["subagent"],
            WebFetch = new() { Model = selected },
            ModelResolver = _ => throw new InvalidOperationException("No child content model should be resolved."),
        });

        Assert.Equal("parent answer", (await agent.InvokeAsync("Delegate this task.")).ToString());
        await agent.ShutdownAsync();

        Assert.Equal(2, agents.Count);
        Assert.Same(agent, agents[0]);
        var childStore = Assert.Single(agents[1].MemoryManager!.Stores);
        Assert.False(childStore.Writable);
        Assert.Null(childStore.Extraction);
        Assert.False(childStore is IWritableMemoryStore);
        var extraction = Assert.Single(selected.Requests);
        Assert.DoesNotContain("Child-only observation.", extraction.Messages.Single().Text, StringComparison.Ordinal);
        Assert.Equal(3, main.Requests.Count);
        await agent.DisposeAsync();
        Assert.Equal(0, main.DisposeCalls);
        Assert.Equal(0, selected.DisposeCalls);
    }

    private static string PortableConfig(string identifier) => JsonSerializer.Serialize(new
    {
        builtinTools = new Dictionary<string, object> { ["*"] = false, ["web_fetch"] = new { transport = "direct", model = identifier } },
        builtinPlugins = Array.Empty<string>(),
        caching = false,
        contextManager = false,
        skills = false,
        session = false,
        memory = true,
    });

    private static string Facts(string heading, string fact) => JsonSerializer.Serialize(new[] { new { content = "# " + heading + "\n" + fact } });

    private static ModelStreamEvent[] Text(string value) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn),
    ];

    private static ModelStreamEvent[] Call(string name, object input) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new("call-" + name, name, JsonSerializer.SerializeToElement(input))),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse),
    ];

    private sealed class RecallStore : MemoryStore
    {
        public override string Name => "custom";
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(Array.Empty<MemoryEntry>());
        }
    }

    private sealed class SequenceModel(params ModelStreamEvent[][] turns) : IModel, IDisposable
    {
        public List<ModelRequest> Requests { get; } = [];
        public int DisposeCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    private static HarnessOptions Options(IModel model, InMemoryStorage storage) => new()
    {
        UseEnvironmentTelemetry = false,
        Model = model,
        BackgroundTasks = null,
        BuiltinTools = [],
        BuiltinPlugins = [],
        Skills = new() { Enabled = false },
        Context = new() { Enabled = false },
        Session = new() { Enabled = false },
        Memory = new() { Storage = storage },
    };

    private static IModel? ExtractionModel(Agent agent) => Assert.IsType<FileMemoryExtractionOptions>(Assert.Single(agent.MemoryManager!.Stores).Extraction).Model;

    private sealed class TextModel(string output) : IModel, IDisposable
    {
        public List<ModelRequest> Requests { get; } = [];
        public int DisposeCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: output);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
