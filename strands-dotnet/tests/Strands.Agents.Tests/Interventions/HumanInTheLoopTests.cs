using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Interventions;

namespace Strands.Agents.Tests.Interventions;

public sealed class HumanInTheLoopTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static FunctionTool Tool(string name, Action effect) => new(new(name, name, Json("{\"type\":\"object\"}")), (_, _, _) => { effect(); return new ValueTask<string>("executed"); });

    [Fact]
    public async Task EveryToolRequiresApprovalByDefaultAndResumesExactlyOnce()
    {
        var effects = 0; var model = new Model("write");
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("write", () => effects++)], Interventions = [new HumanInTheLoop()] });
        var paused = await agent.InvokeAsync("task");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Equal(0, effects);
        var question = Assert.Single(paused.Interrupts);
        Assert.Equal("strands:human-in-the-loop", question.Name); Assert.Equal("Approve \"write\"?\n  Input: {}", question.Reason?.GetString());
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] })).StopReason);
        Assert.Equal(1, effects); Assert.Equal(2, model.Calls);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("*")]
    public async Task AllowlistedToolsBypassBothClassifierAndPrompt(string allowed)
    {
        var effects = 0;
        var approval = new HumanInTheLoop(new() { AllowedTools = [allowed], Classifier = (_, _) => throw new IOException("must not classify"), Ask = (_, _) => throw new IOException("must not ask") });
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("task")).StopReason); Assert.Equal(1, effects);
    }

    [Fact]
    public async Task NegatedToolOverridesWildcardAndExistingTrustAndCannotBeTrusted()
    {
        var effects = 0; var asked = 0;
        var approval = new HumanInTheLoop(new() { AllowedTools = ["*", "!write"], EnableTrust = true, Ask = (_, _) => { asked++; return new ValueTask<JsonElement?>(Json("\"trust\"")); } });
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        agent.State.Set(HumanInTheLoop.TrustedToolsStateKey, new[] { "write" });
        await agent.InvokeAsync("task");
        Assert.Equal(1, asked); Assert.Equal(0, effects); Assert.Null(agent.InterruptState);
        Assert.Contains("CONFIRMATION_FAILED", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task TrustResponseApprovesAndPersistsNamesForLaterCalls()
    {
        var effects = 0; var asked = 0;
        var approval = new HumanInTheLoop(new() { EnableTrust = true, Ask = (_, _) => { asked++; return new ValueTask<JsonElement?>(Json("\" TrUsT \"")); } });
        await using var agent = new Agent(new() { Model = new Model("write", "write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        await agent.InvokeAsync("first"); await agent.InvokeAsync("second");
        Assert.Equal(2, effects); Assert.Equal(1, asked); Assert.Equal(new[] { "write" }, agent.State.Get<string[]>(HumanInTheLoop.TrustedToolsStateKey));
    }

    [Fact]
    public async Task TrustDuringInterruptResumeSurvivesSessionRestore()
    {
        var store = new Store(); var effects = 0;
        await using (var original = new Agent(new() { Model = new Model("write"), SessionStore = store, SessionId = "session", Tools = [Tool("write", () => effects++)], Interventions = [new HumanInTheLoop(new() { EnableTrust = true })] }))
        {
            var paused = await original.InvokeAsync("task");
            await original.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, Json("\"t\""))] });
        }
        await using var restored = new Agent(new() { Model = new Model("write"), SessionStore = store, SessionId = "session", Tools = [Tool("write", () => effects++)], Interventions = [new HumanInTheLoop(new() { EnableTrust = true })] });
        Assert.Equal(StopReasons.EndTurn, (await restored.InvokeAsync("again")).StopReason); Assert.Equal(2, effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineMissingAndExplicitJsonNullResponsesDeclineWithoutCreatingAnInterrupt(bool explicitNull)
    {
        var effects = 0;
        var approval = new HumanInTheLoop(new() { Ask = (_, _) => new ValueTask<JsonElement?>(explicitNull ? Json("null") : null) });
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("task")).StopReason); Assert.Equal(0, effects); Assert.Null(agent.InterruptState);
        Assert.Equal(ToolResultStatus.Error, Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task ClassifierFailureRequiresApprovalAndDurableQuestionsSkipReclassification()
    {
        var count = 0; var store = new Store();
        await using (var original = new Agent(new() { Model = new Model("write"), SessionStore = store, SessionId = "session", Tools = [Tool("write", () => { })], Interventions = [new HumanInTheLoop(new() { Classifier = (_, _) => { count++; throw new IOException("classifier unavailable"); } })] }))
            Assert.Equal(StopReasons.Interrupt, (await original.InvokeAsync("task")).StopReason);
        await using var restored = new Agent(new() { Model = new Model(), SessionStore = store, SessionId = "session", Tools = [Tool("write", () => { })], Interventions = [new HumanInTheLoop(new() { Classifier = (_, _) => { count++; return new(new HumanInTheLoopClassifierResult(false)); } })] });
        var question = Assert.Single(store.Snapshot!.InterruptState!.Interrupts);
        Assert.Equal(StopReasons.EndTurn, (await restored.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] })).StopReason); Assert.Equal(1, count);
    }

    [Fact]
    public async Task SharedHandlerAndRepeatedProviderIdsDoNotShareClassificationAcrossAgentsOrBatches()
    {
        var classified = 0; var effects = 0;
        var approval = new HumanInTheLoop(new() { Classifier = (_, _) => { classified++; return new(new HumanInTheLoopClassifierResult(false)); } });
        await using var one = new Agent(new() { Model = new Model("write", "write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        await using var two = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        await one.InvokeAsync("one"); await one.InvokeAsync("two"); await two.InvokeAsync("three");
        Assert.Equal(3, classified); Assert.Equal(3, effects);
    }

    [Fact]
    public async Task ClassifierReceivesOriginalCallerContextAndFailureDoesNotSwallowCancellation()
    {
        using var source = new CancellationTokenSource(); var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new Dictionary<string, object?> { ["caller"] = true };
        var approval = new HumanInTheLoop(new() { Classifier = async (value, token) => { Assert.Same(state, value.InvocationState); Assert.True(token.CanBeCanceled); started.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return new(false); } });
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => throw new InvalidOperationException("must not execute"))], Interventions = [approval] });
        var invocation = agent.InvokeAsync("task", new() { InvocationState = state }, source.Token); await started.Task; source.Cancel();
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason); Assert.Null(agent.InterruptState); Assert.Single(state);
    }

    [Fact]
    public async Task InlinePromptFailurePropagatesTheOriginalError()
    {
        var error = new IOException("UI disconnected");
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => { })], Interventions = [new HumanInTheLoop(new() { Ask = (_, _) => throw error })] });
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("task")));
    }

    [Fact]
    public async Task TrustIsDisabledByDefaultAndNullClassificationRequiresApproval()
    {
        var effects = 0; var asked = 0;
        var approval = new HumanInTheLoop(new() { Classifier = (_, _) => new((HumanInTheLoopClassifierResult)null!), Ask = (_, _) => { asked++; return new ValueTask<JsonElement?>(Json("\"trust\"")); } });
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        await agent.InvokeAsync("task");
        Assert.Equal(1, asked); Assert.Equal(0, effects); Assert.Null(agent.State.Get(HumanInTheLoop.TrustedToolsStateKey)); Assert.Null(agent.InterruptState);
    }

    [Fact]
    public async Task ClassificationReasonAndCustomApprovalAndTrustEvaluatorsReachThePrompt()
    {
        var effects = 0; var prompts = new List<string>();
        var approval = new HumanInTheLoop(new()
        {
            EnableTrust = true,
            Classifier = (_, _) => new(new HumanInTheLoopClassifierResult(true, "Changes shared state")),
            Ask = (prompt, _) => { prompts.Add(prompt); return new ValueTask<JsonElement?>(Json("{\"approved\":true}")); },
            EvaluateTrust = value => value.ValueKind == JsonValueKind.Object && value.GetProperty("approved").GetBoolean(),
            Evaluate = _ => throw new InvalidOperationException("Trust wins before ordinary approval evaluation.")
        });
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => effects++)], Interventions = [approval] });
        await agent.InvokeAsync("task");
        Assert.Equal("Approve \"write\" — Changes shared state?\n  Input: {}", Assert.Single(prompts));
        Assert.Equal(1, effects); Assert.Equal(new[] { "write" }, agent.State.Get<string[]>(HumanInTheLoop.TrustedToolsStateKey));
    }

    [Fact]
    public async Task AllowedToolConfigurationIsCopiedBeforeCallerMutation()
    {
        var allowed = new List<string> { "read" };
        var approval = new HumanInTheLoop(new() { AllowedTools = allowed }); allowed.Add("write");
        await using var agent = new Agent(new() { Model = new Model("write"), Tools = [Tool("write", () => throw new InvalidOperationException("must remain gated"))], Interventions = [approval] });
        Assert.Equal(StopReasons.Interrupt, (await agent.InvokeAsync("task")).StopReason);
    }

    [Fact]
    public async Task ConcurrentTrustResponsesRetainEveryToolName()
    {
        var entered = 0; var effects = 0;
        var both = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new HumanInTheLoop(new() { EnableTrust = true, Ask = async (_, _) => { if (Interlocked.Increment(ref entered) == 2) both.TrySetResult(true); await both.Task; return Json("\"t\""); } });
        await using var agent = new Agent(new() { Model = new BatchModel(), ToolExecution = ToolExecutionStrategy.Concurrent, Tools = [Tool("one", () => Interlocked.Increment(ref effects)), Tool("two", () => Interlocked.Increment(ref effects))], Interventions = [approval] });
        var invocation = agent.InvokeAsync("task");
        Assert.Same(invocation, await Task.WhenAny(invocation, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(StopReasons.EndTurn, (await invocation).StopReason); Assert.Equal(2, effects);
        Assert.Equal(new[] { "one", "two" }, agent.State.Get<string[]>(HumanInTheLoop.TrustedToolsStateKey)!.OrderBy(value => value));
    }

    private sealed class Store : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Snapshot = snapshot; return default; }
    }

    private sealed class Model(params string[] tools) : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; var index = Calls++;
            yield return new ModelMessageStartEvent();
            if (index % 2 == 0 && index / 2 < tools.Length)
            {
                yield return new ModelContentBlockStartEvent(0, new("same-id", tools[index / 2], Json("{}")));
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }

    private sealed class BatchModel : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; yield return new ModelMessageStartEvent();
            if (_calls++ == 0)
            {
                for (var index = 0; index < 2; index++)
                {
                    var name = index == 0 ? "one" : "two";
                    yield return new ModelContentBlockStartEvent(index, new(name, name, Json("{}"))); yield return new ModelContentBlockStopEvent(index);
                }
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }
}
