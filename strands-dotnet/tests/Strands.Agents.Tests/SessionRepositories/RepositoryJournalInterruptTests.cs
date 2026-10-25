using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.SessionRepositories;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class RepositoryJournalInterruptTests
{
    [Fact]
    public async Task RowsWhoseMetadataCommitFailedAreNotAConfirmedRestoreFrontier()
    {
        var repository = new JournalProbeRepository();
        await using (var agent = Create(repository, new JournalTestModel()))
        {
            await agent.InitializeAsync();
            repository.Before = (operation, _) => { if (operation == "update-agent") throw new IOException("metadata unavailable"); return default; };
            await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("unconfirmed"));
            Assert.Equal("unconfirmed", Assert.Single(await repository.Inner.ListMessagesAsync("session", "agent")).Message.Text);
        }
        repository.Before = null;
        await using var restored = Create(repository, new JournalTestModel());
        await restored.InitializeAsync();
        Assert.Empty(restored.Messages);
        await restored.InvokeAsync("confirmed");
        Assert.Equal(new long[] { 0, 1, 2 }, (await repository.Inner.ListMessagesAsync("session", "agent")).Select(row => row.MessageId));
        await using var reopenedAgain = Create(repository, new JournalTestModel());
        await reopenedAgain.InitializeAsync();
        Assert.Equal(new[] { "confirmed", "answer" }, reopenedAgain.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ResumeAfterAFailedAbortExcludesItsProvisionalRowAndExecutesOnlyTheAcceptedTool()
    {
        var repository = new JournalProbeRepository();
        var effects = 0;
        await using var agent = Create(repository, new JournalApprovalModel(), Approval(() => effects++));
        var question = Assert.Single((await agent.InvokeAsync("task")).Interrupts);
        var reject = true;
        repository.Before = (operation, _) => { if (operation == "update-agent" && reject) { reject = false; throw new IOException("abort failed"); } return default; };
        await Assert.ThrowsAsync<IOException>(async () => await agent.AbortInterruptsAsync("not accepted"));
        await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] });
        Assert.Equal(1, effects);
        await using var reopened = Create(repository, new JournalTestModel());
        await reopened.InitializeAsync();
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(reopened.Messages[2].Content));
        Assert.Equal("approved", result.Content);
        Assert.DoesNotContain(reopened.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(), value => value.Content == "not accepted");
        Assert.Null(reopened.InterruptState);
    }

    [Fact]
    public async Task ARealPausedToolBatchCanBeAbortedAndReopenedWithoutReplayingItsBody()
    {
        var repository = new JournalProbeRepository();
        var calls = 0;
        var tool = Approval(() => calls++);
        await using (var agent = Create(repository, new JournalApprovalModel(), tool))
        {
            Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync("task")).StopReason);
            Assert.True(await agent.AbortInterruptsAsync("declined"));
        }
        await using var restored = Create(repository, new JournalTestModel(), tool);
        await restored.InitializeAsync();
        Assert.Null(restored.InterruptState);
        Assert.Equal("declined", Assert.IsType<ToolResultBlock>(Assert.Single(restored.Messages[2].Content)).Content);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FailedAbortRetainsItsLivePauseAndReusesItsReservedNumericRowOnRetry()
    {
        var repository = new JournalProbeRepository();
        await using var agent = Create(repository, new JournalApprovalModel(), Approval(() => { }));
        await agent.InvokeAsync("task");
        var initial = JsonSerializer.Serialize(agent.InterruptState);
        var reject = true;
        repository.Before = (operation, _) => { if (operation == "update-agent" && reject) { reject = false; throw new IOException("abort metadata"); } return default; };
        await Assert.ThrowsAsync<IOException>(async () => await agent.AbortInterruptsAsync("declined"));
        Assert.Equal(initial, JsonSerializer.Serialize(agent.InterruptState));
        await using (var checkpoint = Create(repository, new JournalTestModel(), Approval(() => { })))
        {
            await checkpoint.InitializeAsync();
            Assert.NotNull(checkpoint.InterruptState);
            Assert.Equal(2, checkpoint.Messages.Count);
        }
        Assert.True(await agent.AbortInterruptsAsync("declined"));
        Assert.Equal(new long[] { 0, 1, 2, 2 }, repository.MessageAttempts);
        Assert.Null(agent.InterruptState);
        Assert.Equal(3, (await repository.Inner.ListMessagesAsync("session", "agent")).Count);
    }

    [Fact]
    public async Task AConfirmedHumanAnswerSurvivesAFailingBeforeInvocationObserverAndFreshRestore()
    {
        var repository = new JournalProbeRepository();
        var effects = 0;
        var hooks = new HookRegistry();
        var fail = false;
        var primary = new IOException("observer");
        hooks.Register<BeforeInvocationEvent>((_, _) => { if (fail) throw primary; return default; });
        string id;
        await using (var agent = new Agent(new() { Id = "agent", Model = new JournalApprovalModel(), Tools = [Approval(() => effects++)], Hooks = hooks, Plugins = [new RepositorySessionManager("session", repository)] }))
        {
            id = Assert.Single((await agent.InvokeAsync("task")).Interrupts).Id;
            fail = true;
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null, new() { InterruptResponses = [new(id, JsonSerializer.SerializeToElement(true))] })));
        }
        await using var restored = Create(repository, new JournalTestModel(), Approval(() => effects++));
        await restored.InitializeAsync();
        Assert.Equal(id, Assert.Single(restored.InterruptState!.Responses).InterruptId);
        await restored.InvokeAsync(null);
        Assert.Equal(1, effects);
    }

    private static Agent Create(ISessionRepository repository, IModel model, params ITool[] tools) => new(new() { Id = "agent", Model = model, Tools = tools, Plugins = [new RepositorySessionManager("session", repository)] });
    private static FunctionTool Approval(Action effect) => new(new("approve", "approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => { context.Interrupt("approval"); effect(); return new("approved"); });
}

internal sealed class JournalApprovalModel : IModel
{
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.Messages.LastOrDefault()?.Content.OfType<ToolResultBlock>().Any() == true)
        {
            await foreach (var item in new JournalTestModel().StreamAsync(request, cancellationToken)) yield return item;
            yield break;
        }
        await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
        yield return new ModelMessageStartEvent();
        yield return new ModelContentBlockStartEvent(0, new("approval-call", "approve", JsonSerializer.SerializeToElement(new { })));
        yield return new ModelContentBlockStopEvent(0);
        yield return new ModelMessageStopEvent(StopReasons.ToolUse);
    }
}
