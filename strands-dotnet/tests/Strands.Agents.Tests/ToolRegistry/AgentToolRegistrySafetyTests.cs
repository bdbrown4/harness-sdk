using System.Text.Json;
using Strands.Agents.Conversation;

namespace Strands.Agents.Tests.ToolRegistries;

public sealed class AgentToolRegistrySafetyTests
{
    [Fact]
    public async Task ADirectResolutionFailureMustReleaseItsAdmission()
    {
        var error = new IOException("late specification failure"); var fail = false;
        var valid = AgentRegistryFixtures.Tool("my-tool", "body");
        var tool = new GetterTool(() => fail ? throw error : valid.Spec);
        var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [tool], DirectToolNameResolution = ToolNameResolution.UnderscoreAlias });
        var released = false;
        try
        {
            fail = true;
            Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => agent.InvokeToolAsync(AgentRegistryFixtures.Call("my_tool")).AsTask()));
            fail = false;
            agent.AddTool(AgentRegistryFixtures.Tool("after", "usable"));
            Assert.Equal(2, agent.Tools.Count); released = true;
        }
        finally { fail = false; if (released) await agent.DisposeAsync(); }
    }

    [Theory]
    [InlineData("invocation")]
    [InlineData("initialization")]
    [InlineData("direct")]
    [InlineData("classic")]
    public async Task EveryPublicMutationRejectsExistingLifecycleAdmissions(string kind)
    {
        var entered = Signal(); var release = Signal();
        async Task Wait() { entered.TrySetResult(true); await release.Task.ConfigureAwait(false); }
        var normal = AgentRegistryFixtures.Tool("existing", "old");
        var blocked = new FunctionTool(normal.Spec, async (_, _, _) => { await Wait(); return "old"; });
        var reducer = new GateReducer(Wait);
        var model = new RegistryAgentModel { BeforeStart = kind == "invocation" ? Wait : null };
        var agent = new Agent(new() { Model = model, Tools = [kind == "direct" ? blocked : normal], Plugins = kind == "initialization" ? [new GatePlugin(Wait)] : [] });
        Task operation = kind switch
        {
            "invocation" => agent.InvokeAsync("blocked"),
            "initialization" => agent.InitializeAsync(),
            "direct" => agent.InvokeToolAsync(AgentRegistryFixtures.Call("existing")).AsTask(),
            _ => reducer.ReduceContextAsync(agent).AsTask()
        };
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, operation, Task.Delay(8000)));
            AssertAllRejected(agent);
            Assert.Single(agent.Tools);
        }
        finally { release.TrySetResult(true); await operation; await agent.DisposeAsync(); }
    }

    [Fact]
    public async Task AbortAdmissionAlsoRejectsMutationsAfterItsPendingStateHasCleared()
    {
        var tool = AgentRegistryFixtures.Tool("approve", "approved", body: context => context.Interrupt("human"));
        await using var agent = new Agent(new() { Model = new RegistryAgentModel { ToolName = "approve" }, Tools = [tool] });
        await agent.InvokeAsync("pause");
        var entered = Signal(); var release = Signal();
        using var hook = agent.Hooks.Register<MessageAddedEvent>(async (value, _) =>
        {
            if (value.Message.Content.OfType<ToolResultBlock>().Any()) { entered.TrySetResult(true); await release.Task; }
        });
        var abort = agent.AbortInterruptsAsync("abort").AsTask();
        try { Assert.Same(entered.Task, await Task.WhenAny(entered.Task, abort, Task.Delay(8000))); Assert.Null(agent.InterruptState); AssertAllRejected(agent); }
        finally { release.TrySetResult(true); await abort; }
    }

    [Fact]
    public async Task AdmissionIsRecheckedAfterApplicationPreparationWithoutHoldingItsCallbacks()
    {
        var preparing = Signal(); var prepared = Signal(); var modelEntered = Signal(); var modelRelease = Signal();
        var candidate = AgentRegistryFixtures.Tool("new", "new");
        var model = new RegistryAgentModel { BeforeStart = async () => { modelEntered.TrySetResult(true); await modelRelease.Task; } };
        await using var agent = new Agent(new()
        {
            Model = model,
            Tools = [AgentRegistryFixtures.Tool("existing", "old")],
            ToolFilter = value => { if (ReferenceEquals(value, candidate)) { preparing.TrySetResult(true); Assert.True(prepared.Task.Wait(TimeSpan.FromSeconds(8))); } return true; }
        });
        var mutation = Task.Run(() => agent.AddOrReplaceApplicationTool(candidate));
        Task<AgentResult>? invocation = null;
        try
        {
            Assert.Same(preparing.Task, await Task.WhenAny(preparing.Task, mutation, Task.Delay(8000)));
            invocation = agent.InvokeAsync("use old tools");
            Assert.Same(modelEntered.Task, await Task.WhenAny(modelEntered.Task, invocation, Task.Delay(8000)));
            prepared.TrySetResult(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => mutation);
            Assert.Equal("existing", Assert.Single(agent.Tools).Spec.Name);
        }
        finally { prepared.TrySetResult(true); modelRelease.TrySetResult(true); if (invocation is not null) await invocation; try { await mutation; } catch (InvalidOperationException) { } }
    }

    [Theory]
    [InlineData("persistent")]
    [InlineData("ephemeral")]
    [InlineData("structured")]
    public async Task StrictInvocationNamesAreValidatedBeforeModelIoAndHistory(string conflict)
    {
        var model = new RegistryAgentModel();
        var alias = conflict == "structured" ? "strands-structured-output" : "my-tool";
        await using var agent = new Agent(new() { Model = model, ToolNameConflictPolicy = ToolNameConflictPolicy.RejectNormalized, Tools = conflict == "persistent" ? [AgentRegistryFixtures.Tool(alias, "persistent")] : [] });
        var temporary = conflict == "structured" ? AgentRegistryFixtures.Tool(alias, "temporary") : AgentRegistryFixtures.Tool("my_tool", "temporary");
        var options = new InvocationOptions { EphemeralTools = conflict == "ephemeral" ? [AgentRegistryFixtures.Tool("my-tool", "first"), temporary] : [temporary] };
        if (conflict == "structured") await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeStructuredAsync("task", new StructuredOutputOptions<JsonElement> { Schema = AgentRegistryFixtures.Json("{\"type\":\"object\"}") }, options));
        else await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync("task", options));
        Assert.Null(model.LastRequest); Assert.Empty(agent.Messages); Assert.Equal(conflict == "persistent" ? 1 : 0, agent.Tools.Count);
    }

    [Fact]
    public async Task StrictStructuredOutputAlsoChecksPersistentRegistrations()
    {
        var model = new RegistryAgentModel();
        await using var agent = new Agent(new() { Model = model, ToolNameConflictPolicy = ToolNameConflictPolicy.RejectNormalized, Tools = [AgentRegistryFixtures.Tool("strands-structured-output", "persistent")] });
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeStructuredAsync("task", new StructuredOutputOptions<JsonElement> { Schema = AgentRegistryFixtures.Json("{\"type\":\"object\"}") }));
        Assert.Null(model.LastRequest); Assert.Empty(agent.Messages); Assert.Single(agent.Tools);
    }

    [Fact]
    public async Task StrictPluginBatchFailurePublishesNoneOfThatBatch()
    {
        var original = AgentRegistryFixtures.Tool("my-tool", "old");
        await using var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [original], ToolNameConflictPolicy = ToolNameConflictPolicy.RejectNormalized, Plugins = [new RegistryToolPlugin(AgentRegistryFixtures.Tool("new", "new"), AgentRegistryFixtures.Tool("my_tool", "conflict"))] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Same(original, Assert.Single(agent.Tools));
    }

    [Fact]
    public async Task StrictBackgroundManageCollisionCannotPublishTheManageTool()
    {
        var model = new RegistryAgentModel(); var original = AgentRegistryFixtures.Tool("strands-manage-background-task", "old");
        await using var agent = new Agent(new() { Model = model, BackgroundTasks = new(), ToolNameConflictPolicy = ToolNameConflictPolicy.RejectNormalized, Tools = [original] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());
        Assert.Same(original, Assert.Single(agent.Tools)); Assert.Null(model.LastRequest);
    }

    [Fact]
    public void RemovalAndClearAreExactAndNeverDisposeBorrowedApplicationTools()
    {
        var first = new BorrowedTool("first"); var second = new BorrowedTool("second"); var replacement = new BorrowedTool("second");
        var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [first, second] });
        Assert.False(agent.RemoveApplicationTool("FIRST")); Assert.True(agent.RemoveApplicationTool("first"));
        agent.ReplaceApplicationTool(replacement); agent.ClearApplicationTools();
        Assert.Empty(agent.Tools); Assert.False(first.Disposed); Assert.False(second.Disposed); Assert.False(replacement.Disposed);
    }

    [Fact]
    public void AGetterFailureLeavesBothBindingsAndChildInheritanceUnchanged()
    {
        var original = AgentRegistryFixtures.Tool("original", "old"); var error = new IOException("getter");
        var agent = new Agent(new() { Model = new RegistryAgentModel(), Tools = [original] });
        Assert.Same(error, Assert.Throws<IOException>(() => agent.AddOrReplaceApplicationTools([AgentRegistryFixtures.Tool("original", "new"), new GetterTool(() => throw error)])));
        Assert.Same(original, Assert.Single(agent.Tools)); Assert.Same(original, Assert.Single(agent.CreateChild().Tools));
    }

    private static void AssertAllRejected(Agent agent)
    {
        Assert.Throws<InvalidOperationException>(() => agent.AddTool(AgentRegistryFixtures.Tool("new", "new")));
        Assert.Throws<InvalidOperationException>(() => agent.ReplaceApplicationTool(AgentRegistryFixtures.Tool("existing", "new")));
        Assert.Throws<InvalidOperationException>(() => agent.AddOrReplaceApplicationTool(AgentRegistryFixtures.Tool("new", "new")));
        Assert.Throws<InvalidOperationException>(() => agent.RemoveApplicationTool("existing")); Assert.Throws<InvalidOperationException>(() => agent.ClearApplicationTools());
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class GetterTool(Func<ToolSpec> get) : ITool
    {
        public ToolSpec Spec => get();
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("unused");
    }
    private sealed class BorrowedTool(string name) : ITool, IDisposable
    {
        public ToolSpec Spec { get; } = AgentRegistryFixtures.Tool(name, "unused").Spec;
        public bool Disposed { get; private set; }
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default) => new("unused");
        public void Dispose() => Disposed = true;
    }
    private sealed class GatePlugin(Func<Task> wait) : IPlugin
    {
        public string Name => "test:gate";
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => await wait();
        public IReadOnlyList<ITool> GetTools() => [];
    }
    private sealed class GateReducer(Func<Task> wait) : ConversationManager
    {
        protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken) => default;
        protected override async ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken) => await wait();
    }
}
