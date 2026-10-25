using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bedrock;

namespace Strands.Harness.Tests;

public sealed class ConfigurationTests
{
    private const string Native = "\"builtinTools\":[],\"caching\":false,\"contextManager\":false,\"skills\":false,\"memory\":false";

    [Fact]
    public void EmptyConfigurationNormalizesToPortableDefaultsAndRoundTrips()
    {
        var config = HarnessAgentConfig.Parse("{}");
        var value = config.ToJsonElement();

        Assert.Equal("Strands harness", config.Name);
        Assert.Equal("bedrock/global.anthropic.claude-opus-5", config.Model);
        Assert.Equal("auto", value.GetProperty("effort").GetString());
        Assert.Equal(8, value.GetProperty("builtinTools").GetArrayLength());
        Assert.Equal(new[] { "todos", "environment" }, value.GetProperty("builtinPlugins").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(config.ToJson(), HarnessAgentConfig.Parse(config.ToJson()).ToJson());
        var options = config.ToOptions(new HarnessConfigBindings { ModelResolver = _ => new CaptureModel() });
        Assert.Equal(Harness.BuiltinToolNames, options.BuiltinTools);
    }

    [Theory]
    [InlineData("[]", "agent config")]
    [InlineData("{\"session\":{\"unknown\":true}}", "session.unknown")]
    [InlineData("{\"session\":null}", "session")]
    [InlineData("{\"builtinTools\":[\"read\",\"read\"]}", "builtinTools")]
    [InlineData("{\"builtinTools\":{\"*\":0}}", "builtinTools.*")]
    [InlineData("{\"builtinTools\":{\"shell\":{\"description\":\" \"}}}", "builtinTools.shell.description")]
    [InlineData("{\"builtinTools\":{\"subagent\":{\"maxDepth\":-1}}}", "builtinTools.subagent.maxDepth")]
    [InlineData("{\"builtinTools\":{\"programmatic_tool_caller\":{\"timeout\":0}}}", "builtinTools.programmatic_tool_caller.timeout")]
    [InlineData("{\"builtinPlugins\":[\"Todos\"]}", "builtinPlugins")]
    [InlineData("{\"tools\":[{\"kind\":\"plugin\",\"module\":\"tools\"}]}", "tools[0].kind")]
    [InlineData("{\"dependencies\":{\"python\":[\"a\\nb\"]}}", "dependencies.python")]
    [InlineData("{\"model\":\"one\",\"model\":\"two\"}", "model")]
    public void MalformedConfigurationReportsTheRelevantPath(string json, string path)
    {
        var error = Assert.Throws<ArgumentException>(() => HarnessAgentConfig.Parse(json));
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void UnknownTopLevelFieldsAreDroppedWithADiagnosticLikeBothUpstreamLoaders()
    {
        using var listener = new ConfigurationTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var config = HarnessAgentConfig.Parse("{\"futureZ\":{\"enabled\":true},\"name\":\"configured\",\"futureA\":42}");
            Assert.Equal("configured", config.Name);
            Assert.False(config.ToJsonElement().TryGetProperty("futureZ", out _));
            Assert.False(config.ToJsonElement().TryGetProperty("futureA", out _));
            Assert.Contains(listener.Messages, message => message.Contains("Ignoring unknown agent config keys: futureA, futureZ.", StringComparison.Ordinal));
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task BooleanMappingUsesEditsToDefaultsAndResolvesSessionAgainstProjectRoot()
    {
        var model = new CaptureModel();
        var root = Path.Combine(Path.GetTempPath(), "strands-config-" + Guid.NewGuid().ToString("N"));
        var config = HarnessAgentConfig.Parse("{" + Native.Replace("\"builtinTools\":[]", "\"builtinTools\":{\"*\":false,\"read\":true,\"write\":false}", StringComparison.Ordinal) + ",\"name\":\"configured\",\"description\":\"Config description\",\"instructions\":\"Be precise\",\"session\":{\"id\":\"My Session\",\"dir\":\"state\"},\"builtinPlugins\":[]}");
        var options = config.ToOptions(new HarnessConfigBindings { ModelResolver = name => { Assert.Equal(config.Model, name); return model; } }, root);
        try
        {
            Assert.Equal(Path.Combine(root, "state"), options.Session.Directory);
            var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
            Assert.Equal("configured", agent.Name);
            Assert.Equal("Config description", agent.Description);
            Assert.Equal("my-session", agent.SessionId);
            Assert.Equal(new[] { "read" }, agent.Tools.Select(tool => tool.Spec.Name));
            await agent.InvokeAsync("hello");
            Assert.EndsWith("Be precise", Assert.Single(model.Requests).SystemPrompt);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "state")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TypedBindingsResolveOnlyDeclaredComponentsAndCoreOptionsWin()
    {
        var model = new CaptureModel();
        var modelModule = new CaptureModel();
        var coreModel = new CaptureModel();
        var plugin = new Strands.Harness.Plugins.Todos();
        var tool = new FunctionTool(new("explicit_tool", "Bound tool", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("bound"));
        var sandbox = new TextOnlySandbox();
        var config = HarnessAgentConfig.Parse("{" + Native + ",\"modelModule\":{\"module\":\"project/models\",\"export\":\"main\",\"language\":\"python\",\"files\":[\"model.py\"],\"dependency\":\"example==1\",\"revision\":3},\"tools\":[{\"module\":\"project/tools\"}],\"plugins\":[{\"module\":\"project/plugin\"}],\"sandbox\":{\"module\":\"project/sandbox\"},\"builtinPlugins\":[],\"session\":false,\"agentConfig\":{\"maxTurns\":2,\"state\":{\"setting\":\"configured\"}}}");
        var options = config.ToOptions(new HarnessConfigBindings
        {
            ModelResolver = _ => model,
            ModelModuleResolver = _ => throw new InvalidOperationException("Explicit core model must win before resolution"),
            ToolResolver = reference => { Assert.Equal("default", reference.Export); return tool; },
            PluginResolver = _ => plugin,
            SandboxResolver = _ => sandbox,
            AgentOptions = new() { Model = coreModel, Name = "core", SystemPrompt = "core prompt", State = new Dictionary<string, JsonElement> { ["setting"] = JsonSerializer.SerializeToElement("core") } },
        });
        var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        await agent.InvokeAsync("hello");

        Assert.Same(coreModel, agent.Model);
        Assert.Equal("core", agent.Name);
        Assert.Equal("core prompt", agent.SystemPrompt);
        Assert.Equal("core", agent.State.Get<string>("setting"));
        Assert.Equal(new[] { "explicit_tool", "todo_write" }, agent.Tools.Select(item => item.Spec.Name));
        Assert.Empty(model.Requests);
        Assert.Empty(modelModule.Requests);
    }

    [Fact]
    public void ModelModuleBindingReceivesPreservedMetadataAndOverridesTheModelString()
    {
        var model = new CaptureModel();
        var config = HarnessAgentConfig.Parse("{" + Native + ",\"modelModule\":{\"module\":\"project/models\",\"export\":\"main\",\"language\":\"python\",\"files\":[\"model.py\"],\"dependency\":\"example==1\",\"revision\":3}}");
        var options = config.ToOptions(new()
        {
            ModelResolver = _ => throw new InvalidOperationException("module takes precedence"),
            ModelModuleResolver = reference =>
            {
                Assert.Equal("model", reference.Kind);
                Assert.Equal("project/models", reference.Module);
                Assert.Equal("main", reference.Export);
                Assert.Equal("python", reference.Language);
                Assert.Equal("model.py", Assert.Single(reference.Files));
                Assert.Equal("example==1", reference.Dependency);
                Assert.Equal(3, reference.ToJsonElement().GetProperty("revision").GetInt32());
                return model;
            },
        });
        Assert.Same(model, options.Model);
    }

    [Fact]
    public void MappingWithoutWildcardStartsWithTheCompletePortableDefaultSet()
    {
        var config = HarnessAgentConfig.Parse("{" + Native.Replace("\"builtinTools\":[]", "\"builtinTools\":{\"shell\":false,\"web_fetch\":false,\"web_search\":false,\"programmatic_tool_caller\":false,\"subagent\":false,\"read\":{\"media\":false}}", StringComparison.Ordinal) + "}");
        Assert.Equal(new[] { "read", "write", "edit" }, config.ToOptions(new() { ModelResolver = _ => new CaptureModel() }).BuiltinTools);
    }

    [Fact]
    public async Task SubagentMappingAppliesItsRealDepthConfiguration()
    {
        var config = HarnessAgentConfig.Parse("{" + Native.Replace("\"builtinTools\":[]", "\"builtinTools\":{\"*\":false,\"subagent\":{\"maxDepth\":0}}", StringComparison.Ordinal) + ",\"builtinPlugins\":[],\"session\":false}");
        var options = config.ToOptions(new() { ModelResolver = _ => new CaptureModel() });
        Assert.Equal(0, options.Subagent.MaxDepth);
        var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        var tool = Assert.IsAssignableFrom<IRichTool>(Assert.Single(agent.Tools));
        var input = JsonSerializer.SerializeToElement(new { task = "work" });
        var result = await tool.InvokeResultAsync(input, new(agent, new("delegate", "subagent", input), new Dictionary<string, object?>()));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.Contains("depth limit", result.Content);
    }

    [Fact]
    public async Task NativeBedrockConfigurationNeedsNoApplicationResolverAndOwnsItsLazyModel()
    {
        var config = HarnessAgentConfig.Parse("{" + Native + ",\"builtinPlugins\":[],\"session\":false}");
        var options = config.ToOptions(new());
        Assert.True(options.AgentOptions!.OwnsModel);
        var model = Assert.IsType<BedrockModel>(options.Model);
        Assert.Equal("global.anthropic.claude-opus-5", model.ModelId);
        var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        await agent.DisposeAsync();
        await using var stream = model.StreamAsync(new([])).GetAsyncEnumerator();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.MoveNextAsync());
    }

    [Fact]
    public async Task BoundSubagentsAreExposedAsOrdinaryAgentTools()
    {
        var child = new Agent(new() { Model = new CaptureModel(), Name = "specialist", Description = "A specialist" });
        var config = HarnessAgentConfig.Parse("{" + Native + ",\"subagents\":[{\"module\":\"specialists\"}],\"session\":false,\"builtinPlugins\":[]}");
        var options = config.ToOptions(new HarnessConfigBindings { ModelResolver = _ => new CaptureModel(), SubagentResolver = _ => child });
        var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        var tool = Assert.Single(agent.Tools);
        var input = JsonSerializer.SerializeToElement(new { task = "delegate" });
        Assert.Equal("answer", await tool.InvokeAsync(input, new(agent, new("delegate", tool.Spec.Name, input), new Dictionary<string, object?>())));
        Assert.Empty(child.Messages);
    }

    [Theory]
    [InlineData("\"agentConfig\":{\"unknown\":true}", "agentConfig.unknown")]
    public void UnsupportedFeaturesFailBeforeTheModelResolverIsCalled(string setting, string path)
    {
        var input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{" + Native + "}")!;
        var overrideValue = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{" + setting + "}")!;
        foreach (var item in overrideValue) input[item.Key] = item.Value;
        var config = HarnessAgentConfig.Parse(JsonSerializer.Serialize(input));
        var error = Assert.Throws<NotSupportedException>(() => config.ToOptions(new HarnessConfigBindings { ModelResolver = _ => throw new InvalidOperationException("must not resolve") }));
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void MissingBindingsAndUnknownModelsHaveActionableErrors()
    {
        var config = HarnessAgentConfig.Parse("{" + Native + ",\"model\":\"unknown/model\"}");
        Assert.Contains("Unknown model provider", Assert.Throws<ArgumentException>(() => config.ToOptions(new())).Message);
        var error = Assert.Throws<InvalidOperationException>(() => config.ToOptions(new() { ModelResolver = _ => throw new ArgumentException("Unknown provider") }));
        Assert.Contains("unknown/model", error.Message);
        Assert.IsType<ArgumentException>(error.InnerException);
        var tools = HarnessAgentConfig.Parse("{" + Native + ",\"tools\":[{\"module\":\"tools.py\"}]}");
        Assert.Contains("ToolResolver", Assert.Throws<InvalidOperationException>(() => tools.ToOptions(new() { ModelResolver = _ => new CaptureModel() })).Message);
    }

    [Fact]
    public void ConfigurationStateIsIndependentOfTheParsingDocumentAndReturnedCopies()
    {
        HarnessAgentConfig config;
        using (var document = JsonDocument.Parse("{" + Native + ",\"agentConfig\":{\"state\":{\"nested\":{\"value\":1}}}}")) config = HarnessAgentConfig.Normalize(document.RootElement);
        var first = config.ToOptions(new() { ModelResolver = _ => new CaptureModel() });
        Assert.Equal(1, first.AgentOptions!.State["nested"].GetProperty("value").GetInt32());
        Assert.Equal(config.ToJson(), HarnessAgentConfig.Parse(config.ToJson(indented: true)).ToJson());
    }

    private sealed class TextOnlySandbox : ISandbox
    {
        public ValueTask<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) => new("unused");
        public ValueTask WriteTextAsync(string path, string content, CancellationToken cancellationToken = default) => default;
    }

    private sealed class CaptureModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class ConfigurationTraceListener : TraceListener
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public override void Write(string? message) { if (message is not null) Messages.Enqueue(message); }
        public override void WriteLine(string? message) { if (message is not null) Messages.Enqueue(message); }
    }
}
