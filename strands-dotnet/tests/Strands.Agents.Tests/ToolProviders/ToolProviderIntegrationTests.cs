using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.MultiAgent;
using static Strands.Agents.Tests.ToolProviders.ToolProviderLifecycleTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class ToolProviderIntegrationTests
{
    [Fact]
    public async Task OverflowRecoveryUsesCapturedProviderSpecifications()
    {
        var tool = new FrozenTool(); var model = new OverflowModel(); var strategy = new ShrinkOnOverflow();
        await using var agent = new Agent(new()
        {
            Model = model,
            ToolProviders = [new Provider([tool])],
            Plugins = [new Plugin(_ => tool.Refuse = true, [])],
            ContextManager = new(new() { Strategies = [strategy], StashEnabled = false }),
            Messages = [Message.User("first"), Message.Assistant("old context")]
        });
        Assert.Equal("recovered", (await agent.InvokeAsync("latest")).ToString());
        Assert.Equal(2, model.Calls); Assert.Equal(1, strategy.Overflows); Assert.Equal(1, tool.Reads);
    }

    [Fact]
    public async Task SwarmReservedNamePreflightUsesCapturedProviderSpecifications()
    {
        var tool = new FrozenTool();
        await using var agent = new Agent(new() { Model = new Model(), ToolProviders = [new Provider([tool])], Plugins = [new Plugin(_ => tool.Refuse = true, [])] });
        var swarm = await Swarm.CreateAsync(new() { Nodes = [new(agent)], Routing = SwarmRoutingMode.Tool, AgentContext = AgentContextPolicy.Retain });
        Assert.NotNull(swarm); Assert.Equal(1, tool.Reads);
    }

    private sealed class FrozenTool : ITool
    {
        internal bool Refuse;
        internal int Reads;
        public ToolSpec Spec { get { Reads++; if (Refuse) throw new IOException("Live provider metadata reread."); return new("provided", "provided", JsonSerializer.SerializeToElement(new { type = "object" })); } }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("provided");
    }
    private sealed class ShrinkOnOverflow : ContextStrategy
    {
        public override string Name => "test:provider-overflow";
        internal int Overflows;
        public override ValueTask<bool> ApplyAsync(ContextState context, CancellationToken cancellationToken = default)
        {
            if (!context.Overflow) return new(false);
            Overflows++; var previous = context.Messages[1];
            context.Messages[1] = new(previous.Role, [new TextBlock(previous.Text.Substring(0, previous.Text.Length - 1))], previous.TrackingId, previous.Metadata);
            return new(true);
        }
    }
    private sealed class OverflowModel : IModel
    {
        internal int Calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Calls == 1) throw new ContextWindowOverflowException("provider overflow");
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("recovered") };
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn); await Task.CompletedTask;
        }
    }
}
