using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class CheckpointBoundarySafetyTests
{
    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task CallerCancellationBeforePauseWinsWithoutExecutingTools(CheckpointBehavior behavior)
    {
        var effects = 0;
        using var cancellation = new CancellationTokenSource();
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, _ => { effects++; return "accepted"; });
        using var hook = agent.Hooks.Register<AfterModelCallEvent>((_, _) => { cancellation.Cancel(); return default; });

        var result = await agent.InvokeAsync("question", cancellationToken: cancellation.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task ProviderIteratorCleanupFailureCannotPublishAPause(CheckpointBehavior behavior)
    {
        var effects = 0;
        var failure = new IOException("provider cleanup failed");
        var model = new BoundaryModel(() => effects, failure);
        await using var agent = Create(model, behavior, _ => { effects++; return "accepted"; });

        var actual = await Record.ExceptionAsync(() => agent.InvokeAsync("question"));

        Assert.Same(failure, actual);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task AnAcceptedEndTurnPreemptsTheAfterToolsPause(CheckpointBehavior behavior)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, _ => { effects++; return "accepted"; });
        var first = await agent.InvokeAsync("question");
        using var hook = agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("halted")]; return default; });

        var result = await agent.InvokeAsync(null, new() { CheckpointResume = first.Checkpoint });

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Equal("halted", result.Message!.Text);
        Assert.Equal(1, effects);
        Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task DeferredCancellationAfterToolsPreemptsThePause(CheckpointBehavior behavior)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, _ => { effects++; return "accepted"; });
        var first = await agent.InvokeAsync("question");
        using var hook = agent.Hooks.Register<AfterToolsEvent>((_, _) => { agent.Cancel(afterCurrentTools: true); return default; });

        var result = await agent.InvokeAsync(null, new() { CheckpointResume = first.Checkpoint });

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task AnAfterInvocationContinuationCannotConsumeAnExplicitPause(CheckpointBehavior behavior)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, _ => { effects++; return "accepted"; });
        var callbacks = 0;
        using var hook = agent.Hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            callbacks++;
            if (value.Result?.Checkpoint is not null) value.Resume = InvocationContinuation.Continue;
            return default;
        });

        var result = await agent.InvokeAsync("question");

        Assert.Equal(StopReasons.Checkpoint, result.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), result.Checkpoint);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, model.Requests);
        Assert.Equal(0, effects);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task HumanInterruptAnswersResumeThePendingToolsWithoutAnotherModelPause(CheckpointBehavior behavior)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, context =>
        {
            Assert.True(context.Interrupt("approval").GetBoolean());
            effects++;
            return "approved";
        });
        var boundary = await agent.InvokeAsync("question");
        var interrupted = await agent.InvokeAsync(null, new() { CheckpointResume = boundary.Checkpoint });
        Assert.Equal(StopReasons.Interrupt, interrupted.StopReason);
        Assert.Null(interrupted.Checkpoint);
        Assert.Equal(0, effects);
        var question = Assert.Single(interrupted.Interrupts);
        var requests = model.Requests;

        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] });

        Assert.Equal(StopReasons.Checkpoint, result.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools), result.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Equal(requests, model.Requests);
        Assert.Null(agent.InterruptState);
        Assert.Equal(ToolResultStatus.Success, Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, false)]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, true)]
    [InlineData(CheckpointBehavior.PythonReplay, false)]
    [InlineData(CheckpointBehavior.PythonReplay, true)]
    public async Task MixedResumeInputIsRejectedBeforeAdmissionAndTheAgentRemainsUsable(CheckpointBehavior behavior, bool content)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, _ => { effects++; return "accepted"; });
        var initialized = 0;
        using var hook = agent.Hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        var options = new InvocationOptions { CheckpointResume = new(CheckpointPosition.AfterTools), IdempotencyToken = "resume" };

        await Assert.ThrowsAsync<ArgumentException>(() => content
            ? agent.InvokeContentAsync([new TextBlock("mixed")], options)
            : agent.InvokeAsync("mixed", options));

        Assert.Equal(0, initialized);
        Assert.Equal(0, model.Requests);
        var next = await agent.InvokeAsync("fresh", new() { IdempotencyToken = "resume" });
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), next.Checkpoint);
        Assert.Equal(1, initialized);
        Assert.Equal(1, model.Requests);
        Assert.Equal(0, effects);
    }

    [Fact]
    public async Task DisabledResumptionRejectsBeforeAdmissionAndPreservesOrdinaryExecution()
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, CheckpointBehavior.Disabled, _ => { effects++; return "accepted"; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync(null,
            new() { CheckpointResume = new(CheckpointPosition.AfterTools), IdempotencyToken = "resume" }));

        Assert.Equal(0, model.Requests);
        var result = await agent.InvokeAsync("fresh", new() { IdempotencyToken = "resume" });
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Requests);
    }

    [Fact]
    public async Task AReplayMarkerDoesNotInventMissingConversationState()
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, CheckpointBehavior.PythonReplay, _ => { effects++; return "accepted"; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync(null,
            new() { CheckpointResume = new(CheckpointPosition.AfterModel), IdempotencyToken = "resume" }));

        Assert.Equal(0, model.Requests);
        var next = await agent.InvokeAsync("fresh", new() { IdempotencyToken = "resume" });
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), next.Checkpoint);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
    }

    [Fact]
    public void UndefinedCheckpointBehaviorIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Agent(new()
        {
            Model = new BoundaryModel(() => 0),
            CheckpointBehavior = (CheckpointBehavior)int.MaxValue
        }));
    }

    [Fact]
    public async Task AdvancingTheMaximumCycleIndexIsRejectedBeforeAnyProviderCall()
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, CheckpointBehavior.TypeScriptRegenerate, _ => { effects++; return "accepted"; });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => agent.InvokeAsync(null,
            new() { CheckpointResume = new(CheckpointPosition.AfterTools, int.MaxValue), IdempotencyToken = "resume" }));

        Assert.Equal(0, model.Requests);
        var next = await agent.InvokeAsync("fresh", new() { IdempotencyToken = "resume" });
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), next.Checkpoint);
        Assert.Equal(0, effects);
    }

    private static Agent Create(IModel model, CheckpointBehavior behavior, Func<ToolContext, string> effect)
    {
        var tool = new FunctionTool(new("effect", "Record an effect", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<string>(effect(context));
            });
        return new(new() { Model = model, Tools = [tool], CheckpointBehavior = behavior });
    }

    private sealed class BoundaryModel(Func<int> effects, Exception? cleanupFailure = null) : IModel
    {
        internal int Requests { get; private set; }
        internal int IteratorDisposals { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var needsTools = effects() == 0;
            try
            {
                yield return new ModelMessageStartEvent();
                if (needsTools)
                {
                    yield return new ModelContentBlockStartEvent(0,
                        new ToolUseBlock("effect-1", "effect", JsonSerializer.SerializeToElement(new { })));
                    yield return new ModelContentBlockStopEvent(0);
                }
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                    yield return new ModelContentBlockStopEvent(0);
                }
                yield return new ModelMetadataEvent(new Usage(2, 1));
                yield return new ModelMessageStopEvent(needsTools ? StopReasons.ToolUse : StopReasons.EndTurn);
                await Task.CompletedTask;
            }
            finally
            {
                IteratorDisposals++;
                if (cleanupFailure is not null) throw cleanupFailure;
            }
        }
    }
}
