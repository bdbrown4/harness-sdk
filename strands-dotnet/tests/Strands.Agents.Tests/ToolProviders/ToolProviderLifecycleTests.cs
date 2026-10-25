using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class ToolProviderLifecycleTests
{
    private static readonly JsonElement ObjectSchema = JsonSerializer.SerializeToElement(new { type = "object" });

    [Fact]
    public async Task ConstructionIsLazyAndSnapshotsProviderDefinitions()
    {
        var first = new Provider([Tool("first")]); var replacement = new Provider([Tool("replacement")]);
        var definitions = new List<IToolProvider> { first };
        await using var agent = new Agent(new() { Model = new Model(), ToolProviders = definitions });
        definitions[0] = replacement;
        Assert.Empty(first.Events);
        await agent.InitializeAsync();
        Assert.Equal(["first"], agent.Tools.Select(tool => tool.Spec.Name));
        Assert.Equal(["add", "load"], first.Events);
        Assert.Empty(replacement.Events);
    }

    [Fact]
    public async Task ApplicationProviderAndPluginToolsKeepTheirOrder()
    {
        var provider = new Provider([Tool("provider_a"), Tool("provider_b")]);
        var plugin = new Plugin(agent => Assert.Equal(["application", "provider_a", "provider_b"], agent.Tools.Select(tool => tool.Spec.Name)), [Tool("plugin")]);
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((evt, _) => { Assert.Equal(["application", "provider_a", "provider_b", "plugin"], evt.Agent.Tools.Select(tool => tool.Spec.Name)); return default; });
        await using var agent = new Agent(new() { Model = new Model(), Tools = [Tool("application")], ToolProviders = [provider], Plugins = [plugin], Hooks = hooks });
        await agent.InitializeAsync();
    }

    [Fact]
    public async Task RepeatedEmptyProviderUsesOneContextAndRemovesEveryDeclaration()
    {
        var provider = new Provider([]);
        var agent = new Agent(new() { Model = new Model(), ToolProviders = [provider, provider] });
        await agent.InitializeAsync(); await agent.ShutdownAsync(); await agent.DisposeAsync();
        Assert.Equal(["add", "load", "add", "load", "remove", "remove"], provider.Events);
        Assert.Single(provider.Contexts.Distinct());
        Assert.All(provider.CleanupTokens, token => Assert.False(token.CanBeCanceled));
        Assert.Equal(0, provider.Disposals);
    }

    [Fact]
    public async Task FilterRunsBeforeSpecificationAndMalformedBatchPublishesNothing()
    {
        var denied = new BadTool();
        var provider = new Provider([denied, Tool("duplicate"), Tool("duplicate")]);
        var agent = new Agent(new() { Model = new Model(), ToolProviders = [provider], Tools = [Tool("existing")], ToolFilter = tool => !ReferenceEquals(tool, denied) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Equal(["existing"], agent.Tools.Select(tool => tool.Spec.Name));
        Assert.Equal(["add", "load", "remove"], provider.Events);
        await agent.DisposeAsync();
    }

    [Fact]
    public async Task FilterCanDenyAThrowingSpecificationWithoutReadingIt()
    {
        var denied = new BadTool(); var accepted = Tool("accepted");
        await using var agent = new Agent(new() { Model = new Model(), ToolProviders = [new Provider([denied, accepted])], ToolFilter = value => !ReferenceEquals(value, denied) });
        await agent.InitializeAsync(); Assert.Same(accepted, Assert.Single(agent.Tools));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("load")]
    [InlineData("plugin")]
    [InlineData("initialized")]
    public async Task InitializationFailureReleasesAllTrackedDeclarationsAndPreservesPrimary(string phase)
    {
        var primary = new IOException("primary"); var cleanup = new ApplicationException("cleanup");
        var first = new Provider([Tool("first")]) { OnRemove = _ => throw cleanup };
        var second = new Provider([Tool("second")]);
        if (phase == "add") second.OnAdd = _ => throw primary;
        if (phase == "load") second.OnLoad = _ => throw primary;
        var hooks = new HookRegistry();
        if (phase == "initialized") hooks.Register<InitializedEvent>((_, _) => throw primary);
        var plugins = phase == "plugin" ? new IPlugin[] { new Plugin(_ => throw primary, []) } : [];
        var agent = new Agent(new() { Model = new Model(), ToolProviders = [first, second], Plugins = plugins, Hooks = hooks });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Empty(agent.Tools);
        Assert.Equal("remove", first.Events.Last()); Assert.Equal("remove", second.Events.Last());
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        var count = first.Events.Count; await agent.DisposeAsync(); Assert.Equal(count, first.Events.Count);
    }

    internal static ITool Tool(string name) => new FunctionTool(new(name, name, ObjectSchema), (_, _, _) => new ValueTask<string>(name));
    internal sealed class Provider(IReadOnlyList<ITool> tools) : IToolProvider, IAsyncDisposable
    {
        public List<string> Events { get; } = [];
        public List<ToolProviderContext> Contexts { get; } = [];
        public List<CancellationToken> CleanupTokens { get; } = [];
        public Func<ToolProviderContext, ValueTask>? OnAdd { get; set; }
        public Func<ToolProviderContext, ValueTask>? OnLoad { get; set; }
        public Func<ToolProviderContext, ValueTask>? OnRemove { get; set; }
        public int Disposals { get; private set; }
        public async ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        { Events.Add("add"); Contexts.Add(context); if (OnAdd is { } callback) await callback(context); }
        public async ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        { Events.Add("load"); Contexts.Add(context); if (OnLoad is { } callback) await callback(context); return tools; }
        public async ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        { Events.Add("remove"); Contexts.Add(context); CleanupTokens.Add(cancellationToken); if (OnRemove is { } callback) await callback(context); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class BadTool : ITool
    {
        public ToolSpec Spec => throw new IOException("Denied specification must not be inspected.");
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    internal sealed class Plugin(Action<Agent> initialize, IReadOnlyList<ITool> tools) : IPlugin
    {
        public string Name => "test:provider";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { initialize(agent); return default; }
        public IReadOnlyList<ITool> GetTools() => tools;
    }
    internal sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent("end_turn"); await Task.CompletedTask; }
    }
}
