using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentToolFilterTests
{
    [Fact]
    public void ConstructionOmitsRejectedTools()
    {
        var agent = new Agent(new() { Model = new Model(), Tools = [Tool("allowed"), Tool("denied")], ToolFilter = tool => tool.Spec.Name == "allowed" });
        Assert.Equal("allowed", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task FilteringPluginToolsPreservesInitializationAndBindings()
    {
        var source = new ToolSource(() => [Tool("allowed"), Tool("denied")]);
        var agent = new Agent(new() { Model = new Model(), Plugins = [source], ToolFilter = tool => tool.Spec.Name == "allowed" });
        await agent.InitializeAsync();
        Assert.Equal(1, source.Initializations);
        Assert.Equal("allowed", Assert.Single(agent.Tools).Spec.Name);
        await agent.InvokeAsync("go");
        Assert.Equal(1, source.ModelCallsObserved);
    }

    [Fact]
    public void AddedBatchOmitsFalseResultsAndCommitsNothingOnPredicateFailure()
    {
        var failure = new IOException("predicate failed");
        var agent = new Agent(new()
        {
            Model = new Model(),
            Tools = [Tool("existing")],
            ToolFilter = tool => tool.Spec.Name == "boom" ? throw failure : tool.Spec.Name != "denied"
        });
        agent.AddTools([Tool("allowed"), Tool("denied")]);
        Assert.Equal(new[] { "existing", "allowed" }, agent.Tools.Select(tool => tool.Spec.Name));
        Assert.Same(failure, Assert.Throws<IOException>(() => agent.AddTools([Tool("candidate"), Tool("boom")])));
        Assert.Equal(new[] { "existing", "allowed" }, agent.Tools.Select(tool => tool.Spec.Name));
    }

    [Fact]
    public async Task FailedPluginBatchIsUnregisteredAndInitializationFailureIsCached()
    {
        var failure = new IOException("predicate failed");
        var calls = new List<string>();
        var source = new ToolSource(() => [Tool("candidate"), Tool("boom")]);
        var agent = new Agent(new()
        {
            Model = new Model(),
            Tools = [Tool("existing")],
            Plugins = [source],
            ToolFilter = tool => { calls.Add(tool.Spec.Name); return tool.Spec.Name == "boom" ? throw failure : true; }
        });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Equal("existing", Assert.Single(agent.Tools).Spec.Name);
        Assert.Equal(new[] { "existing", "candidate", "boom" }, calls);
        Assert.Equal(1, source.Initializations);
    }

    [Fact]
    public async Task DefaultChildInheritsFilterForItsApplicationAndFreshPluginTools()
    {
        var source = new ToolSource(() => [Tool("plugin_allowed"), Tool("plugin_denied")]);
        var parent = new Agent(new()
        {
            Model = new Model(),
            Tools = [Tool("parent_allowed")],
            Plugins = [source],
            ToolFilter = tool => tool.Spec.Name.EndsWith("allowed", StringComparison.Ordinal)
        });
        await parent.InitializeAsync();
        await using var child = parent.CreateChild(options => options with { Tools = [Tool("child_allowed"), Tool("child_denied")] });
        await child.InitializeAsync();
        Assert.Equal(new[] { "child_allowed", "plugin_allowed" }, child.Tools.Select(tool => tool.Spec.Name));
        Assert.Equal(2, source.Initializations);
    }

    [Fact]
    public void ConstructorPredicateFailureDoesNotTransferDependencyOwnership()
    {
        var failure = new IOException("predicate failed");
        var model = new Model();
        Assert.Same(failure, Assert.Throws<IOException>(() => new Agent(new()
        {
            Model = model,
            OwnsModel = true,
            Tools = [Tool("candidate"), Tool("boom")],
            ToolFilter = tool => tool.Spec.Name == "boom" ? throw failure : true
        })));
        Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public void ExcludedToolsAreNotInspectedOrCheckedForDuplicateNames()
    {
        var accepted = Tool("same");
        var excluded = Tool("same");
        var opaque = new UnregisteredTool();
        var agent = new Agent(new() { Model = new Model(), Tools = [excluded, opaque, accepted], ToolFilter = tool => ReferenceEquals(tool, accepted) });
        Assert.Same(accepted, Assert.Single(agent.Tools));
    }

    [Fact]
    public void NullFilterKeepsExistingRegistrationBehavior()
    {
        var agent = new Agent(new() { Model = new Model(), Tools = [Tool("first")] });
        agent.AddTool(Tool("second"));
        Assert.Equal(new[] { "first", "second" }, agent.Tools.Select(tool => tool.Spec.Name));
    }

    private static FunctionTool Tool(string name) => new(new(name, name, JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => ValueTask.FromResult("ok"));
    private sealed class ToolSource(Func<IReadOnlyList<ITool>> tools) : IPlugin
    {
        public string Name => "test:tool-source";
        internal int Initializations { get; private set; }
        internal int ModelCallsObserved { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            Initializations++;
            agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { ModelCallsObserved++; return default; });
            return default;
        }
        public IReadOnlyList<ITool> GetTools() => tools();
    }
    private sealed class UnregisteredTool : ITool
    {
        public ToolSpec Spec => throw new InvalidOperationException("A rejected tool must not be inspected.");
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Model : IModel, IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { foreach (var value in AgentTests.Text("reply")) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); } }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
