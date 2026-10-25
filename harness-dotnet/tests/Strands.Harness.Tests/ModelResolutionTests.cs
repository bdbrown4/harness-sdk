using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Testing;
using Strands.Harness.Tools;

namespace Strands.Harness.Tests;

public sealed class ModelResolutionTests
{
    [Fact]
    public async Task ReplacingAConfiguredModelCannotRetainItsNativeSearchCapability()
    {
        var config = HarnessAgentConfig.Parse("{\"model\":\"google/gemini-3.5-flash\",\"builtinTools\":[\"web_search\"],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}");
        var options = config.ToOptions(new() { ModelFactory = _ => new QuietModel() });
        await Assert.ThrowsAsync<NotSupportedException>(() => Harness.CreateAsync(options with
        {
            BackgroundTasks = null,
            AgentOptions = options.AgentOptions! with { Model = new QuietModel() },
        }));
    }

    [Fact]
    public async Task DelegatesResolveFreshModelsWithTheInheritedPolicy()
    {
        var policies = new List<HarnessModelConfiguration>();
        var models = new List<QuietModel>();
        await using var agent = await Harness.CreateAsync(Isolated() with
        {
            BackgroundTasks = null,
            ModelId = "anthropic/claude-opus-5",
            Effort = HarnessEffort.Low,
            Caching = false,
            BuiltinTools = ["subagent"],
            ModelFactory = policy => { policies.Add(policy); var model = new QuietModel(); models.Add(model); return model; },
        });
        var tool = Assert.Single(agent.Tools);
        var input = JsonSerializer.SerializeToElement(new { task = "Inspect", tools = Array.Empty<string>() });
        Assert.Equal("done", await tool.InvokeAsync(input, new(agent, new("delegate", "subagent", input), new Dictionary<string, object?>())));
        Assert.Equal(2, policies.Count);
        Assert.All(policies, policy => { Assert.Equal("low", policy.Effort); Assert.False(policy.Caching); Assert.False(policy.NativeWebSearch); });
        Assert.Empty(models[0].Requests);
        Assert.Single(models[1].Requests);
    }

    [Fact]
    public async Task ExaSearchIsInheritedIndependentlyOfTheSelectableToolAxis()
    {
        var model = new QuietModel();
        await using var agent = await Harness.CreateAsync(Isolated() with
        {
            BackgroundTasks = null,
            Model = model,
            BuiltinTools = ["subagent", "web_search"],
            WebSearch = new() { Provider = WebSearchProvider.Exa, Exa = new() { ApiKey = "" } },
        });
        var tool = agent.Tools.Single(tool => tool.Spec.Name == "subagent");
        var selectable = tool.Spec.InputSchema.GetProperty("properties").GetProperty("tools").GetProperty("items").GetProperty("enum").EnumerateArray().Select(item => item.GetString());
        Assert.DoesNotContain("web_search", selectable);
        var input = JsonSerializer.SerializeToElement(new { task = "Inspect", tools = Array.Empty<string>() });
        Assert.Equal("done", await tool.InvokeAsync(input, new(agent, new("delegate", "subagent", input), new Dictionary<string, object?>())));
        Assert.Equal("web_search", Assert.Single(Assert.Single(model.Requests).Tools!).Name);
    }

    [Fact]
    public async Task PortablePolicyBindingConstructsOnceAndCarriesNativeSearch()
    {
        var model = new QuietModel();
        var configured = new List<HarnessModelConfiguration>();
        var config = HarnessAgentConfig.Parse("{\"model\":\"google/gemini-3.5-flash\",\"effort\":\"low\",\"caching\":false,\"builtinTools\":[\"web_search\"],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}");
        var options = config.ToOptions(new() { ModelFactory = policy => { configured.Add(policy); return model; } });
        await using var agent = await Harness.CreateAsync(options with { BackgroundTasks = null });

        var policy = Assert.Single(configured);
        Assert.Equal("low", policy.Effort);
        Assert.False(policy.Caching);
        Assert.True(policy.NativeWebSearch);
        Assert.True(policy.Parameters!.Value.GetProperty("tools")[0].TryGetProperty("googleSearch", out _));
        Assert.Empty(agent.Tools);
        Assert.Same(model, agent.Model);
    }

    [Fact]
    public void InvalidPortablePolicyFailsBeforeResolvingComponents()
    {
        var calls = 0;
        var config = HarnessAgentConfig.Parse("{\"model\":\"google/gemini-3.5-flash\",\"effort\":\"max\",\"builtinTools\":[],\"tools\":[{\"module\":\"tool\"}],\"caching\":false}");
        var error = Assert.Throws<ArgumentException>(() => config.ToOptions(new()
        {
            ModelFactory = _ => { calls++; return new QuietModel(); },
            ToolResolver = _ => { calls++; throw new InvalidOperationException("Must not resolve components."); },
        }));
        Assert.Contains("max", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OptionsAwareFactoryReceivesMappedPolicyAndWinsOverLegacyResolver()
    {
        var model = new QuietModel();
        HarnessModelConfiguration? configuration = null;
        await using var agent = await Harness.CreateAsync(Isolated() with
        {
            BackgroundTasks = null,
            ModelId = "anthropic/claude-opus-5",
            Effort = HarnessEffort.Low,
            Caching = true,
            ModelFactory = options => { configuration = options; return model; },
            ModelResolver = _ => throw new InvalidOperationException("Legacy resolver must not win."),
        });
        Assert.Same(model, agent.Model);
        Assert.NotNull(configuration);
        Assert.Equal("low", configuration.Effort);
        Assert.Equal(128000, configuration.MaxTokens);
        Assert.True(configuration.CacheConfig!.Tools);
        Assert.Equal("low", configuration.Parameters!.Value.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task SuppliedModelWinsWithoutCallingFactories()
    {
        var model = new QuietModel();
        await using var agent = await Harness.CreateAsync(Isolated() with
        {
            BackgroundTasks = null,
            Model = model,
            ModelId = "ignored/provider",
            Effort = HarnessEffort.High,
            Caching = true,
            ModelFactory = _ => throw new InvalidOperationException("Factory must not run."),
            ModelResolver = _ => throw new InvalidOperationException("Resolver must not run."),
        });
        Assert.Same(model, agent.Model);
    }

    [Fact]
    public async Task ExplicitExaSelectionRegistersAnOrdinaryWorkingTool()
    {
        await using var server = new McpTestServer { ToolName = "web_search_exa", ToolResponseText = "Title: Result\nURL: https://example.test\nHighlights:\ntext" };
        await using var agent = await Harness.CreateAsync(Isolated() with
        {
            BackgroundTasks = null,
            Model = new QuietModel(),
            BuiltinTools = ["web_search"],
            WebSearch = new() { Provider = WebSearchProvider.Exa, Exa = new() { Endpoint = server.Endpoint, ApiKey = "fixture" } },
        });
        var tool = Assert.Single(agent.Tools);
        var input = JsonSerializer.SerializeToElement(new { query = "question" });
        Assert.Equal("Sources:\n1. Result — https://example.test\n   text", await tool.InvokeAsync(input, new(agent, new("call", "web_search", input), new Dictionary<string, object?>())));
    }

    private static HarnessOptions Isolated() => new()
    {
        BuiltinTools = [],
        BuiltinPlugins = [],
        Session = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Skills = new() { Enabled = false },
    };
    private sealed class QuietModel : IModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
