using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Interventions;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.Interventions;

public sealed class InterventionTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static Message Text(string value) => new(Role.Assistant, [new TextBlock(value)]);
    private static Message Call() => new(Role.Assistant, [new ToolUseBlock("call", "effect", Json("{\"value\":1}"))]);
    private static FunctionTool Tool(Func<JsonElement, string> body) => new(new("effect", "effect", Json("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"]}")), (input, _, _) => new ValueTask<string>(body(input)));

    [Fact]
    public async Task DenyBeforeInvocationSkipsInputAndModelAndStopsLaterHandlers()
    {
        var model = new Model(Text("unprotected"));
        var later = 0;
        var state = new Dictionary<string, object?> { ["caller"] = 42 };
        await using var agent = new Agent(new() { Model = model, Interventions = [new Handler("deny", value => value is BeforeInvocationEvent ? new Deny("blocked") : new Proceed()), new Handler("later", value => { if (value is BeforeInvocationEvent) later++; return new Proceed(); })] });
        var result = await agent.InvokeAsync("private input", new() { InvocationState = state });
        Assert.Equal("DENIED: blocked", result.ToString()); Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Empty(model.Requests); Assert.Equal(0, later); Assert.Same(state, result.InvocationState);
        Assert.Equal("DENIED: blocked", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task BeforeModelDenialSkipsMiddlewareButKeepsAfterModelBoundary()
    {
        var model = new Model(Text("unprotected")); var middlewareCalls = 0; var after = 0;
        var middleware = new MiddlewareRegistry();
        middleware.RegisterInput(MiddlewareStages.InvokeModel, (context, _) => { middlewareCalls++; return new(context); });
        var hooks = new HookRegistry(); hooks.Register<AfterModelCallEvent>((value, _) => { after++; Assert.Equal("DENIED: model blocked", value.Message?.Text); return default; });
        await using var agent = new Agent(new() { Model = model, Middleware = middleware, Hooks = hooks, Interventions = [new Handler("deny", value => value is BeforeModelCallEvent ? new Deny("model blocked") : new Proceed())] });
        Assert.Equal("DENIED: model blocked", (await agent.InvokeAsync("question")).ToString());
        Assert.Empty(model.Requests); Assert.Equal(0, middlewareCalls); Assert.Equal(1, after);
        Assert.Equal(2, agent.Messages.Count);
    }

    [Fact]
    public async Task BeforeModelGuidanceAccumulatesAndUsesDurableMessageHooks()
    {
        var model = new Model(Text("accepted")); var added = new List<Message>(); var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) => { added.Add(value.Message); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Interventions = [new Handler("one", value => value is BeforeModelCallEvent ? new Guide("first") : new Proceed()), new Handler("two", value => value is BeforeModelCallEvent ? new Guide("second") : new Proceed())] });
        await agent.InvokeAsync("question");
        Assert.Equal("[one] first\n[two] second", model.Requests.Single().Messages.Last().Text);
        Assert.Equal("[one] first\n[two] second", agent.Messages[1].Text);
        Assert.Equal(3, added.Count);
    }

    [Fact]
    public async Task AfterModelGuidanceRetriesBeforeCommitAndAccountsForBothAttempts()
    {
        var model = new Model(Text("reject"), Text("accepted"));
        await using var agent = new Agent(new() { Model = model, Interventions = [new Handler("quality", value => value is AfterModelCallEvent { Message.Text: "reject" } ? new Guide("give a precise answer") : new Proceed())] });
        var result = await agent.InvokeAsync("question");
        Assert.Equal("accepted", result.ToString()); Assert.Equal(2, model.Requests.Count); Assert.Equal(4, result.Usage.TotalTokens);
        Assert.DoesNotContain(agent.Messages, value => value.Text == "reject");
        Assert.Equal("[quality] give a precise answer", model.Requests[1].Messages.Last().Text);
        Assert.Equal(1, result.Turns);
    }

    [Fact]
    public async Task TransformsAffectLaterHandlersToolInputResultAndFinalModelMessage()
    {
        var effects = 0; var observed = 0;
        var transform = new Handler("transform", value => value switch
        {
            BeforeToolCallEvent => new Transform(current => { var tool = (BeforeToolCallEvent)current; tool.ToolUse = tool.ToolUse with { Input = Json("{\"value\":2}") }; }),
            AfterToolCallEvent => new Transform(current => ((AfterToolCallEvent)current).Result = new("forged", "public result")),
            AfterModelCallEvent { Message.Text: "raw final" } => new Transform(current => ((AfterModelCallEvent)current).Message = Text("transformed final")),
            _ => new Proceed()
        });
        var later = new Handler("later", value => { if (value is BeforeToolCallEvent tool) observed = tool.ToolUse.Input.GetProperty("value").GetInt32(); return new Proceed(); });
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("raw final")), Tools = [Tool(input => { effects++; Assert.Equal(2, input.GetProperty("value").GetInt32()); return "private result"; })], Interventions = [transform, later] });
        Assert.Equal("transformed final", (await agent.InvokeAsync("task")).ToString());
        Assert.Equal(1, effects); Assert.Equal(2, observed);
        var result = Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>());
        Assert.Equal("call", result.ToolUseId); Assert.Equal("public result", result.Content);
    }

    [Fact]
    public async Task ConfirmWithNoResponsePausesEvenWhenHandlerErrorsNormallyProceed()
    {
        var effects = 0;
        var handler = new Handler("approval", value => value is BeforeToolCallEvent ? new Confirm("Approve this action?") : new Proceed()) { ErrorPolicy = InterventionErrorPolicy.Proceed };
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("done")), Tools = [Tool(_ => { effects++; return "saved"; })], Interventions = [handler] });
        var paused = await agent.InvokeAsync("task");
        var question = Assert.Single(paused.Interrupts); Assert.Equal("approval", question.Name); Assert.Equal("Approve this action?", question.Reason!.Value.GetString());
        Assert.Equal(0, effects);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("\" YeS \""))] });
        Assert.Equal(StopReasons.EndTurn, result.StopReason); Assert.Equal(1, effects);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    public async Task ExplicitDeclinedConfirmResponseDoesNotPauseOrExecuteTheTool(string response)
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("done")), Tools = [Tool(_ => { effects++; return "saved"; })], Interventions = [new Handler("approval", value => value is BeforeToolCallEvent ? new Confirm("Approve?", response: Json(response)) : new Proceed())] });
        var result = await agent.InvokeAsync("task");
        Assert.Equal(StopReasons.EndTurn, result.StopReason); Assert.Equal(0, effects); Assert.Null(agent.InterruptState);
        Assert.Equal("CONFIRMATION_FAILED: Approve?", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
    }

    [Theory]
    [InlineData(InterventionErrorPolicy.Throw)]
    [InlineData(InterventionErrorPolicy.Proceed)]
    [InlineData(InterventionErrorPolicy.Deny)]
    public async Task HandlerFailurePolicyPreservesDefaultErrorOrContinuesOrDenies(InterventionErrorPolicy policy)
    {
        var failure = new IOException("policy failed"); var model = new Model(Text("accepted"));
        var handler = new Handler("policy", value => value is BeforeInvocationEvent ? throw failure : new Proceed()) { ErrorPolicy = policy };
        await using var agent = new Agent(new() { Model = model, Interventions = [handler] });
        if (policy == InterventionErrorPolicy.Throw) Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question")));
        else Assert.Equal(policy == InterventionErrorPolicy.Proceed ? "accepted" : "DENIED: Handler threw: policy failed", (await agent.InvokeAsync("question")).ToString());
        Assert.Equal(policy == InterventionErrorPolicy.Proceed ? 1 : 0, model.Requests.Count);
    }

    [Fact]
    public async Task StandaloneRegistryDisposalRemovesOnlyItsOwnCallbacks()
    {
        var hooks = new HookRegistry(); var calls = 0;
        hooks.Register<BeforeInvocationEvent>((_, _) => { calls++; return default; });
        await using var agent = new Agent(new() { Model = new Model(Text("plain")), Hooks = hooks });
        using (new InterventionRegistry([new Handler("deny", value => value is BeforeInvocationEvent ? new Deny("blocked") : new Proceed())], agent.Hooks))
            Assert.Equal("DENIED: blocked", (await agent.InvokeAsync("first")).ToString());
        Assert.Equal("plain", (await agent.InvokeAsync("second")).ToString());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DefaultEvaluatorAndDetachedResponseFollowTheSharedSourceContract()
    {
        Assert.Equal(InterventionErrorPolicy.Throw, new Handler("default", _ => new Proceed()).OnError);
        foreach (var input in new[] { "true", "\"y\"", "\" YES \"" }) Assert.True(Confirm.DefaultEvaluate(Json(input)));
        foreach (var input in new[] { "false", "null", "1", "{}", "\"approve\"" }) Assert.False(Confirm.DefaultEvaluate(Json(input)));
        using var document = JsonDocument.Parse("\"yes\"");
        var value = new Confirm("approve", response: document.RootElement); document.Dispose();
        Assert.True(value.Evaluate(value.Response!.Value));
    }

    [Fact]
    public async Task EarlyModelStreamDisposalDoesNotRequireACompletedResponse()
    {
        var observed = 0; var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) => { observed++; Assert.Null(value.Message); return default; });
        await using var agent = new Agent(new() { Model = new Model(Text("not committed")), Hooks = hooks });
        await using (var iterator = agent.StreamAsync("question").GetAsyncEnumerator())
            while (await iterator.MoveNextAsync()) if (iterator.Current is ModelStreamUpdateEvent) break;
        Assert.Equal(1, observed); Assert.Equal("question", Assert.Single(agent.Messages).Text);
    }

    [Theory]
    [InlineData("null-message")]
    [InlineData("user-role")]
    [InlineData("null-stop")]
    [InlineData("tool-stop-without-call")]
    public async Task InvalidAfterModelTransformIsRejectedBeforeCommit(string mutation)
    {
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) =>
        {
            if (mutation == "null-message") value.Message = null;
            if (mutation == "user-role") value.Message = Message.User("invalid role");
            if (mutation == "null-stop") value.StopReason = null;
            if (mutation == "tool-stop-without-call") value.StopReason = StopReasons.ToolUse;
            return default;
        });
        await using var agent = new Agent(new() { Model = new Model(Text("response")), Hooks = hooks });
        await Assert.ThrowsAnyAsync<ArgumentException>(() => agent.InvokeAsync("question"));
        Assert.Equal("question", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task InterventionPrioritiesSurroundOrdinaryHooksAndMiddleware()
    {
        var observed = new List<string>(); var hooks = new HookRegistry(); var middleware = new MiddlewareRegistry();
        hooks.Register<BeforeModelCallEvent>((_, _) => { observed.Add("ordinary input"); return default; });
        hooks.Register<BeforeModelCallEvent>((_, _) => { observed.Add("SDK last"); return default; }, HookOrder.SdkLast);
        hooks.Register<AfterModelCallEvent>((_, _) => { observed.Add("ordinary output"); return default; });
        hooks.Register<AfterModelCallEvent>((_, _) => { observed.Add("SDK first"); return default; }, HookOrder.SdkFirst);
        middleware.RegisterInput(MiddlewareStages.InvokeModel, (value, _) => { observed.Add("middleware"); return new(value); });
        var handler = new Handler("policy", value => { if (value is BeforeModelCallEvent) observed.Add("policy input"); if (value is AfterModelCallEvent) observed.Add("policy output"); return new Proceed(); });
        await using var agent = new Agent(new() { Model = new Model(Text("accepted")), Hooks = hooks, Middleware = middleware, Interventions = [handler] });
        await agent.InvokeAsync("question");
        Assert.Equal(new[] { "ordinary input", "policy input", "SDK last", "middleware", "SDK first", "policy output", "ordinary output" }, observed);
    }

    [Fact]
    public async Task GuidanceBeforeToolAccumulatesButALaterDenyWinsWithoutExecution()
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("accepted")), Tools = [Tool(_ => { effects++; return "ran"; })], Interventions = [new Handler("guide", value => value is BeforeToolCallEvent ? new Guide("try another way") : new Proceed()), new Handler("deny", value => value is BeforeToolCallEvent ? new Deny("not allowed") : new Proceed())] });
        await agent.InvokeAsync("task");
        Assert.Equal(0, effects); Assert.Equal("DENIED: not allowed", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task DenyingAResumePreservesItsPendingBatchAndAcceptedAnswer()
    {
        var enabled = false; var effects = 0;
        var policy = new Handler("policy", value => value switch
        {
            BeforeInvocationEvent when enabled => new Deny("temporarily unavailable"),
            BeforeToolCallEvent => new Confirm("approve"),
            _ => new Proceed()
        });
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("done")), Tools = [Tool(_ => { effects++; return "saved"; })], Interventions = [policy] });
        var question = Assert.Single((await agent.InvokeAsync("task")).Interrupts);
        enabled = true;
        Assert.Equal("DENIED: temporarily unavailable", (await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] })).ToString());
        Assert.Equal(Role.Assistant, agent.Messages.Last().Role); Assert.Single(agent.InterruptState!.Responses); Assert.Equal(0, effects);
        enabled = false;
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync(null)).StopReason); Assert.Equal(1, effects);
    }

    [Fact]
    public async Task ParentDisposalLeavesFreshChildPoliciesAndHandlerSnapshotIntact()
    {
        var observed = 0;
        var handlers = new InterventionHandler[] { new Handler("policy", value => { if (value is BeforeInvocationEvent) observed++; return new Proceed(); }) };
        var parent = new Agent(new() { Model = new Model(Text("parent"), Text("child")), Interventions = handlers });
        handlers[0] = new Handler("replaced", _ => throw new IOException("must not be used"));
        Assert.Equal("policy", Assert.Single(parent.Interventions).Name);
        await parent.InvokeAsync("parent");
        await using var child = parent.CreateChild();
        await parent.DisposeAsync();
        await child.InvokeAsync("child");
        Assert.Equal(2, observed);
    }

    [Fact]
    public async Task AfterModelTransformCannotRewriteAServerManagedToolIdentifier()
    {
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) => { if (value.Message?.Content.OfType<ToolUseBlock>().SingleOrDefault() is { } call) value.Message = new(Role.Assistant, [call with { ToolUseId = "forged" }]); return default; });
        await using var agent = new Agent(new() { Model = new StatefulModel(), Hooks = hooks, Tools = [Tool(_ => throw new InvalidOperationException("must not execute"))] });
        agent.ModelState.Set("id", "prior");
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("question"));
        Assert.Equal("prior", agent.ModelState.Get<string>("id")); Assert.Equal("question", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task GuidanceRejectingProposedToolsDoesNotCommitOrExecuteThem()
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("better answer")), Tools = [Tool(_ => { effects++; return "ran"; })], Interventions = [new Handler("review", value => value is AfterModelCallEvent { Message: { } message } && message.Content.OfType<ToolUseBlock>().Any() ? new Guide("answer without side effects") : new Proceed())] });
        Assert.Equal("better answer", (await agent.InvokeAsync("question")).ToString());
        Assert.Equal(0, effects); Assert.DoesNotContain(agent.Messages, value => value.Content.OfType<ToolUseBlock>().Any());
    }

    [Fact]
    public async Task GuidanceRetryHasNoInventedThreeAttemptDefault()
    {
        var calls = 0;
        var model = new Model(Text("one"), Text("two"), Text("three"), Text("four"), Text("accepted"));
        await using var agent = new Agent(new() { Model = model, Interventions = [new Handler("converging", value => value is AfterModelCallEvent && ++calls < 5 ? new Guide("try again") : new Proceed())] });
        Assert.Equal("accepted", (await agent.InvokeAsync("question")).ToString()); Assert.Equal(5, model.Requests.Count);
    }

    [Theory]
    [InlineData(InterventionErrorPolicy.Throw)]
    [InlineData(InterventionErrorPolicy.Proceed)]
    [InlineData(InterventionErrorPolicy.Deny)]
    public async Task TransformFailureHonorsTheSameHandlerErrorPolicy(InterventionErrorPolicy policy)
    {
        var error = new IOException("transform failed"); var effects = 0;
        var handler = new Handler("policy", value => value is BeforeToolCallEvent ? new Transform(_ => throw error) : new Proceed()) { ErrorPolicy = policy };
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("done")), Tools = [Tool(_ => { effects++; return "ran"; })], Interventions = [handler] });
        if (policy == InterventionErrorPolicy.Throw) Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("task")));
        else await agent.InvokeAsync("task");
        Assert.Equal(policy == InterventionErrorPolicy.Proceed ? 1 : 0, effects);
        if (policy == InterventionErrorPolicy.Deny) Assert.Equal("DENIED: Handler threw: transform failed", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public async Task CancellationDuringAwaitedEvaluationIsNeverConvertedToProceed()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Model(Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Interventions = [new AwaitedHandler(started)] });
        var invocation = agent.InvokeAsync("question", cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason); Assert.Empty(model.Requests);
        Assert.Equal("question", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task InvalidNullActionCannotBeHiddenByAProceedErrorPolicy()
    {
        var handler = new Handler("invalid", value => value is BeforeInvocationEvent ? null! : new Proceed()) { ErrorPolicy = InterventionErrorPolicy.Proceed };
        var model = new Model(Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Interventions = [handler] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("question"));
        Assert.Empty(model.Requests); Assert.Empty(agent.Messages);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("guide")]
    [InlineData("confirm")]
    public async Task UnsupportedAfterToolActionsHaveNoModelVisibleEffect(string action)
    {
        IInterventionAction decision = action switch { "deny" => new Deny("denial"), "guide" => new Guide("feedback"), _ => new Confirm("approval") };
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("done")), Tools = [Tool(_ => "actual result")], Interventions = [new Handler("policy", value => value is AfterToolCallEvent ? decision : new Proceed())] });
        await agent.InvokeAsync("task");
        Assert.Null(agent.InterruptState); Assert.Equal("actual result", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
    }

    [Fact]
    public void DuplicateHandlersAreRejectedBeforeRegisteringAnyHooks()
    {
        var hooks = new HookRegistry();
        Assert.Throws<ArgumentException>(() => new InterventionRegistry([new Handler("same", _ => new Proceed()), new Handler("same", _ => new Proceed())], hooks));
    }

    [Fact]
    public async Task EveryPolicyBoundaryReceivesTheSameCallerOwnedContextWithoutSdkKeys()
    {
        var context = new Dictionary<string, object?> { ["role"] = "operator" };
        var observed = new List<Type>();
        var handler = new Handler("context", value =>
        {
            IReadOnlyDictionary<string, object?> state = value switch
            {
                BeforeInvocationEvent before => before.InvocationState,
                BeforeModelCallEvent before => before.InvocationState,
                AfterModelCallEvent after => after.InvocationState,
                BeforeToolCallEvent before => before.InvocationState,
                AfterToolCallEvent after => after.InvocationState,
                _ => throw new InvalidOperationException("Unexpected intervention boundary.")
            };
            Assert.Same(context, state); Assert.Equal("operator", state["role"]); observed.Add(value.GetType());
            return new Proceed();
        });
        await using var agent = new Agent(new() { Model = new Model(Call(), Text("done")), Tools = [Tool(_ => "done")], Interventions = [handler] });
        var result = await agent.InvokeAsync("task", new() { InvocationState = context });
        Assert.Same(context, result.InvocationState); Assert.Single(context); Assert.Equal(5, observed.Distinct().Count());
    }

    private sealed class AwaitedHandler(TaskCompletionSource<bool> started) : InterventionHandler
    {
        public override string Name => "awaited";
        public override InterventionErrorPolicy OnError => InterventionErrorPolicy.Proceed;
        public override async ValueTask<IInterventionAction> BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken = default)
        { started.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); return new Proceed(); }
    }

    private sealed class StatefulModel : IStatefulModel
    {
        private readonly Model _inner = new(Call());
        public bool Stateful => true;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in _inner.StreamAsync(request, cancellationToken))
                yield return item is ModelMessageStopEvent stop ? stop with { ModelState = new Dictionary<string, JsonElement> { ["id"] = Json("\"new\"") } } : item;
        }
    }

    private sealed class Handler(string name, Func<HookEvent, IInterventionAction> callback) : InterventionHandler
    {
        public override string Name => name;
        public InterventionErrorPolicy ErrorPolicy { get; init; }
        public override InterventionErrorPolicy OnError => ErrorPolicy;
        public override ValueTask<IInterventionAction> BeforeInvocationAsync(BeforeInvocationEvent value, CancellationToken cancellationToken = default) => new(callback(value));
        public override ValueTask<IInterventionAction> BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken = default) => new(callback(value));
        public override ValueTask<IInterventionAction> AfterModelCallAsync(AfterModelCallEvent value, CancellationToken cancellationToken = default) => new(callback(value));
        public override ValueTask<IInterventionAction> BeforeToolCallAsync(BeforeToolCallEvent value, CancellationToken cancellationToken = default) => new(callback(value));
        public override ValueTask<IInterventionAction> AfterToolCallAsync(AfterToolCallEvent value, CancellationToken cancellationToken = default) => new(callback(value));
    }

    private sealed class Model(params Message[] messages) : IModel
    {
        private readonly Queue<Message> _responses = new(messages);
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; Requests.Add(request); var message = _responses.Dequeue();
            yield return new ModelMessageStartEvent();
            for (var index = 0; index < message.Content.Count; index++)
            {
                if (message.Content[index] is ToolUseBlock call)
                {
                    yield return new ModelContentBlockStartEvent(index, call with { Input = Json("{}") });
                    yield return new ModelContentBlockDeltaEvent(index, ToolInput: call.Input.GetRawText());
                }
                else { yield return new ModelContentBlockStartEvent(index); yield return new ModelContentBlockDeltaEvent(index, Text: ((TextBlock)message.Content[index]).Text); }
                yield return new ModelContentBlockStopEvent(index);
            }
            yield return new ModelMetadataEvent(new(1, 1));
            yield return new ModelMessageStopEvent(message.Content.OfType<ToolUseBlock>().Any() ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
