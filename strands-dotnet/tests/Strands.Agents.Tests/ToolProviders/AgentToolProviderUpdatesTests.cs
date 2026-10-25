using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesTests
{
    [Fact]
    public async Task OptInEstablishesInitialWatchWithoutASeparateLoad()
    {
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await agent.InitializeAsync();
        Assert.Equal(1, provider.Watches);
        Assert.Equal(0, provider.Loads);
        Assert.Equal("initial", Assert.Single(agent.Tools).Spec.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingInitialSnapshotOrNullLeaseFailsInitializationAndRemovesConsumer(bool nullLease)
    {
        var provider = new UpdatingProvider([new FixedTool("initial")]) { NoInitial = !nullLease, NullLease = nullLease };
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Empty(agent.Tools);
        Assert.Equal(1, provider.Removes);
        Assert.Equal(nullLease ? 0 : 1, provider.Lease.Disposals);
    }

    [Fact]
    public async Task DefaultRemainsOneTimeLoadEvenForAnUpdatingProvider()
    {
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider] });
        await agent.InitializeAsync();
        Assert.Equal(0, provider.Watches);
        Assert.Equal(1, provider.Loads);
        Assert.Equal("initial", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task BusyInvocationRetainsItsTableThenPublishesTheLatestFullBatch()
    {
        var model = new GatedModel();
        var provider = new UpdatingProvider([new FixedTool("initial")]);
        await using var agent = new Agent(new() { Model = model, ToolProviders = [provider], EnableToolProviderUpdates = true });
        var invocation = agent.InvokeAsync("run");
        await model.Entered.Task;
        try
        {
            provider.Emit([new FixedTool("intermediate")]);
            provider.Emit([new FixedTool("latest")]);
            Assert.Equal("initial", Assert.Single(agent.Tools).Spec.Name);
        }
        finally { model.Release.TrySetResult(true); }
        Assert.Equal("done", (await invocation).ToString());
        Assert.Equal("initial", Assert.Single(model.Request!.Tools!).Name);
        Assert.Equal("latest", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task BusyDirectCallKeepsItsBodyUntilPhysicalRelease()
    {
        var tool = new GatedTool();
        var provider = new UpdatingProvider([tool]);
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        var invocation = agent.InvokeToolAsync(new("direct", "initial", JsonSerializer.SerializeToElement(new { }))).AsTask();
        await tool.Entered.Task;
        try
        {
            provider.Emit([new FixedTool("latest")]);
            Assert.Same(tool, Assert.Single(agent.Tools));
        }
        finally { tool.Release.TrySetResult(true); }
        Assert.Equal("original body", (await invocation).Content);
        Assert.Equal("latest", Assert.Single(agent.Tools).Spec.Name);
    }

    internal sealed class UpdatingProvider(IReadOnlyList<ITool> tools) : IToolProviderUpdates
    {
        internal int Watches;
        internal int Loads;
        internal int Removes;
        internal bool NoInitial { get; init; }
        internal bool NullLease { get; init; }
        internal Lease Lease { get; } = new();
        internal IObserver<IReadOnlyList<ITool>>? Observer;
        internal Action? BeforeReturn;
        internal void Emit(IReadOnlyList<ITool> tools) => (Observer ?? throw new InvalidOperationException("No subscription.")).OnNext(tools);
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Loads++; return new(tools); }
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Removes++; return default; }
        public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default)
        {
            Watches++;
            Observer = observer;
            if (!NoInitial) observer.OnNext(tools);
            BeforeReturn?.Invoke();
            return new(NullLease ? null! : Lease);
        }
    }

    internal sealed class Lease : IAsyncDisposable
    {
        internal int Disposals;
        internal Func<ValueTask>? BeforeDispose;
        public ValueTask DisposeAsync() { Disposals++; return BeforeDispose?.Invoke() ?? default; }
    }

    internal sealed class FixedTool(string name) : ITool
    {
        public ToolSpec Spec { get; } = new(name, name, JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new(name);
    }

    internal sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("done") };
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            await Task.CompletedTask;
        }
    }

    internal sealed class GatedModel : IModel
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ModelRequest? Request;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request; Entered.TrySetResult(true);
            await Release.Task;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0) { ContentBlock = new TextBlock("done") };
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    internal sealed class GatedTool : ITool
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ToolSpec Spec { get; } = new("initial", "initial", JsonSerializer.SerializeToElement(new { type = "object" }));
        public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(true); await Release.Task; return "original body"; }
    }
}
