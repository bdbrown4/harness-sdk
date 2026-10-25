using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Interventions.Steering;

namespace Strands.Agents.Tests.Interventions;

public sealed class LlmSteeringHandlerTests
{
    [Fact]
    public async Task DefaultLedgerIsPendingBeforeEvaluationAndCompletedAfterTheTool()
    {
        var decision = new DecisionModel("{\"type\":\"proceed\",\"reason\":\"No concerning pattern\"}"); var effects = 0;
        var steering = new LlmSteeringHandler(new() { SystemPrompt = "steering rules" });
        await using var agent = new Agent(new() { Model = new ToolModel(), AuxModel = decision, Interventions = [steering], Tools = [Tool(() => effects++)], SystemPrompt = "parent instructions" });
        Assert.Equal("done", (await agent.InvokeAsync("task")).ToString());
        var request = Assert.Single(decision.Requests); Assert.Equal("steering rules", request.SystemPrompt);
        Assert.Equal(3, Assert.Single(request.Messages).Content.Count); Assert.IsType<CachePointBlock>(request.Messages[0].Content[1]);
        Assert.Contains("\"status\": \"pending\"", request.Messages[0].Text); Assert.Contains("Tool: effect", request.Messages[0].Text);
        Assert.DoesNotContain("parent instructions", JsonSerializer.Serialize(request)); Assert.Equal(1, effects);
        var call = Assert.Single(Assert.Single(steering.GetSteeringContext(agent)).GetProperty("calls").EnumerateArray());
        Assert.Equal("success", call.GetProperty("status").GetString()); Assert.False(decision.Disposed);
    }

    [Fact]
    public async Task GuidanceComesFromTheValidatedStructuredDecisionAndSkipsTheEffect()
    {
        var decision = new DecisionModel("{\"type\":\"guide\",\"reason\":\"Repeated failures: choose another action\"}"); var effects = 0;
        await using var agent = new Agent(new() { Model = new ToolModel(), AuxModel = decision, Tools = [Tool(() => effects++)], Interventions = [new LlmSteeringHandler(new() { SystemPrompt = "steer", ContextProviders = [] })] });
        await agent.InvokeAsync("task");
        Assert.Equal(0, effects); Assert.Contains("GUIDANCE: [strands:llm-steering-handler] Repeated failures", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
        Assert.Single(decision.Requests);
    }

    [Theory]
    [InlineData("{\"type\":\"confirm\",\"reason\":\"Approve the retry?\"}")]
    [InlineData("{\"decision\":\"interrupt\",\"reason\":\"Approve the retry?\"}")]
    public async Task TypeScriptConfirmAndPythonInterruptDecisionsUseDurableApproval(string response)
    {
        var decision = new DecisionModel(response); var effects = 0;
        await using var agent = new Agent(new() { Model = new ToolModel(), AuxModel = decision, Tools = [Tool(() => effects++)], Interventions = [new LlmSteeringHandler(new() { SystemPrompt = "steer", ContextProviders = [] })] });
        var paused = await agent.InvokeAsync("task");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Equal(0, effects);
        var question = Assert.Single(paused.Interrupts); Assert.Equal("Approve the retry?", question.Reason?.GetString());
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] })).StopReason);
        Assert.Equal(1, effects); Assert.Equal(2, decision.Requests.Count);
    }

    [Fact]
    public async Task ExplicitModelAndRichPromptBuilderWinWithoutSharingParentContext()
    {
        var decision = new DecisionModel("{\"type\":\"proceed\",\"reason\":\"Allowed\"}");
        var steering = new LlmSteeringHandler(new() { Model = decision, Name = "custom", SystemPrompt = "custom rules", ContextProviders = [], PromptBuilder = (context, call) => { Assert.Empty(context); Assert.Equal("effect", call!.Name); return [new TextBlock("custom"), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 42 }))]; } });
        await using var agent = new Agent(new() { Model = new ToolModel(), AuxModel = new NoCallsModel(), Tools = [Tool(() => { })], Interventions = [steering] });
        await agent.InvokeAsync("task");
        var request = Assert.Single(decision.Requests); Assert.Equal("custom rules", request.SystemPrompt);
        Assert.Equal(42, Assert.IsType<JsonBlock>(request.Messages[0].Content[1]).Json.GetProperty("value").GetInt32()); Assert.Empty(steering.GetSteeringContext(agent));
    }

    [Theory]
    [InlineData("{\"type\":\"deny\",\"reason\":\"outside the steering contract\"}")]
    [InlineData("{\"type\":\"proceed\",\"decision\":\"guide\",\"reason\":\"ambiguous\"}")]
    public async Task InvalidOrAmbiguousDecisionsUseValidationFeedbackAndNeverExecuteATool(string response)
    {
        var model = new DecisionModel(response); var effects = 0;
        await using var agent = new Agent(new() { Model = new ToolModel(), AuxModel = model, Tools = [Tool(() => effects++)], Interventions = [new LlmSteeringHandler(new() { SystemPrompt = "steer", ContextProviders = [] })] });
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeAsync("task"));
        Assert.Equal(0, effects); Assert.True(model.Requests.Count > 1);
        Assert.Equal(ToolResultStatus.Error, Assert.Single(model.Requests[1].Messages.Last().Content.OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task CancellationStopsTheAuxiliaryModelWithoutDisposingItOrExecutingTheEffect()
    {
        using var cancellation = new CancellationTokenSource(); var model = new WaitingModel(); var effects = 0;
        await using var agent = new Agent(new() { Model = new ToolModel(), AuxModel = model, Tools = [Tool(() => effects++)], Interventions = [new LlmSteeringHandler(new() { SystemPrompt = "steer", ContextProviders = [] })] });
        var invocation = agent.InvokeAsync("task", cancellationToken: cancellation.Token);
        Assert.Same(model.Started.Task, await Task.WhenAny(model.Started.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        cancellation.Cancel();
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason); Assert.Equal(0, effects); Assert.False(model.Disposed);
    }

    [Fact]
    public async Task InvalidPromptBuilderFailsBeforeAnyAuxiliaryModelCall()
    {
        var model = new DecisionModel("{}");
        await using var agent = new Agent(new() { Model = new ToolModel(), AuxModel = model, Tools = [Tool(() => { })], Interventions = [new LlmSteeringHandler(new() { SystemPrompt = "steer", PromptBuilder = (_, _) => null!, ContextProviders = [] })] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("task")); Assert.Empty(model.Requests);
    }

    private static FunctionTool Tool(Action effect) => new(new("effect", "effect", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { effect(); return new ValueTask<string>("executed"); });
    private sealed class DecisionModel(string response) : IModel, IAsyncDisposable
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal bool Disposed { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Requests.Add(request);
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0, new("decision", StructuredOutputTool<object>.ToolName, JsonSerializer.Deserialize<JsonElement>(response)));
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
    private sealed class NoCallsModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("This model must not be called.");
    }
    private sealed class WaitingModel : IModel, IAsyncDisposable
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Started.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); yield break; }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
    private sealed class ToolModel : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent();
            if (_calls++ == 0) yield return new ModelContentBlockStartEvent(0, new("call", "effect", JsonSerializer.SerializeToElement(new { })));
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); }
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(_calls == 1 ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
