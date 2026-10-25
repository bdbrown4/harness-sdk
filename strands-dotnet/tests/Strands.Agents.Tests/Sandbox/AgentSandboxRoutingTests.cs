using System.Text.Json;
using Strands.Agents.Sandbox;

namespace Strands.Agents.Tests.Sandbox;

public sealed class AgentSandboxRoutingTests
{
    [Fact]
    public async Task UnboundConfiguredFactoryUsesTheInvocationAgentAndExactOperationToken()
    {
        var selected = new SourceExecutionBackend { Result = new(0, "selected", "") };
        await using var agent = Make(selected);
        using var caller = new CancellationTokenSource();
        var tool = PythonRepl.CreateForAgent(new() { Name = "custom_repl", Language = "python3.12", Description = "Selected code" });
        using var result = JsonDocument.Parse(await Invoke(tool, agent, caller.Token));
        Assert.Equal("selected", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(caller.Token, selected.Token);
        Assert.Equal("python3.12", selected.Command);
        Assert.Equal("custom_repl", tool.Spec.Name);
        Assert.Equal("Selected code", tool.Spec.Description);
    }

    [Fact]
    public async Task ExplicitBoundFactoryWinsOverTheAgentsConfiguredEnvironment()
    {
        var configured = new SourceExecutionBackend { Result = new(0, "configured", "") };
        var bound = new SourceExecutionBackend { Result = new(0, "bound", "") };
        await using var agent = Make(configured);
        using var caller = new CancellationTokenSource();
        using var result = JsonDocument.Parse(await Invoke(PythonRepl.Create(bound), agent, caller.Token));
        Assert.Equal("bound", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(caller.Token, bound.Token);
        Assert.Equal(0, configured.CommandCalls);
        Assert.Equal(0, configured.DisposeCalls);
        Assert.Equal(0, bound.DisposeCalls);
    }

    [Fact]
    public async Task DefaultIsStablePerAgentAndChildrenAreIndependentBeforeAndAfterParentMaterialization()
    {
        await using var parent = Make();
        await using var before = parent.CreateChild();
        var selected = parent.Sandbox;
        Assert.Same(selected, parent.Sandbox);
        await using var after = parent.CreateChild();
        Assert.Same(before.Sandbox, before.Sandbox);
        Assert.Same(after.Sandbox, after.Sandbox);
        Assert.NotSame(selected, before.Sandbox);
        Assert.NotSame(selected, after.Sandbox);
        Assert.NotSame(before.Sandbox, after.Sandbox);
        Assert.IsType<NotASandboxLocalEnvironment>(selected);
        Assert.Equal(0, Assert.IsType<NeverCalledModel>(parent.Model).Calls);
    }

    [Fact]
    public async Task InheritedUnboundToolResolvesTheReboundChildAndNullChildRequestsItsOwnDefault()
    {
        var parentBackend = new SourceExecutionBackend { Result = new(0, "parent", "") };
        var childBackend = new SourceExecutionBackend { Result = new(0, "child", "") };
        var tool = PythonRepl.Create();
        await using var parent = Make(parentBackend, [tool]);
        await using var child = parent.CreateChild(options => options with { Sandbox = childBackend });
        await using var reset = parent.CreateChild(options => options with { Sandbox = null });
        Assert.Same(tool, Assert.Single(child.Tools));
        using var result = JsonDocument.Parse(await Invoke(Assert.Single(child.Tools), child));
        Assert.Equal("child", result.RootElement.GetProperty("output").GetString());
        Assert.Equal(0, parentBackend.CommandCalls);
        Assert.Same(childBackend, child.Sandbox);
        Assert.IsType<NotASandboxLocalEnvironment>(reset.Sandbox);
        Assert.Same(reset.Sandbox, reset.Sandbox);
        Assert.NotSame(parent.Sandbox, reset.Sandbox);
    }

    [Fact]
    public async Task ConstructorGetterAndDisposalPerformNoBackendIOAndNeverDisposeBorrowedSelection()
    {
        var borrowed = new SourceExecutionBackend();
        var model = new NeverCalledModel();
        var parent = new Agent(new() { Model = model, Sandbox = borrowed });
        Agent? child = null;
        try
        {
            child = parent.CreateChild();
            Assert.Same(borrowed, parent.Sandbox);
            Assert.Same(borrowed, child.Sandbox);
            Assert.Equal(0, borrowed.CommandCalls);
            Assert.Equal(0, model.Calls);
        }
        finally
        {
            if (child is not null) await child.DisposeAsync();
            await parent.DisposeAsync();
        }
        Assert.Equal(0, borrowed.CommandCalls);
        Assert.Equal(0, borrowed.DisposeCalls);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task ParameterlessFactoryResolvesEachInvocationAndTheOldSingleNullCallStillBinds()
    {
        Assert.Throws<ArgumentNullException>(() => PythonRepl.Create(null!));
        var tool = PythonRepl.Create();
        Assert.Equal("python_repl", tool.Spec.Name);
        var first = new SourceExecutionBackend { Result = new(0, "first", "") };
        var second = new SourceExecutionBackend { Result = new(0, "second", "") };
        await using var firstAgent = Make(first);
        await using var secondAgent = Make(second);
        using var firstResult = JsonDocument.Parse(await Invoke(tool, firstAgent));
        using var secondResult = JsonDocument.Parse(await Invoke(tool, secondAgent));
        Assert.Equal("first", firstResult.RootElement.GetProperty("output").GetString());
        Assert.Equal("second", secondResult.RootElement.GetProperty("output").GetString());
        Assert.Equal(1, first.CommandCalls);
        Assert.Equal(1, second.CommandCalls);
    }

    private static Agent Make(Agents.Sandbox.Sandbox? selected = null, IReadOnlyList<ITool>? tools = null) => new(new() { Model = new NeverCalledModel(), Sandbox = selected, Tools = tools ?? [] });
    private static ValueTask<string> Invoke(ITool tool, Agent agent, CancellationToken token = default)
    {
        var input = JsonSerializer.SerializeToElement(new { code = "source" });
        return tool.InvokeAsync(input, new(agent, new("call", tool.Spec.Name, input), new Dictionary<string, object?>()), token);
    }
    private sealed class NeverCalledModel : IModel
    {
        internal int Calls;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Fake routing tests must never invoke a model or interpreter.");
        }
    }
}
