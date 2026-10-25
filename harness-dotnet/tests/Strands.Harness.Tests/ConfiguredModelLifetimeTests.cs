using System.Net;
using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Models;

namespace Strands.Harness.Tests;

public sealed class ConfiguredModelLifetimeTests
{
    [Fact]
    public async Task PortableNativeAllocationsAreTrackedSeparatelyFromCallerResources()
    {
        var options = HarnessAgentConfig.Parse("{\"model\":\"ollama/test\",\"caching\":false,\"builtinTools\":[],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}").ToOptions();
        await using var owner = new Agent(options.AgentOptions!);
        Assert.Same(Assert.Single(options.AgentOptions!.OwnedResources), Assert.Single(options.ConfiguredOwnedResources));
    }

    [Fact]
    public async Task FactoryFailureClosesTransferredNativeHttpButLeavesCallerResourcesUntouched()
    {
        var owned = new List<IAsyncDisposable>();
        using var handler = new Handler();
        var model = NativeModels.Create(ModelPolicy.Configure("ollama/test", HarnessEffort.Off, false, false, false), owned, () => new HttpClient(handler));
        var caller = new Resource();
        var options = new HarnessOptions
        {
            Model = model,
            ConfiguredModel = ("ollama/test", model),
            ConfiguredModelFeaturesApplied = true,
            ConfiguredOwnedResources = owned.ToArray(),
            AgentOptions = new() { Model = model, OwnsModel = true, OwnedResources = owned.Concat(new[] { caller }).ToArray() },
            BuiltinTools = [],
            BuiltinPlugins = ["invalid"],
            Session = new() { Enabled = false },
            Skills = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(options with { BackgroundTasks = null }));
        Assert.True(handler.Disposed);
        Assert.Equal(0, caller.Disposals);
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("effort")]
    [InlineData("caching")]
    [InlineData("search")]
    [InlineData("model")]
    [InlineData("factory")]
    public async Task ChangedResolvedPoliciesAreRejectedBeforeReusingTheConfiguredModel(string setting)
    {
        var calls = 0;
        var options = HarnessAgentConfig.Parse("{\"model\":\"google/gemini-3.5-flash\",\"effort\":\"low\",\"builtinTools\":[\"web_search\"],\"builtinPlugins\":[],\"session\":false,\"skills\":false,\"memory\":false,\"contextManager\":false}").ToOptions(new()
        {
            ModelFactory = _ => { calls++; return new Model(); },
        });
        options = setting switch
        {
            "tools" => options with { BuiltinTools = [] },
            "effort" => options with { Effort = HarnessEffort.Off },
            "caching" => options with { Caching = false },
            "search" => options with { WebSearch = new() { Provider = WebSearchProvider.Exa } },
            "model" => options with { ModelId = "openai/gpt-5.6" },
            _ => options with { ModelFactory = _ => new Model() },
        };
        var failure = await Assert.ThrowsAsync<ArgumentException>(() => Harness.CreateAsync(options with { BackgroundTasks = null }));
        Assert.Contains("ToOptions", failure.Message);
        Assert.Equal(1, calls);
    }

    private sealed class Resource : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class Model : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
