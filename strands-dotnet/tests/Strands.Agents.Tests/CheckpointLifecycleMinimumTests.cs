using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class CheckpointLifecycleMinimumTests
{
    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, 1)]
    [InlineData(CheckpointBehavior.PythonReplay, 2)]
    public async Task EnabledExecutionPausesBeforeAnyToolEffect(CheckpointBehavior behavior, int historyCount)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, () => effects++);

        var result = await agent.InvokeAsync("question");

        Assert.Equal(StopReasons.Checkpoint, result.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), result.Checkpoint);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Equal(historyCount, agent.Messages.Count);
        Assert.Equal("question", agent.Messages[0].Text);
        Assert.Single(result.Message!.Content.OfType<ToolUseBlock>());
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, 2, 1)]
    [InlineData(CheckpointBehavior.PythonReplay, 1, 0)]
    public async Task AfterModelResumeUsesTheSelectedSourceWorkflow(CheckpointBehavior behavior, int requests, int resumedTurns)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, behavior, () => effects++);
        var before = 0;
        var after = 0;
        using var beforeHook = agent.Hooks.Register<BeforeInvocationEvent>((_, _) => { before++; return default; });
        using var afterHook = agent.Hooks.Register<AfterInvocationEvent>((_, _) => { after++; return default; });
        var paused = await agent.InvokeAsync("question");

        var resumed = await agent.InvokeAsync(null, new() { CheckpointResume = paused.Checkpoint });

        Assert.Equal(StopReasons.Checkpoint, resumed.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools), resumed.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Equal(requests, model.Requests);
        Assert.Equal(requests, model.IteratorDisposals);
        Assert.Equal(resumedTurns, resumed.Turns);
        Assert.Equal(3 * resumedTurns, resumed.Usage.TotalTokens);
        Assert.Equal(2, before);
        Assert.Equal(2, after);
        Assert.Equal(new[] { Role.User, Role.Assistant, Role.User }, agent.Messages.Select(message => message.Role));
        var call = Assert.Single(agent.Messages[1].Content.OfType<ToolUseBlock>());
        var accepted = Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>());
        Assert.Equal(call.ToolUseId, accepted.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, accepted.Status);
        Assert.Equal("accepted", accepted.Content);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate, 3)]
    [InlineData(CheckpointBehavior.PythonReplay, 2)]
    public async Task AfterToolsResumeAdvancesTheCycleWithoutRepeatingEffects(CheckpointBehavior behavior, int requests)
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects, desiredEffects: 2);
        await using var agent = Create(model, behavior, () => effects++);
        var first = await agent.InvokeAsync("question");
        var tools = await agent.InvokeAsync(null, new() { CheckpointResume = first.Checkpoint });

        var next = await agent.InvokeAsync(null, new() { CheckpointResume = tools.Checkpoint });

        Assert.Equal(StopReasons.Checkpoint, next.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel, 1), next.Checkpoint);
        Assert.Equal(1, effects);
        Assert.Equal(requests, model.Requests);
        Assert.Equal(requests, model.IteratorDisposals);
        Assert.Equal(1, next.Turns);
        Assert.Equal(3, next.Usage.TotalTokens);
        Assert.Equal(1, agent.Messages.Count(message => message.Text == "question"));
        Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
    }

    [Fact]
    public async Task DisabledExecutionKeepsTheOrdinaryModelToolWorkflow()
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects);
        await using var agent = Create(model, CheckpointBehavior.Disabled, () => effects++);

        var result = await agent.InvokeAsync("question");

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Equal("done", result.Message!.Text);
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Requests);
        Assert.Equal(2, model.IteratorDisposals);
    }

    [Fact]
    public async Task AnEnabledTextResponseNeverInventsAPause()
    {
        var effects = 0;
        var model = new BoundaryModel(() => effects, desiredEffects: 0);
        await using var agent = Create(model, CheckpointBehavior.PythonReplay, () => effects++);

        var result = await agent.InvokeAsync("question");

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Null(result.Checkpoint);
        Assert.Equal("done", result.Message!.Text);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
    }

    private static Agent Create(IModel model, CheckpointBehavior behavior, Action effect)
    {
        var tool = new FunctionTool(new("effect", "Record an effect", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                effect();
                return new ValueTask<string>("accepted");
            });
        return new(new() { Model = model, Tools = [tool], CheckpointBehavior = behavior });
    }

    private sealed class BoundaryModel(Func<int> effects, int desiredEffects = 1) : IModel
    {
        internal int Requests { get; private set; }
        internal int IteratorDisposals { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            var needsTools = effects() < desiredEffects;
            try
            {
                yield return new ModelMessageStartEvent();
                if (needsTools)
                {
                    yield return new ModelContentBlockStartEvent(0,
                        new ToolUseBlock("effect-" + (effects() + 1), "effect", JsonSerializer.SerializeToElement(new { })));
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
            finally { IteratorDisposals++; }
        }
    }
}
