using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Testing;

namespace Strands.Harness.Tests;

[Collection("Harness configuration module environment")]
public sealed class HarnessConfigurationModuleTests
{
    private const string Minimal = "\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false";

    [Fact]
    public async Task NativeModulePatchesOverlayJsonInOrderAndConvenienceValuesRetainPrecedence()
    {
        var keys = new List<string>();
        var model = new Model();
        var hooks = new HookRegistry();
        var calls = 0;
        using var registration = hooks.Register<BeforeInvocationEvent>((_, _) => { calls++; return default; });
        var config = HarnessAgentConfig.Parse("{" + Minimal + ",\"name\":\"portable name\",\"agentConfig\":{\"maxTurns\":2,\"systemPrompt\":\"JSON prompt\",\"state\":{\"source\":\"JSON\"}},\"agentConfigModules\":{\"hooks\":{\"module\":\"project/hooks\",\"export\":\"registered\",\"language\":\"typescript\",\"revision\":7},\"limits\":{\"module\":\"project/limits\"}}}");
        var options = config.ToOptions(new()
        {
            ModelResolver = _ => model,
            AgentConfigModuleResolver = (key, reference) =>
            {
                keys.Add(key);
                Assert.Equal("agent-config", reference.Kind);
                if (key == "hooks")
                {
                    Assert.Equal("registered", reference.Export);
                    Assert.Equal("typescript", reference.Language);
                    Assert.Equal(7, reference.ToJsonElement().GetProperty("revision").GetInt32());
                    return core => core with { Hooks = hooks, MaxTurns = 3, Name = "module name", SystemPrompt = "module prompt" };
                }
                return core =>
                {
                    Assert.Equal(3, core.MaxTurns);
                    Assert.Equal("module prompt", core.SystemPrompt);
                    return core with { MaxTurns = 4, State = new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("module") } };
                };
            },
        });
        Assert.Equal(new[] { "hooks", "limits" }, keys);
        Assert.Equal(4, options.AgentOptions!.MaxTurns);
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal("portable name", agent.Name);
        Assert.Same(model, agent.Model);
        Assert.Equal("module", agent.State.Get<string>("source"));
        Assert.Equal("module prompt", agent.SystemPrompt);
        await agent.InvokeAsync("hello");
        Assert.Equal(1, calls);
        Assert.Equal(2, config.ToJsonElement().GetProperty("agentConfig").GetProperty("maxTurns").GetInt32());
    }

    [Fact]
    public async Task ExplicitCoreOptionsWinAfterModulesAreResolved()
    {
        var model = new Model();
        var patched = false;
        var core = new AgentOptions { Model = model, Name = "explicit", MaxTurns = 9, SystemPrompt = "explicit prompt" };
        var options = Config().ToOptions(new()
        {
            AgentOptions = core,
            ModelFactory = _ => throw new InvalidOperationException("The explicit model wins."),
            AgentConfigModuleResolver = (_, _) => value => { patched = true; return value with { MaxTurns = 2, Name = "module", SystemPrompt = "module" }; },
        });
        Assert.True(patched);
        Assert.Same(core, options.AgentOptions);
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });
        Assert.Equal("explicit", agent.Name);
        Assert.Equal("explicit prompt", agent.SystemPrompt);
        Assert.Equal(9, options.AgentOptions!.MaxTurns);
    }

    [Fact]
    public void MissingOrThrowingModuleBindingsFailBeforeModelAllocation()
    {
        var calls = 0;
        var bindings = new HarnessConfigBindings { ModelFactory = _ => { calls++; return new Model(); } };
        Assert.Contains("AgentConfigModuleResolver", Assert.Throws<NotSupportedException>(() => Config().ToOptions(bindings)).Message);
        var primary = new IOException("application module could not load");
        var failure = Assert.Throws<IOException>(() => Config().ToOptions(bindings with { AgentConfigModuleResolver = (_, _) => throw primary }));
        Assert.Same(primary, failure);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("model")]
    [InlineData("ownership")]
    [InlineData("resources")]
    public async Task FailedPatchClosesOnlyTheNewlyAllocatedNativeTransport(string change)
    {
        await using var server = new WebTestServer(_ => new(Encoding.UTF8.GetBytes("{\"done\":true}\n"), "application/x-ndjson"));
        var previous = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        Environment.SetEnvironmentVariable("OLLAMA_HOST", server.Endpoint.AbsoluteUri);
        IModel? captured = null;
        IReadOnlyList<IAsyncDisposable> resources = Array.Empty<IAsyncDisposable>();
        var borrowed = new Model();
        var primary = new IOException("native patch failed");
        try
        {
            void Bind() => Config("ollama/test").ToOptions(new()
            {
                AgentConfigModuleResolver = (_, _) => core =>
                {
                    captured = core.Model; resources = core.OwnedResources.ToArray(); Assert.True(core.OwnsModel); Assert.Single(resources);
                    return change switch
                    {
                        "throw" => throw primary,
                        "model" => core with { Model = borrowed },
                        "ownership" => core with { OwnsModel = false },
                        _ => core with { OwnedResources = Array.Empty<IAsyncDisposable>() },
                    };
                },
            });
            if (change == "throw") Assert.Same(primary, Assert.Throws<IOException>(Bind));
            else Assert.Contains("model identity or ownership", Assert.Throws<ArgumentException>(Bind).Message);
            Assert.NotNull(captured);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => { await foreach (var _ in captured.StreamAsync(new(new[] { Message.User("test") }), deadline.Token)) { } });
            Assert.Equal(0, server.Calls);
            Assert.Equal(0, borrowed.Disposals);
        }
        finally
        {
            foreach (var resource in resources) await resource.DisposeAsync();
            await borrowed.DisposeAsync();
            Environment.SetEnvironmentVariable("OLLAMA_HOST", previous);
        }
    }

    [Theory]
    [InlineData("model")]
    [InlineData("ownership")]
    [InlineData("resources")]
    public async Task ModulesCannotChangeTheModelOrItsOwnership(string change)
    {
        var original = new Model();
        var replacement = new Model();
        var resource = new Resource();
        var core = new AgentOptions { Model = original, OwnsModel = true, OwnedResources = new[] { resource } };
        var failure = Assert.Throws<ArgumentException>(() => Config().ToOptions(new()
        {
            AgentOptions = core,
            AgentConfigModuleResolver = (_, _) => value => change switch
            {
                "model" => value with { Model = replacement },
                "ownership" => value with { OwnsModel = false },
                _ => value with { OwnedResources = Array.Empty<IAsyncDisposable>() },
            },
        }));
        Assert.Contains("model identity or ownership", failure.Message);
        Assert.Equal(0, original.Disposals);
        Assert.Equal(0, replacement.Disposals);
        Assert.Equal(0, resource.Disposals);
        await resource.DisposeAsync();
        await original.DisposeAsync();
        await replacement.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullPatchOrResultHasAnActionableBindingError(bool nullPatch)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Config().ToOptions(new()
        {
            ModelResolver = _ => new Model(),
            AgentConfigModuleResolver = (_, _) => nullPatch ? null! : _ => null!,
        }));
        Assert.Contains("agentConfigModules.hooks", failure.Message);
    }

    private static HarnessAgentConfig Config(string model = "bedrock/global.anthropic.claude-opus-5") => HarnessAgentConfig.Parse("{" + Minimal + ",\"model\":\"" + model + "\",\"agentConfigModules\":{\"hooks\":{\"module\":\"project/hooks\"}}}");
    private sealed class Resource : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class Model : IModel, IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return default; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}

[CollectionDefinition("Harness configuration module environment", DisableParallelization = true)]
public sealed class HarnessConfigurationModuleEnvironmentCollection;
