using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;

namespace Strands.Harness.Tests;

public sealed class ToolProviderHarnessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HarnessNameGatheringUsesAdmittedProviderMetadata(bool subagent, bool background)
    {
        var provider = new Provider();
        await using var agent = await Harness.CreateAsync(new HarnessOptions
        {
            UseEnvironmentTelemetry = false,
            BuiltinTools = subagent ? ["subagent"] : [],
            BuiltinPlugins = [],
            BackgroundTasks = background ? new() : null,
            Skills = new() { Enabled = false },
            Memory = new() { Enabled = false },
            Context = new() { Enabled = false },
            Session = new() { Enabled = false },
            AgentOptions = new() { Model = new Model(), ToolProviders = [provider], Plugins = [new Plugin(provider)] }
        });
        Assert.Same(provider.Tools[0], Assert.Single(agent.Tools, tool => ReferenceEquals(tool, provider.Tools[0])));
        Assert.Equal("provided", (await agent.InvokeToolAsync(new("direct", "provided", JsonSerializer.SerializeToElement(new { })))).Content);
        if (subagent)
        {
            var delegated = await agent.InvokeToolAsync(new("delegate", "subagent", JsonSerializer.SerializeToElement(new { task = "work" })));
            Assert.True(delegated.Status == ToolResultStatus.Success, delegated.Content);
            Assert.Equal("done", delegated.Content); Assert.Equal(2, provider.Tools.Count);
        }
    }

    private sealed class Provider : IToolProvider
    {
        internal List<FrozenTool> Tools { get; } = [];
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        { var tool = new FrozenTool(); Tools.Add(tool); return new(new ITool[] { tool }); }
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
    }
    private sealed class FrozenTool : ITool
    {
        internal bool Refuse;
        public ToolSpec Spec => Refuse ? throw new IOException("Live provider metadata reread.") : new("provided", "provided", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("provided");
    }
    private sealed class Plugin(Provider provider) : IPlugin
    {
        public string Name => "test:freeze-provider";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { provider.Tools.Last().Refuse = true; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class Model : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("done") }; yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); await Task.CompletedTask; }
    }
}
