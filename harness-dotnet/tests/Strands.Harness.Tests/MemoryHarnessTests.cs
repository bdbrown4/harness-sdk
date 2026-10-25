using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;
using Strands.Agents.Memory;
using Strands.Agents.Models;
using Strands.Agents.Storage;

namespace Strands.Harness.Tests;

public sealed class MemoryHarnessTests
{
    [Fact]
    public async Task OmittedMainModelUsesALazyOwnedHaikuExtractor()
    {
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Memory = new() { Storage = new InMemoryStorage() },
        });

        Assert.Equal("global.anthropic.claude-opus-5", Assert.IsType<BedrockModel>(agent.Model).ModelId);
        Assert.Equal("global.anthropic.claude-haiku-4-5-20251001-v1:0", Assert.IsType<BedrockModel>(ExtractionModel(agent)).ModelId);
        Assert.NotSame(agent.Model, ExtractionModel(agent));
    }

    [Fact]
    public async Task PortableStringModelDerivesTheProviderSmallModelThroughTheSameResolver()
    {
        var identifiers = new List<string>();
        var config = HarnessAgentConfig.Parse("""
            {"model":"bedrock/global.anthropic.claude-opus-5","builtinTools":[],"builtinPlugins":[],
             "caching":false,"contextManager":false,"skills":false,"session":false,"memory":true}
            """);
        var options = config.ToOptions(new() { ModelResolver = identifier => { identifiers.Add(identifier); return new TextModel(identifier); } });
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null, Memory = options.Memory with { Storage = new InMemoryStorage() } });

        Assert.Equal(new[] { "bedrock/global.anthropic.claude-opus-5", "bedrock/global.anthropic.claude-haiku-4-5-20251001-v1:0" }, identifiers);
        Assert.NotSame(agent.Model, ExtractionModel(agent));
    }

    [Fact]
    public async Task SuppliedRouterUsesItsConcreteDefaultForAuxiliaryExtraction()
    {
        var first = new TextModel("first");
        var router = new RoutingModel([new(first), new(new TextModel("second"))]);
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = router,
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Memory = new() { Storage = new InMemoryStorage() },
        });

        Assert.Same(first, ExtractionModel(agent));
    }

    [Theory]
    [InlineData("bedrock/us.openai.gpt-6-astra", "bedrock/us.openai.gpt-5.6-luna")]
    [InlineData("bedrock/us.amazon.nova-pro-v1:0", "bedrock/amazon.nova-lite-v1:0")]
    [InlineData("bedrock/meta.llama3-3-70b-instruct-v1:0", "bedrock/meta.llama3-2-3b-instruct-v1:0")]
    [InlineData("bedrock/mistral.mistral-large-2407-v1:0", "bedrock/mistral.mistral-small-2402-v1:0")]
    [InlineData("google/gemini-3.5-pro", "google/gemini-3.5-flash")]
    [InlineData("bedrock-mantle/openai.gpt-6-astra", "bedrock-mantle/openai.gpt-5.6-luna")]
    public async Task PortableAuxiliaryModelFamiliesRemainProviderAlignedAndCallerOwned(string main, string small)
    {
        var models = new Dictionary<string, TextModel>(StringComparer.Ordinal);
        var json = "{\"model\":\"" + main + "\",\"builtinTools\":[],\"builtinPlugins\":[],\"caching\":false,\"contextManager\":false,\"skills\":false,\"session\":false,\"memory\":true}";
        var options = HarnessAgentConfig.Parse(json).ToOptions(new() { ModelResolver = identifier => models[identifier] = new TextModel(identifier) });
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null, Memory = options.Memory with { Storage = new InMemoryStorage() } });

        Assert.Same(models[small], ExtractionModel(agent));
        Assert.Equal(2, models.Count);
        await agent.DisposeAsync();
        Assert.All(models.Values, model => Assert.False(model.Disposed));
    }

    [Fact]
    public async Task ExplicitAuxiliaryAndReplacementCoreModelsOverridePortableStringProvenance()
    {
        var configured = new TextModel("configured");
        var replacement = new TextModel("replacement");
        var auxiliary = new TextModel("auxiliary");
        var config = HarnessAgentConfig.Parse("""
            {"builtinTools":[],"builtinPlugins":[],"caching":false,"contextManager":false,"skills":false,"session":false,"memory":true}
            """);
        var options = config.ToOptions(new() { ModelResolver = _ => configured });
        await using var agent = await Harness.CreateAsync(options with
        {
            BackgroundTasks = null,
            AgentOptions = options.AgentOptions! with { Model = replacement, AuxModel = auxiliary },
            Memory = options.Memory with { Storage = new InMemoryStorage() },
        });
        Assert.Same(auxiliary, ExtractionModel(agent));
        await using var changed = await Harness.CreateAsync(options with
        {
            BackgroundTasks = null,
            AgentOptions = options.AgentOptions! with { Model = replacement },
            Memory = options.Memory with { Storage = new InMemoryStorage() },
        });
        Assert.Same(replacement, ExtractionModel(changed));
    }

    [Fact]
    public async Task DefaultMemoryUsesTheSelectedBinaryFilesystemAndFlushesExtractedFactsAtShutdown()
    {
        var files = new SandboxStorageTests.RemoteFiles();
        var model = new TextModel("answer");
        var auxiliary = new TextModel("[{\"content\":\"# Preferences\\nPrefers Café coffee.\"}]");
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, AuxModel = auxiliary, FileSystem = files },
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.NotNull(agent.MemoryManager);
        Assert.Equal("search_memory", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal("answer", (await agent.InvokeAsync("Remember that I prefer Café coffee.")).ToString());
        await agent.ShutdownAsync();

        var content = await files.ReadTextForAssertionAsync("./.agent/memory/preferences.md");
        Assert.Contains("Prefers Café coffee.", content, StringComparison.Ordinal);
        Assert.Single(auxiliary.Requests);
        Assert.All(files.Accesses, path => Assert.StartsWith("./.agent/memory", path, StringComparison.Ordinal));
        Assert.Equal(0, files.TextReads);
    }

    [Fact]
    public async Task TextOnlySandboxCannotAcquireHostStorageWhenDefaultMemoryIsEnabled()
    {
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new TextModel("unused"),
            Sandbox = new TextOnlySandbox(),
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        }));
        Assert.Contains("IFileSystem", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessBuiltMemoryBecomesATypedReadOnlyManagerInDelegates()
    {
        var store = new WritableStore();
        var model = new SequenceModel(Call("subagent", new { task = "inspect" }), Call("backend_write", new { }), Text("child"), Text("parent"));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks },
            BuiltinTools = ["subagent"],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Memory = new() { Stores = [store] },
        });

        Assert.Equal("parent", (await parent.InvokeAsync("delegate")).ToString());

        var child = agents[1];
        Assert.NotNull(child.MemoryManager);
        var readOnly = Assert.Single(child.MemoryManager.Stores);
        Assert.False(readOnly.Writable);
        Assert.False(readOnly is IWritableMemoryStore);
        Assert.False(readOnly is IMessageMemoryStore);
        Assert.Null(readOnly.Extraction);
        Assert.Empty(readOnly.GetTools());
        Assert.DoesNotContain(child.Tools, tool => tool.Spec.Name == "backend_write");
        Assert.Contains(child.Tools, tool => tool.Spec.Name == "search_memory");
        Assert.Equal(0, store.Writes);
        Assert.Contains(child.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), result => result.Status == ToolResultStatus.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExplicitManagerIsNotForwardedToDefaultDelegates(bool suppliedAsPlugin)
    {
        var manager = new MemoryManager(new() { Stores = [new WritableStore()] });
        var model = new SequenceModel(Call("subagent", new { task = "inspect" }), Text("child"), Text("parent"));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks, MemoryManager = suppliedAsPlugin ? null : manager, Plugins = suppliedAsPlugin ? [manager] : [] },
            BuiltinTools = ["subagent"],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
        });

        Assert.Equal("parent", (await parent.InvokeAsync("delegate")).ToString());

        Assert.Same(manager, parent.MemoryManager);
        Assert.Null(agents[1].MemoryManager);
        Assert.DoesNotContain(agents[1].Tools, tool => tool.Spec.Name is "search_memory" or "backend_write");
    }

    [Fact]
    public async Task ExplicitStorageAndCoreManagerWorkWithoutGrantingTextOnlySandboxHostAccess()
    {
        var storage = new InMemoryStorage();
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = new TextModel("unused"),
            Sandbox = new TextOnlySandbox(),
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Memory = new() { Storage = storage },
        });
        await Assert.IsAssignableFrom<IWritableMemoryStore>(Assert.Single(agent.MemoryManager!.Stores)).AddAsync("# Preferences\nPrefers coffee.");
        Assert.Contains("Prefers coffee.", System.Text.Encoding.UTF8.GetString((await storage.ReadAsync("preferences.md"))!), StringComparison.Ordinal);
        var manager = new MemoryManager(new() { Stores = [new WritableStore()], SearchTool = new() { Name = "explicit_search" } });
        await using var explicitAgent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = new TextModel("unused"), MemoryManager = manager },
            Sandbox = new TextOnlySandbox(),
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Memory = new() { Directory = "", Stores = null! },
        });
        Assert.Same(manager, explicitAgent.MemoryManager);
        Assert.Contains(explicitAgent.Tools, tool => tool.Spec.Name == "explicit_search");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableMemoryStoreBindingsResolveEvenWhenMemoryIsDisabled(bool enabled)
    {
        var store = new WritableStore();
        var resolved = 0;
        var root = Path.Combine(Path.GetTempPath(), "strands-memory-config");
        var json = "{\"builtinTools\":[],\"builtinPlugins\":[],\"caching\":false,\"contextManager\":false,\"skills\":false,\"session\":false,\"memory\":" + (enabled ? "{\"dir\":\"./facts\"}" : "false") + ",\"memoryStores\":[{\"module\":\"application/facts\",\"export\":\"store\"}]}";
        var options = HarnessAgentConfig.Parse(json).ToOptions(new()
        {
            ModelResolver = _ => new TextModel("unused"),
            MemoryStoreResolver = reference => { resolved++; Assert.Equal("memory-store", reference.Kind); Assert.Equal("store", reference.Export); return store; },
        }, root);

        Assert.Equal(1, resolved);
        Assert.Equal(enabled, options.Memory.Enabled);
        if (enabled)
        {
            Assert.Same(store, Assert.Single(options.Memory.Stores));
            Assert.Equal(Path.Combine(root, "facts"), options.Memory.Directory);
        }
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal(enabled, agent.MemoryManager is not null);
    }

    [Fact]
    public async Task AutomaticMemoryRecallRunsOnEveryModelTurnWithoutChangingDurableMessages()
    {
        var store = new WritableStore();
        var model = new SequenceModel(Call("echo", new { }, "Consult the persisted preference."), Text("answer"));
        var echo = new FunctionTool(new("echo", "Return text", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new("tool result"));
        await using var agent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            Model = model,
            Tools = [echo],
            BuiltinTools = [],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Memory = new() { Stores = [store] },
        });

        Assert.Equal("answer", (await agent.InvokeAsync("question")).ToString());

        Assert.Equal(2, store.Searches);
        Assert.All(model.Requests, request => Assert.Contains(request.Messages, message => message.Text.Contains("Persisted preference", StringComparison.Ordinal)));
        Assert.DoesNotContain(agent.Messages, message => message.Text.Contains("Persisted preference", StringComparison.Ordinal));
        Assert.DoesNotContain(agent.Tools, tool => tool.Spec.Name == "add_memory");
    }

    [Fact]
    public async Task ExcludingAllChildToolsRetainsReadOnlyMemoryInjectionWithoutRegeneratingItsSearchTool()
    {
        var model = new SequenceModel(Call("subagent", new { task = "inspect", tools = Array.Empty<string>() }), Text("child"), Text("parent"));
        var agents = new List<Agent>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { agents.Add(value.Agent); return default; });
        await using var parent = await Harness.CreateAsync(new()
        {
            BackgroundTasks = null,
            AgentOptions = new() { Model = model, Hooks = hooks },
            BuiltinTools = ["subagent"],
            BuiltinPlugins = [],
            Skills = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            Memory = new() { Stores = [new WritableStore()] },
        });

        Assert.Equal("parent", (await parent.InvokeAsync("delegate")).ToString());

        Assert.NotNull(agents[1].MemoryManager);
        Assert.Empty(agents[1].Tools);
        Assert.Contains("Persisted preference", model.Requests[1].Messages.Last().Text, StringComparison.Ordinal);
        Assert.DoesNotContain(agents[1].Messages, message => message.Text.Contains("Persisted preference", StringComparison.Ordinal));
    }

    private sealed class TextOnlySandbox : ISandbox
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Host filesystem access is forbidden.");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Host filesystem access is forbidden.");
    }

    private static IModel? ExtractionModel(Agent agent) => Assert.IsType<FileMemoryExtractionOptions>(Assert.Single(agent.MemoryManager!.Stores).Extraction).Model;

    private sealed class WritableStore : MemoryStore, IWritableMemoryStore, IMessageMemoryStore
    {
        public int Writes { get; private set; }
        public int Searches { get; private set; }
        public override string Name => "custom";
        public override bool Writable => true;
        public override MemoryExtractionOptions? Extraction => new() { Extractor = new EmptyExtractor() };
        public override ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int maxSearchResults, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Searches++; return new((IReadOnlyList<MemoryEntry>)new[] { new MemoryEntry("Persisted preference") });
        }
        public ValueTask AddAsync(string content, JsonElement? metadata = null, CancellationToken cancellationToken = default) { Writes++; return default; }
        public ValueTask AddMessagesAsync(IReadOnlyList<Message> messages, IReadOnlyList<long> sequenceNumbers, CancellationToken cancellationToken = default) { Writes++; return default; }
        public override IReadOnlyList<ITool> GetTools() => [new FunctionTool(new("backend_write", "Write through the backend", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { Writes++; return new("written"); })];
    }
    private sealed class EmptyExtractor : IExtractor
    {
        public ValueTask<IReadOnlyList<ExtractionResult>> ExtractAsync(IReadOnlyList<Message> messages, ExtractionContext? context = null, CancellationToken cancellationToken = default) => new((IReadOnlyList<ExtractionResult>)Array.Empty<ExtractionResult>());
    }
    private static ModelStreamEvent[] Text(string value) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: value), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static ModelStreamEvent[] Call(string name, object input, string? text = null)
    {
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        var index = 0;
        if (text is not null)
        {
            events.Add(new ModelContentBlockStartEvent(index)); events.Add(new ModelContentBlockDeltaEvent(index, Text: text)); events.Add(new ModelContentBlockStopEvent(index)); index++;
        }
        events.Add(new ModelContentBlockStartEvent(index, new("call-" + name, name, JsonSerializer.SerializeToElement(input))));
        events.Add(new ModelContentBlockStopEvent(index)); events.Add(new ModelMessageStopEvent(StopReasons.ToolUse));
        return events.ToArray();
    }
    private sealed class SequenceModel(params ModelStreamEvent[][] turns) : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Requests.Add(request);
            foreach (var item in turns[Requests.Count - 1]) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        }
    }

    private sealed class TextModel(string output) : IModel, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request);
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: output); yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
