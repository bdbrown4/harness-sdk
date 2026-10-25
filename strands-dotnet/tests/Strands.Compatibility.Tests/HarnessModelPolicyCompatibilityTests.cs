using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Models;
using Strands.Harness;
using Strands.Harness.Testing;
using HarnessFactory = Strands.Harness.Harness;

namespace Strands.Compatibility.Tests;

[Collection("Portable harness model environment")]
public sealed class HarnessModelPolicyCompatibilityTests
{
    [Fact]
    public async Task NativeMantleConstructionUsesTheAwsRegionChainWithoutRequestingCredentials()
    {
        var previous = Environment.GetEnvironmentVariable("AWS_REGION");
        Environment.SetEnvironmentVariable("AWS_REGION", "us-west-2");
        try
        {
            await using var agent = await HarnessFactory.CreateAsync(Options() with { BackgroundTasks = null, ModelId = "bedrock-mantle/openai.gpt-6-astra", Caching = false });
            Assert.Equal("openai.gpt-6-astra", Assert.IsType<OpenAIResponsesModel>(agent.Model).Metadata.ModelId);
        }
        finally { Environment.SetEnvironmentVariable("AWS_REGION", previous); }
    }

    [Fact]
    public async Task AFailedFactoryClosesNativeTransportsAcquiredDuringPortableBinding()
    {
        await using var server = new WebTestServer(_ => new(System.Text.Encoding.UTF8.GetBytes("{\"message\":{\"role\":\"assistant\",\"content\":\"done\"},\"done\":true}\n"), "application/x-ndjson"));
        var previous = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        Environment.SetEnvironmentVariable("OLLAMA_HOST", server.Endpoint.AbsoluteUri);
        try
        {
            var options = HarnessAgentConfig.Parse("{\"model\":\"ollama/test\",\"caching\":false,\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}").ToOptions();
            await Assert.ThrowsAsync<ArgumentException>(() => HarnessFactory.CreateAsync(options with { BackgroundTasks = null, BuiltinPlugins = ["invalid"] }));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var stream = options.Model!.StreamAsync(new([Message.User("unused")]), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.MoveNextAsync());
            Assert.Equal(0, server.Calls);
        }
        finally { Environment.SetEnvironmentVariable("OLLAMA_HOST", previous); }
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("effort")]
    [InlineData("caching")]
    [InlineData("search")]
    [InlineData("model")]
    [InlineData("factory")]
    public async Task PublicRecordEditsCannotSilentlyReuseAnAlreadyConfiguredPolicy(string setting)
    {
        var options = HarnessAgentConfig.Parse("{\"model\":\"google/gemini-3.5-flash\",\"effort\":\"low\",\"builtinTools\":[\"web_search\"],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}").ToOptions(new() { ModelFactory = _ => new Model() });
        options = setting switch
        {
            "tools" => options with { BuiltinTools = [] },
            "effort" => options with { Effort = HarnessEffort.Off },
            "caching" => options with { Caching = false },
            "search" => options with { WebSearch = new() { Provider = WebSearchProvider.Exa } },
            "model" => options with { ModelId = "openai/gpt-5.6" },
            _ => options with { ModelFactory = _ => new Model() },
        };
        Assert.Contains("ToOptions", (await Assert.ThrowsAsync<ArgumentException>(() => HarnessFactory.CreateAsync(options with { BackgroundTasks = null }))).Message);
    }

    [Theory]
    [InlineData("anthropic/claude-opus-5", "output_config")]
    [InlineData("openai/gpt-5.6", "reasoning")]
    [InlineData("google/gemini-3.5-flash", "generationConfig")]
    public async Task PortablePoliciesReachTheTypedFactoryWithoutReconstruction(string identifier, string reasoningField)
    {
        var calls = new List<HarnessModelConfiguration>();
        var model = new Model();
        var config = HarnessAgentConfig.Parse("{\"model\":\"" + identifier + "\",\"effort\":\"low\",\"caching\":false,\"builtinTools\":[\"web_search\"],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}");
        await using var agent = await HarnessFactory.CreateAsync(config.ToOptions(new()
        {
            ModelFactory = policy => { calls.Add(policy); return model; },
            ModelResolver = _ => throw new InvalidOperationException("Options-aware factory must win."),
        }) with
        { BackgroundTasks = null });
        var policy = Assert.Single(calls);
        Assert.Equal(identifier, policy.Identifier);
        Assert.Equal("low", policy.Effort);
        Assert.False(policy.Caching);
        Assert.True(policy.NativeWebSearch);
        Assert.True(policy.Parameters!.Value.TryGetProperty(reasoningField, out _));
        Assert.Single(policy.Parameters.Value.GetProperty("tools").EnumerateArray());
        Assert.Same(model, agent.Model);
        Assert.Empty(agent.Tools);
        Assert.Equal("done", (await Bounded(agent.InvokeAsync("question"))).ToString());
    }

    [Fact]
    public async Task ParentChildAndGrandchildReceiveFreshBorrowedModelsWithTheSamePolicy()
    {
        var models = new List<Model>();
        var policies = new List<HarnessModelConfiguration>();
        var agent = await HarnessFactory.CreateAsync(Options() with
        {
            BackgroundTasks = null,
            ModelId = "anthropic/claude-opus-5",
            Effort = HarnessEffort.Low,
            Caching = false,
            BuiltinTools = ["subagent", "web_search"],
            ModelFactory = policy => { policies.Add(policy); var model = new Model { DelegateFirst = models.Count == 1 }; models.Add(model); return model; },
        });
        try
        {
            var tool = Assert.Single(agent.Tools);
            var input = JsonSerializer.SerializeToElement(new { task = "inspect" });
            Assert.Equal("done", await Bounded(tool.InvokeAsync(input, new(agent, new("delegate", "subagent", input), new Dictionary<string, object?>())).AsTask()));
            Assert.Equal(3, models.Count);
            Assert.Equal(3, policies.Count);
            Assert.All(policies, policy => { Assert.Equal("low", policy.Effort); Assert.False(policy.Caching); Assert.True(policy.NativeWebSearch); });
            Assert.Empty(models[0].Requests);
            Assert.Equal(2, models[1].Requests.Count);
            Assert.Single(models[2].Requests);
            Assert.NotSame(models[1].Requests[0].Messages, models[2].Requests[0].Messages);
        }
        finally { await agent.DisposeAsync(); }
        Assert.All(models, model => Assert.Equal(0, model.DisposeCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuxiliaryFactoriesUseOffAndNoCachingWhileExplicitModelsWin(bool explicitAuxiliary)
    {
        var policies = new List<HarnessModelConfiguration>();
        var auxiliary = new Model();
        await using var agent = await HarnessFactory.CreateAsync(Options() with
        {
            BackgroundTasks = null,
            ModelId = "anthropic/claude-opus-5",
            Effort = HarnessEffort.Low,
            Caching = false,
            BuiltinTools = ["web_fetch"],
            WebFetch = new() { Transport = Strands.Harness.Tools.WebFetchTransport.Direct },
            AuxModel = explicitAuxiliary ? auxiliary : null,
            ModelFactory = policy => { policies.Add(policy); return new Model(); },
        });
        Assert.Equal(explicitAuxiliary ? 1 : 2, policies.Count);
        Assert.Equal("low", policies[0].Effort);
        if (!explicitAuxiliary)
        {
            Assert.Equal("anthropic/claude-haiku-4-5-20251001", policies[1].Identifier);
            Assert.Null(policies[1].Effort);
            Assert.False(policies[1].Caching);
            Assert.False(policies[1].NativeWebSearch);
        }
    }

    [Fact]
    public async Task PortableTrueCachingRemainsAutomaticButTypedTrueIsExplicit()
    {
        HarnessModelConfiguration? configured = null;
        var config = HarnessAgentConfig.Parse("{\"model\":\"ollama/llama3\",\"caching\":true,\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}");
        await using var agent = await HarnessFactory.CreateAsync(config.ToOptions(new() { ModelFactory = policy => { configured = policy; return new Model(); } }) with { BackgroundTasks = null });
        Assert.NotNull(configured);
        Assert.False(configured.Caching);
        var calls = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => HarnessFactory.CreateAsync(Options() with
        {
            BackgroundTasks = null,
            ModelId = "ollama/llama3",
            Caching = true,
            ModelFactory = _ => { calls++; return new Model(); },
        }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AStringAuxiliaryFallbackRebuildsTheSameIdentifierWithReasoningAndCachingOff()
    {
        var policies = new List<HarnessModelConfiguration>();
        await using var agent = await HarnessFactory.CreateAsync(Options() with
        {
            BackgroundTasks = null,
            ModelId = "ollama/llama3",
            Caching = false,
            BuiltinTools = ["web_fetch"],
            WebFetch = new() { Transport = Strands.Harness.Tools.WebFetchTransport.Direct },
            ModelFactory = policy => { policies.Add(policy); return new Model(); },
        });
        Assert.Equal(2, policies.Count);
        Assert.All(policies, policy => { Assert.Equal("ollama/llama3", policy.Identifier); Assert.Null(policy.Effort); Assert.False(policy.Caching); });
    }

    [Fact]
    public async Task ExplicitUnsupportedNativeSearchFailsBeforeCallingTheFactory()
    {
        var calls = 0;
        await Assert.ThrowsAsync<NotSupportedException>(() => HarnessFactory.CreateAsync(Options() with
        {
            BackgroundTasks = null,
            ModelId = HarnessFactory.DefaultModel,
            BuiltinTools = ["web_search"],
            ModelFactory = _ => { calls++; return new Model(); },
        }));
        Assert.Equal(0, calls);
    }

    private static HarnessOptions Options() => new()
    {
        BuiltinTools = [],
        BuiltinPlugins = [],
        Session = new() { Enabled = false },
        Memory = new() { Enabled = false },
        Context = new() { Enabled = false },
        Skills = new() { Enabled = false },
    };

    private static async Task<T> Bounded<T>(Task<T> operation)
    {
        Assert.Same(operation, await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(30))));
        return await operation;
    }

    private sealed class Model : IModel, IAsyncDisposable
    {
        public List<ModelRequest> Requests { get; } = [];
        public bool DelegateFirst { get; init; }
        public int DisposeCalls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (DelegateFirst && Requests.Count == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("nested", "subagent", JsonSerializer.SerializeToElement(new { task = "nested inspect", tools = Array.Empty<string>() })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }
}

[CollectionDefinition("Portable harness model environment", DisableParallelization = true)]
public sealed class PortableHarnessModelEnvironmentCollection;
