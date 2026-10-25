using Strands.Agents;
using Strands.Agents.ConsumerFixture;

namespace Strands.Compatibility.Tests;

public sealed class ConsumerBinaryCompatibilityTests
{
    [Fact]
    public async Task NetstandardConsumerHookCanBeCopiedFromBothConsumerAndRuntimeAssets()
    {
        await using var agent = new Agent(new() { Model = new UnusedModel() });
        HookEvent original = new ConsumerHookEvent(agent, "original");

        var copyFromConsumer = ConsumerEventCopies.Copy(original);
        var copyFromRuntime = original with { };
        var typedCopy = ConsumerEventCopies.CopyTyped((ConsumerHookEvent)original);

        Assert.Equal("original", Assert.IsType<ConsumerHookEvent>(copyFromConsumer).Value);
        Assert.Equal("original", Assert.IsType<ConsumerHookEvent>(copyFromRuntime).Value);
        Assert.Equal("copied", typedCopy.Value);
        Assert.Same(agent, copyFromRuntime.Agent);
        Assert.NotSame(original, copyFromConsumer);
        Assert.NotSame(original, copyFromRuntime);
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This fixture must not call a model.");
    }
}
