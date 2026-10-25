using System.Text.Json;
using Strands.Agents;
using Strands.Harness.Tools;
using static Strands.Harness.Tests.SwarmToolLifecycleConformanceTests;

namespace Strands.Harness.Tests;

public sealed class SwarmToolProviderConformanceTests
{
    [Theory]
    [InlineData("provider")]
    [InlineData("plugin")]
    [InlineData("outside-scope")]
    public async Task CustomChildCannotKeepForbiddenOrUndelegatedCapabilities(string source)
    {
        var closed = 0;
        var modelCalls = 0;
        var forbidden = Tool(source == "outside-scope" ? "not-owned" : "handoff_to_agent");
        var provider = new SnapshotProvider([forbidden]);
        var tool = SwarmTool.Create(new()
        {
            Builder = (_, _, _) => new(new Agent(new()
            {
                Model = new Events(_ => { modelCalls++; return Answer("must not run"); }),
                Tools = source == "outside-scope" ? [forbidden] : [],
                ToolProviders = source == "provider" ? [provider] : [],
                Plugins = source == "plugin" ? [new ToolsPlugin(forbidden)] : [],
                OwnedResources = [new Resource(() => closed++)]
            }))
        });
        await using var parent = new Agent(new() { Model = new Events(_ => Answer("unused")), Tools = [tool] });
        var input = Team("a");
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal(ToolResultStatus.Error, result.Status);
        Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Equal(0, modelCalls);
        Assert.Equal(1, closed);
        Assert.Equal(source == "provider" ? 1 : 0, provider.Removed);
        Assert.Same(tool, Assert.Single(parent.Tools));
    }

    [Fact]
    public async Task InitialAndUpdatedProviderReservedNamesCannotReplaceSdkCoordination()
    {
        var illegalCalls = 0;
        var reserved = new FunctionTool(new("handoff_to_agent", "Application-owned reserved tool", Schema()), (_, _, _) =>
        { illegalCalls++; return new("must not run"); });
        var read = Tool("read");
        var provider = new SnapshotProvider([reserved, read]);
        var published = 0;
        var model = new Events(request =>
        {
            Assert.Single(request.Tools!, value => value.Name == "handoff_to_agent");
            Assert.Contains(request.Tools!, value => value.Name == "read");
            if (request.SystemPrompt == "A." && !HasResult(request))
            {
                published++;
                provider.Publish([reserved, read]);
                return Calls("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { agent_name = "b", message = "continue" }));
            }
            return Answer(request.SystemPrompt == "A." ? "A done" : "B done");
        });
        var tool = SwarmTool.Create();
        await using var parent = new Agent(new() { Model = model, Tools = [tool], ToolProviders = [provider], EnableToolProviderUpdates = true });
        await parent.InitializeAsync();
        Assert.Contains(parent.Tools, value => ReferenceEquals(value, reserved));
        var input = JsonSerializer.SerializeToElement(new { task = "work", agents = new[] { new { name = "a", instructions = "A." }, new { name = "b", instructions = "B." } } });
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal(ToolResultStatus.Success, result.Status);
        Assert.Equal("a: A done\nb: B done", result.Content);
        Assert.Equal(1, published);
        Assert.Equal(0, illegalCalls);
        Assert.Equal(3, provider.Watched);
        Assert.Equal(2, provider.LeasesClosed);
        Assert.Equal(2, provider.Removed);
        Assert.Contains(parent.Tools, value => ReferenceEquals(value, reserved));
        await parent.ShutdownAsync();
        Assert.Equal(3, provider.LeasesClosed);
        Assert.Equal(3, provider.Removed);
    }

    [Fact]
    public async Task DefaultBuilderPreservesAnExplicitDeveloperCoordinationFilter()
    {
        var calls = 0;
        var model = new Events(request =>
        {
            calls++;
            Assert.DoesNotContain(request.Tools ?? [], value => value.Name == "handoff_to_agent");
            return Answer("policy retained");
        });
        var tool = SwarmTool.Create();
        await using var parent = new Agent(new()
        {
            Model = model,
            Tools = [tool],
            ToolFilter = value => value.Spec.Name != "handoff_to_agent"
        });
        var input = Team("a");
        var result = await tool.InvokeResultAsync(input, Context(parent, tool, input));
        Assert.Equal("a: policy retained", result.Content);
        Assert.Equal(1, calls);
    }

    private static bool HasResult(ModelRequest request) => request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true;
    private static JsonElement Schema() => JsonSerializer.SerializeToElement(new { type = "object" });
    private static ITool Tool(string name) => new FunctionTool(new(name, "Tool", Schema()), (_, _, _) => new("ok"));
    private static ModelStreamEvent[] Calls(string id, string name, JsonElement input) =>
    [
        new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new(id, name, input)),
        new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)
    ];
    private sealed class ToolsPlugin(ITool tool) : IPlugin
    {
        public string Name => "test:forbidden-swarm-tool";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => default;
        public IReadOnlyList<ITool> GetTools() => [tool];
    }
    private sealed class SnapshotProvider(IReadOnlyList<ITool> initial) : IToolProviderUpdates
    {
        private IReadOnlyList<ITool> _current = initial;
        private readonly List<IObserver<IReadOnlyList<ITool>>> _observers = [];
        internal int Removed { get; private set; }
        internal int Watched { get; private set; }
        internal int LeasesClosed { get; private set; }
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => new(_current);
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) { Removed++; return default; }
        public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default)
        {
            Watched++;
            _observers.Add(observer);
            observer.OnNext(_current);
            return new(new Lease(() => { _observers.Remove(observer); LeasesClosed++; }));
        }
        internal void Publish(IReadOnlyList<ITool> tools)
        {
            _current = tools.ToArray();
            foreach (var observer in _observers.ToArray()) observer.OnNext(_current);
        }
        private sealed class Lease(Action close) : IAsyncDisposable
        {
            private bool _closed;
            public ValueTask DisposeAsync() { if (!_closed) { _closed = true; close(); } return default; }
        }
    }
}
