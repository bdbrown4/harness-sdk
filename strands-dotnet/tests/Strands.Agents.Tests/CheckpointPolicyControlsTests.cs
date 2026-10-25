using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class CheckpointPolicyControlsTests
{
    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task InvocationDenialCannotOrphanAnUnexecutedCheckpointToolRequest(CheckpointBehavior behavior)
    {
        var effects = 0;
        var model = new BoundaryModel();
        await using var agent = Create(model, behavior, () => effects++);
        var paused = await agent.InvokeAsync("question");
        using var hook = agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { value.CancelReason = "denied"; return default; });

        var denied = await agent.InvokeAsync(null, new() { CheckpointResume = paused.Checkpoint });

        Assert.Equal(StopReasons.EndTurn, denied.StopReason);
        Assert.Equal("denied", denied.Message!.Text);
        Assert.Null(denied.Checkpoint);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        var requested = agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Select(call => call.ToolUseId).ToArray();
        var answered = agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(requested, answered.Select(result => result.ToolUseId));
        Assert.All(answered, result => Assert.Equal(ToolResultStatus.Error, result.Status));
        var snapshot = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        await using var restored = Create(new BoundaryModel(), behavior, () => effects++);
        await restored.LoadSnapshotAsync(snapshot);
        Assert.Equal(agent.Messages.Select(message => message.TrackingId), restored.Messages.Select(message => message.TrackingId));
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task InvocationHookFailurePreservesItsIdentityAndRepairsThePendingRequest(CheckpointBehavior behavior)
    {
        var effects = 0;
        var model = new BoundaryModel();
        await using var agent = Create(model, behavior, () => effects++);
        var paused = await agent.InvokeAsync("question");
        var failure = new IOException("policy callback failed");
        using var hook = agent.Hooks.Register<BeforeInvocationEvent>((_, _) => new ValueTask(Task.FromException(failure)));

        var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync(null, new() { CheckpointResume = paused.Checkpoint }));

        Assert.Same(failure, actual);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        Assert.Equal(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Select(call => call.ToolUseId),
            agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Select(result => result.ToolUseId));
    }

    [Fact]
    public async Task ModelDenialDuringRegenerationDoesNotExecuteTheDiscardedRequest()
    {
        var effects = 0;
        var model = new BoundaryModel();
        await using var agent = Create(model, CheckpointBehavior.TypeScriptRegenerate, () => effects++);
        var paused = await agent.InvokeAsync("question");
        using var hook = agent.Hooks.Register<BeforeModelCallEvent>((value, _) => { value.CancelReason = "model denied"; return default; });

        var denied = await agent.InvokeAsync(null, new() { CheckpointResume = paused.Checkpoint });

        Assert.Equal(StopReasons.EndTurn, denied.StopReason);
        Assert.Equal("model denied", denied.Message!.Text);
        Assert.Null(denied.Checkpoint);
        Assert.Equal(0, effects);
        Assert.Equal(1, model.Requests);
        Assert.Empty(agent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>());
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task ToolDenialClosesTheBatchBeforePublishingTheNextCheckpoint(CheckpointBehavior behavior)
    {
        var effects = 0;
        await using var agent = Create(new BoundaryModel(), behavior, () => effects++);
        var paused = await agent.InvokeAsync("question");
        using var hook = agent.Hooks.Register<BeforeToolsEvent>((value, _) => { value.CancelReason = "tools denied"; return default; });

        var next = await agent.InvokeAsync(null, new() { CheckpointResume = paused.Checkpoint });

        Assert.Equal(StopReasons.Checkpoint, next.StopReason);
        Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools), next.Checkpoint);
        Assert.Equal(0, effects);
        var result = Assert.Single(agent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>());
        Assert.Equal("effect-1", result.ToolUseId);
        Assert.Equal("tools denied", result.Content);
        Assert.Equal(ToolResultStatus.Error, result.Status);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task TypedInvocationPreservesTheOriginalCallerCancellationToken(CheckpointBehavior behavior)
    {
        using var cancellation = new CancellationTokenSource();
        var conversions = 0;
        await using var agent = new Agent(new() { Model = new BoundaryModel(), CheckpointBehavior = behavior });
        using var hook = agent.Hooks.Register<AfterModelCallEvent>((_, _) => { cancellation.Cancel(); return default; });

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeStructuredAsync("question",
            Structured(() => conversions++), cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(0, conversions);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task TypedInvocationDoesNotReplaceAProviderFailureWithAPauseException(CheckpointBehavior behavior)
    {
        var failure = new IOException("model failed");
        var conversions = 0;
        await using var agent = new Agent(new() { Model = new BoundaryModel(failure), CheckpointBehavior = behavior });

        var actual = await Assert.ThrowsAsync<IOException>(() => agent.InvokeStructuredAsync("question", Structured(() => conversions++)));

        Assert.Same(failure, actual);
        Assert.Equal(0, conversions);
    }

    [Theory]
    [InlineData(CheckpointBehavior.TypeScriptRegenerate)]
    [InlineData(CheckpointBehavior.PythonReplay)]
    public async Task TypedPolicyDenialExposesItsActualResultWithoutConvertingAValue(CheckpointBehavior behavior)
    {
        var conversions = 0;
        var model = new BoundaryModel();
        await using var agent = new Agent(new() { Model = model, CheckpointBehavior = behavior });
        using var hook = agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { value.CancelReason = "denied"; return default; });

        var failure = await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("question", Structured(() => conversions++)));

        Assert.NotNull(failure.Result);
        Assert.Equal(StopReasons.EndTurn, failure.Result.StopReason);
        Assert.Equal("denied", failure.Result.Message!.Text);
        Assert.Null(failure.Result.Checkpoint);
        Assert.Equal(0, conversions);
        Assert.Equal(0, model.Requests);
    }

    private static StructuredOutputOptions<JsonElement> Structured(Action converted) => new()
    {
        Schema = JsonSerializer.SerializeToElement(new { type = "object" }),
        Deserializer = json => { converted(); return json.Clone(); }
    };

    private static Agent Create(IModel model, CheckpointBehavior behavior, Action effect) => new(new()
    {
        Model = model,
        CheckpointBehavior = behavior,
        Tools = [new FunctionTool(new("effect", "Record an effect", JsonSerializer.SerializeToElement(new { type = "object" })),
            (_, _, cancellationToken) => { cancellationToken.ThrowIfCancellationRequested(); effect(); return new ValueTask<string>("accepted"); })]
    });

    private sealed class BoundaryModel(Exception? failure = null) : IModel
    {
        internal int Requests { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            if (failure is not null) throw failure;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("effect-1", "effect", JsonSerializer.SerializeToElement(new { })));
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            await Task.CompletedTask;
        }
    }
}
