using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Interventions;

namespace Strands.Agents.Tests.Interventions;

public sealed class LlmRiskClassifierTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    [Fact]
    public async Task DefaultClassifierUsesAuxModelAndARealStructuredDecisionInAFreshConversation()
    {
        var main = new NoCallsModel(); var auxiliary = new DecisionModel(false, "Read only");
        await using var agent = new Agent(new() { Model = main, AuxModel = auxiliary, SystemPrompt = "private parent prompt", Messages = [Message.User("private history")] });
        agent.State.Set("private", true);
        var classify = LlmRiskClassifier.Create();
        var decision = await classify(new(agent, new("call", "list", Json("{\"directory\":\"public\"}")), new Dictionary<string, object?>()), default);
        Assert.False(decision.RequiresHumanInTheLoop); Assert.Equal("Read only", decision.Reason);
        var request = Assert.Single(auxiliary.Requests);
        Assert.Contains("risk evaluator", request.SystemPrompt); Assert.DoesNotContain("private", JsonSerializer.Serialize(request));
        Assert.Equal(StructuredOutputTool<object>.ToolName, Assert.Single(request.Tools!).Name);
        Assert.Contains("Tool: list", request.Messages[0].Text); Assert.Contains("public", request.Messages[0].Text);
        Assert.Single(agent.Messages); Assert.True(agent.State.Get<bool>("private"));
    }

    [Fact]
    public async Task ExplicitModelAndPromptWinAndRepeatedCallsHaveIndependentConversations()
    {
        var explicitModel = new DecisionModel(true, "Destructive");
        await using var agent = new Agent(new() { Model = new NoCallsModel(), AuxModel = new NoCallsModel() });
        var classify = LlmRiskClassifier.Create(new() { Model = explicitModel, SystemPrompt = "custom criteria" });
        var value = new BeforeToolCallEvent(agent, new("id", "delete", Json("{}")), new Dictionary<string, object?>());
        Assert.True((await classify(value, default)).RequiresHumanInTheLoop);
        Assert.True((await classify(value, default)).RequiresHumanInTheLoop);
        Assert.Equal(2, explicitModel.Requests.Count);
        Assert.All(explicitModel.Requests, request => { Assert.Equal("custom criteria", request.SystemPrompt); Assert.Single(request.Messages); });
        Assert.False(explicitModel.Disposed);
    }

    [Fact]
    public async Task PreCancelledClassificationDoesNotCallAModel()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await using var agent = new Agent(new() { Model = new NoCallsModel() });
        var classify = LlmRiskClassifier.Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classify(new(agent, new("id", "tool", Json("{}")), new Dictionary<string, object?>()), canceled.Token).AsTask());
    }

    private sealed class NoCallsModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Wrong model was selected.");
    }

    private sealed class DecisionModel(bool required, string reason) : IModel, IAsyncDisposable
    {
        internal List<ModelRequest> Requests { get; } = [];
        internal bool Disposed { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Requests.Add(request);
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new("decision", StructuredOutputTool<object>.ToolName, JsonSerializer.SerializeToElement(new { requiresApproval = required, reason })));
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
}
