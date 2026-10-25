using System.Text.Json;
using Strands.Agents.Conversation;
using Strands.Agents.Tests.ToolRegistries;
using static Strands.Agents.Tests.ToolProviders.AgentToolProviderUpdatesTests;

namespace Strands.Agents.Tests.ToolProviders;

public sealed class AgentToolProviderUpdatesLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundPausedBindingsStayFixedThroughResumeOrAbort(bool abort)
    {
        var bodies = 0;
        var tool = AgentRegistryFixtures.Tool("initial", "approved", body: context => { bodies++; context.Interrupt("human"); });
        var provider = new UpdatingProvider([tool]);
        await using var agent = new Agent(new() { Model = new RegistryAgentModel { ToolName = "initial" }, ToolProviders = [provider], EnableToolProviderUpdates = true });
        Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync("pause")).StopReason);
        var question = Assert.Single(agent.InterruptState!.Interrupts);
        provider.Emit([new FixedTool("latest")]);
        Assert.Same(tool, Assert.Single(agent.Tools));
        if (abort) Assert.True(await agent.AbortInterruptsAsync("abort"));
        else Assert.Equal("done", (await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement("answer"))] })).ToString());
        Assert.Equal(abort ? 1 : 2, bodies);
        Assert.Equal("latest", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task SuccessfulSnapshotClearPublishesPendingBatchButFailureAndCancellationDoNot()
    {
        var tool = AgentRegistryFixtures.Tool("initial", "approved", body: context => context.Interrupt("human"));
        var provider = new UpdatingProvider([tool]);
        await using var agent = new Agent(new() { Model = new RegistryAgentModel { ToolName = "initial" }, ToolProviders = [provider], EnableToolProviderUpdates = true });
        await agent.InvokeAsync("pause");
        provider.Emit([new FixedTool("latest")]);
        Assert.Same(tool, Assert.Single(agent.Tools));
        var valid = new AgentSnapshot(new Dictionary<string, JsonElement>
        { ["messages"] = JsonSerializer.SerializeToElement(Array.Empty<Message>()), ["interruptState"] = JsonSerializer.SerializeToElement<object?>(null) });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.LoadSnapshotAsync(valid, cancelled.Token).AsTask());
        Assert.Equal(cancelled.Token, cancellation.CancellationToken); Assert.NotNull(agent.InterruptState); Assert.Same(tool, Assert.Single(agent.Tools));
        await Assert.ThrowsAnyAsync<Exception>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement("invalid") })).AsTask());
        Assert.NotNull(agent.InterruptState); Assert.Same(tool, Assert.Single(agent.Tools));
        await agent.LoadSnapshotAsync(valid);
        Assert.Null(agent.InterruptState); Assert.Empty(agent.Messages);
        Assert.Equal("latest", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task ClassicReductionRetainsItsTableUntilItsAdmissionIsReleased()
    {
        var entered = Signal(); var release = Signal(); var provider = new UpdatingProvider([new FixedTool("initial")]);
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true });
        await agent.InitializeAsync();
        var reducer = new GateReducer(async () => { entered.TrySetResult(true); await release.Task; });
        var reduction = reducer.ReduceContextAsync(agent).AsTask(); await entered.Task;
        try { provider.Emit([new FixedTool("latest")]); Assert.Equal("initial", Assert.Single(agent.Tools).Spec.Name); }
        finally { release.TrySetResult(true); }
        await reduction;
        Assert.Equal("latest", Assert.Single(agent.Tools).Spec.Name);
        await reducer.ReduceContextAsync(agent);
        Assert.Equal("latest", Assert.Single(agent.Tools).Spec.Name);
    }

    [Fact]
    public async Task ShutdownStopsNewPreparationBeforeItsCallbacksRun()
    {
        var original = new FixedTool("initial"); var provider = new UpdatingProvider([original]); var reads = 0;
        await using var agent = new Agent(new() { Model = new TextModel(), ToolProviders = [provider], EnableToolProviderUpdates = true, ToolFilter = _ => { reads++; return true; } });
        await agent.InitializeAsync(); var before = reads;
        using var hook = agent.Hooks.Register<ShutdownEvent>((_, _) => { provider.Emit([new FixedTool("late")]); return default; });
        await agent.ShutdownAsync();
        Assert.Equal(before, reads); Assert.Same(original, Assert.Single(agent.Tools)); Assert.Equal(1, provider.Lease.Disposals); Assert.Equal(1, provider.Removes);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class GateReducer(Func<Task> wait) : ConversationManager
    {
        protected override ValueTask ApplyManagementCoreAsync(Agent agent, CancellationToken cancellationToken) => default;
        protected override async ValueTask ReduceContextCoreAsync(Agent agent, Exception? error, CancellationToken cancellationToken) => await wait();
    }
}
