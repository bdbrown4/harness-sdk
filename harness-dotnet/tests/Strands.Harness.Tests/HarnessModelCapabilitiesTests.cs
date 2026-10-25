using Strands.Agents;
using Strands.Agents.Models;

namespace Strands.Harness.Tests;

public sealed class HarnessModelCapabilitiesTests
{
    [Fact]
    public void OmittedAndNullInputsClassifyTheDefaultWithoutConstructingIt()
    {
        Assert.True(HarnessModelCapabilities.SupportsThinking());
        Assert.True(HarnessModelCapabilities.SupportsThinking(null));
        Assert.True(HarnessModelCapabilities.SupportsThinking(default));
        Assert.True(HarnessModelCapabilities.SupportsThinking((string?)null));
        IModel? missing = null;
        Assert.True(HarnessModelCapabilities.SupportsThinking(missing));
        Assert.True(HarnessModelCapabilities.SupportsThinking<PoisonModel>(null));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch());
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(null));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(default));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch((string?)null));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(missing));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch<PoisonModel>(null));
        Assert.True(HarnessModelCapabilities.SupportsThinking(Harness.DefaultModel));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(Harness.DefaultModel));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown/model")]
    [InlineData("OpenAI/gpt-5.6")]
    public void EmptyAndUnknownIdentifiersHaveNoAdvertisedCapabilities(string identifier)
    {
        Assert.False(HarnessModelCapabilities.SupportsThinking(identifier));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(identifier));
    }

    [Theory]
    [InlineData("bedrock/us.anthropic.claude-haiku-3-5-20241022-v1:0", false)]
    [InlineData("anthropic/claude-sonnet-4-5", true)]
    [InlineData("bedrock/amazon.nova-2-lite-v1:0", true)]
    public void ThinkingIdentifiersReuseTheAcceptedSourcePatterns(string identifier, bool supported) =>
        Assert.Equal(supported, HarnessModelCapabilities.SupportsThinking(identifier));

    [Theory]
    [InlineData("bedrock-mantle/openai.gpt-6-astra", true)]
    [InlineData("bedrock-mantle/openai.gpt-5", false)]
    public void WebSearchKeepsTheMantleFamilyBoundary(string identifier, bool supported) =>
        Assert.Equal(supported, HarnessModelCapabilities.SupportsWebSearch(identifier));

    [Fact]
    public void ReadyModelsAreBorrowedAndNeverInspected()
    {
        var model = new PoisonModel();
        Assert.True(HarnessModelCapabilities.SupportsThinking(model));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(model));
        IModel ready = model;
        Assert.True(HarnessModelCapabilities.SupportsThinking(ready));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(ready));
        Assert.Equal(0, model.Reads);
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public void AReadyRoutingModelDoesNotClassifyOrInspectItsCandidates()
    {
        var first = new PoisonModel();
        var second = new PoisonModel();
        var strategy = new PoisonStrategy();
        var router = new RoutingModel([new(first), new(second)], new() { Strategy = strategy });
        Assert.True(HarnessModelCapabilities.SupportsThinking(router));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(router));
        Assert.Equal(0, strategy.Calls);
        Assert.Equal(0, first.Reads + second.Reads);
        Assert.Equal(0, first.Disposals + second.Disposals);
    }

    private sealed class PoisonModel : IInvocationModel, IModelMetadataProvider, IModelTokenCounter, IDisposable
    {
        internal int Reads;
        internal int Disposals;
        public ModelMetadata Metadata => Unexpected<ModelMetadata>();
        public string ModelId => Unexpected<string>();
        public object Config => Unexpected<object>();
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => Unexpected<IAsyncEnumerable<ModelStreamEvent>>();
        public IModel CreateInvocationModel(IReadOnlyDictionary<string, object?> invocationState) => Unexpected<IModel>();
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default) => Unexpected<ValueTask<long>>();
        public void Dispose() => Disposals++;
        private T Unexpected<T>() { Reads++; throw new InvalidOperationException("Capability queries must not inspect or invoke a ready model."); }
    }

    private sealed class PoisonStrategy : IRoutingStrategy
    {
        internal int Calls;
        public ValueTask<RoutingCandidate?> SelectAsync(RoutingContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Capability queries must not run routing.");
        }
    }
    [Theory]
    [InlineData("bedrock/global.anthropic.claude-opus-5", true)]
    [InlineData("bedrock/apac.anthropic.claude-opus-5", true)]
    [InlineData("bedrock/us.anthropic.claude-opus-5", true)]
    [InlineData("bedrock/eu.anthropic.claude-opus-5", true)]
    [InlineData("bedrock/au.anthropic.claude-opus-5", true)]
    [InlineData("bedrock/jp.anthropic.claude-opus-5", true)]
    [InlineData("global.anthropic.claude-opus-5", true)]
    [InlineData("bedrock/us.global.anthropic.claude-opus-5", false)]
    [InlineData("anthropic/claude-3-5-sonnet-latest", false)]
    [InlineData("anthropic/claude-sonnet-4", false)]
    [InlineData("anthropic/claude-sonnet-4-4", false)]
    [InlineData("anthropic/claude-sonnet-4-5", true)]
    [InlineData("anthropic/claude-sonnet-4-6", true)]
    [InlineData("anthropic/claude-4-5-sonnet", true)]
    [InlineData("anthropic/claude-haiku-4-4", false)]
    [InlineData("anthropic/claude-haiku-4-5", true)]
    [InlineData("anthropic/claude-haiku-5-4", true)]
    [InlineData("anthropic/claude-haiku-5-5", true)]
    [InlineData("anthropic/claude-fable-1", true)]
    [InlineData("anthropic/claude-mythos-1", true)]
    [InlineData("anthropic/custom-model-name", true)]
    [InlineData("bedrock/Amazon.nova-2-lite-v1:0", false)]
    [InlineData("bedrock/amazon.nova-lite-v1:0", false)]
    [InlineData("bedrock/apac.amazon.nova-2-pro-v1:0", true)]
    [InlineData("openai/gpt-5.6", true)]
    [InlineData("bedrock-mantle/another-family", true)]
    [InlineData("google/gemini-2.5-flash", true)]
    [InlineData("ollama/qwen.qwen3", false)]
    [InlineData("litellm/openai/gpt-5.6", false)]
    [InlineData("bedrock/openai.gpt-oss-120b", true)]
    [InlineData("bedrock/openai.gpt-5.6-luna", true)]
    [InlineData("bedrock/openai.gpt-6-astra", true)]
    [InlineData("bedrock/openai.gpt-6-astra-v2", false)]
    [InlineData("bedrock/openai.gpt-6-sol", false)]
    [InlineData("bedrock/openai.gpt-5.7-luna", false)]
    [InlineData("bedrock/qwen.qwen3", true)]
    [InlineData("bedrock/xai.grok-4", true)]
    [InlineData("bedrock/meta.llama3", false)]
    [InlineData("anthropic/", true)]
    [InlineData("openai/", true)]
    [InlineData("google/", true)]
    [InlineData("bedrock-mantle/", true)]
    [InlineData("bedrock/", false)]
    [InlineData("/model", false)]
    [InlineData("unknown/model", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("OPENAI/model", false)]
    [InlineData("openai/model/with/slash", true)]
    public void ThinkingKeepsSourceProviderVersionRegionAndLexicalBoundaries(string identifier, bool supported) =>
        Assert.Equal(supported, HarnessModelCapabilities.SupportsThinking(identifier));

    [Theory]
    [InlineData("anthropic/claude-haiku-3-5", true)]
    [InlineData("anthropic/", true)]
    [InlineData("openai/gpt-4.1", true)]
    [InlineData("google/gemini-3.5-flash", true)]
    [InlineData("bedrock-mantle/openai.gpt-5.0", true)]
    [InlineData("bedrock-mantle/openai.gpt-5.6-luna", true)]
    [InlineData("bedrock-mantle/openai.gpt-5", false)]
    [InlineData("bedrock-mantle/openai.gpt-5-", false)]
    [InlineData("bedrock-mantle/openai.gpt-50.1", false)]
    [InlineData("bedrock-mantle/openai.gpt-6-sol", true)]
    [InlineData("bedrock-mantle/openai.gpt-6.0", true)]
    [InlineData("bedrock-mantle/openai.gpt-6", false)]
    [InlineData("bedrock-mantle/openai.gpt-60", false)]
    [InlineData("bedrock-mantle/anthropic.claude-opus-5", false)]
    [InlineData("bedrock-mantle/OpenAI.gpt-6-astra", false)]
    [InlineData("bedrock/openai.gpt-6-astra", false)]
    [InlineData("us.anthropic.claude-opus-5", false)]
    [InlineData("google/", true)]
    [InlineData("/openai/model", false)]
    [InlineData("openai/model/with/slash", true)]
    [InlineData("ollama/google/gemini", false)]
    [InlineData("litellm/openai/gpt-5.6", false)]
    [InlineData("unknown/anthropic", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("OpenAI/model", false)]
    public void NativeSearchKeepsSourceProviderAndMantlePrefixBoundaries(string identifier, bool supported) =>
        Assert.Equal(supported, HarnessModelCapabilities.SupportsWebSearch(identifier));

    [Fact]
    public void AClassifierDoesNotReplaceStrictConstructionValidation()
    {
        Assert.True(HarnessModelCapabilities.SupportsThinking("openai/"));
        Assert.True(HarnessModelCapabilities.SupportsWebSearch("openai/"));
        var callbacks = 0;
        foreach (var identifier in new[] { "", " ", "openai/", "/model", "unknown/model" })
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { model = identifier, builtinTools = Array.Empty<string>(), builtinPlugins = Array.Empty<string>(), session = false, skills = false, memory = false, contextManager = false });
            Assert.Throws<ArgumentException>(() => HarnessAgentConfig.Parse(json).ToOptions(new()
            {
                ModelFactory = _ => { callbacks++; throw new InvalidOperationException("Invalid configuration must fail before its factory."); },
            }));
        }
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void ReadyStatefulModelsRemainUninspected()
    {
        var model = new PoisonStatefulModel();
        Assert.True(HarnessModelCapabilities.SupportsThinking(model));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(model));
        IModel ready = model;
        Assert.True(HarnessModelCapabilities.SupportsThinking(ready));
        Assert.False(HarnessModelCapabilities.SupportsWebSearch(ready));
        Assert.Equal(0, model.Reads);
    }

    private sealed class PoisonStatefulModel : IStatefulModel, IModelMetadataProvider
    {
        internal int Reads;
        public bool Stateful => Unexpected<bool>();
        public ModelMetadata Metadata => Unexpected<ModelMetadata>();
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => Unexpected<IAsyncEnumerable<ModelStreamEvent>>();
        private T Unexpected<T>() { Reads++; throw new InvalidOperationException("Ready-model classification must not inspect statefulness, metadata or model behavior."); }
    }

}
