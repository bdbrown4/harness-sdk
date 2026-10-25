using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests;

public sealed class AgentInterruptTests
{
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static FunctionTool Tool(string name, Func<ToolContext, string> body) => new(new(name, name, Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(body(context)));

    [Fact]
    public void AgentPendingBatchesRejectOrchestratorOnlyApprovals()
    {
        var assistant = new Message(Role.Assistant, [new ToolUseBlock("call", "tool", Json("{}"))]);
        var question = new Interrupt("question", "approval", "call", source: InterruptSource.OrchestratorHook);
        Assert.Throws<ArgumentException>(() => new InterruptState(assistant, [], [question], []));
    }

    [Theory]
    [InlineData(ToolExecutionStrategy.Sequential)]
    [InlineData(ToolExecutionStrategy.Concurrent)]
    public async Task ResumeSkipsTheModelAndEveryCompletedSideEffect(ToolExecutionStrategy strategy)
    {
        var effects = 0;
        var approved = 0;
        var model = new Model("done", "approve");
        await using var agent = new Agent(new() { Model = model, ToolExecution = strategy, Tools = [Tool("done", _ => { effects++; return "committed"; }), Tool("approve", context => { Assert.True(context.Interrupt("approval").GetBoolean()); approved++; return "approved"; })] });
        var state = new Dictionary<string, object?> { ["caller"] = 42 };
        var paused = await agent.InvokeAsync("task", new() { InvocationState = state });
        Assert.Equal("interrupt", paused.StopReason);
        var question = Assert.Single(paused.Interrupts);
        Assert.Equal(InterruptSource.Tool, question.Source);
        Assert.Equal(1, effects); Assert.Equal(0, approved); Assert.Equal(1, model.Calls);
        Assert.Equal(Role.Assistant, agent.Messages.Last().Role);
        Assert.Equal("committed", Assert.Single(agent.InterruptState!.CompletedToolResults).Content);
        var result = await agent.InvokeAsync(null, new() { InvocationState = state, InterruptResponses = [new(question.Id, Json("true"))] });
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(1, effects); Assert.Equal(1, approved); Assert.Equal(2, model.Calls);
        Assert.Null(agent.InterruptState); Assert.Same(state, result.InvocationState); Assert.Single(state);
        Assert.Equal(new[] { "call-0", "call-1" }, agent.Messages[2].Content.OfType<ToolResultBlock>().Select(value => value.ToolUseId));
    }

    [Fact]
    public async Task PartialResponsesRetainOtherQuestionsAndExplicitJsonNullIsAnAnswer()
    {
        var accepted = new List<string>();
        var model = new Model("first", "second");
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("first", context => { Assert.Equal(JsonValueKind.Null, context.Interrupt("first").ValueKind); accepted.Add("first"); return "one"; }), Tool("second", context => { Assert.Equal("yes", context.Interrupt("second").GetString()); accepted.Add("second"); return "two"; })] });
        var initial = await agent.InvokeAsync("task");
        Assert.Equal(2, initial.Interrupts.Count);
        var one = initial.Interrupts.Single(value => value.Name == "first");
        var partial = await agent.InvokeAsync(null, new() { InterruptResponses = [new(one.Id, Json("null"))] });
        Assert.Equal("interrupt", partial.StopReason); Assert.Equal("second", Assert.Single(partial.Interrupts).Name);
        Assert.NotNull(partial.Message);
        Assert.Equal(new[] { "first" }, accepted); Assert.Equal(1, model.Calls);
        var final = await agent.InvokeAsync(null, new() { InterruptResponses = [new(partial.Interrupts[0].Id, Json("\"yes\""))] });
        Assert.Equal(StopReasons.EndTurn, final.StopReason); Assert.Equal(new[] { "first", "second" }, accepted); Assert.Equal(2, model.Calls);
    }

    [Fact]
    public async Task HookAndMiddlewareInterruptsReplayOnlyTheirIncompleteCall()
    {
        var effects = 0;
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => { Assert.True(value.Interrupt("hook").GetBoolean()); return default; });
        var middleware = new MiddlewareRegistry();
        middleware.RegisterInput(MiddlewareStages.ExecuteTool, (value, _) => { Assert.True(value.Interrupt("middleware").Response.GetBoolean()); return new(value); });
        await using var agent = new Agent(new() { Model = new Model("approve"), Hooks = hooks, Middleware = middleware, Tools = [Tool("approve", _ => { effects++; return "done"; })] });
        var first = await agent.InvokeAsync("task");
        Assert.Equal(InterruptSource.Hook, Assert.Single(first.Interrupts).Source); Assert.Equal(0, effects);
        var second = await agent.InvokeAsync(null, new() { InterruptResponses = [new(first.Interrupts[0].Id, Json("true"))] });
        Assert.Equal(InterruptSource.Middleware, Assert.Single(second.Interrupts).Source); Assert.Equal(0, effects);
        var final = await agent.InvokeAsync(null, new() { InterruptResponses = [new(second.Interrupts[0].Id, Json("true"))] });
        Assert.Equal(StopReasons.EndTurn, final.StopReason); Assert.Equal(1, effects);
    }

    [Fact]
    public async Task InvalidResponsesAndNewPromptsLeaveThePausedBatchUnchanged()
    {
        var model = new Model("approve");
        var afterInvocations = 0;
        var hooks = new HookRegistry();
        hooks.Register<AfterInvocationEvent>((_, _) => { afterInvocations++; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = [Tool("approve", context => context.Interrupt("approval").ToString())] });
        var paused = await agent.InvokeAsync("task");
        var before = JsonSerializer.Serialize(agent.InterruptState);
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync("new task"));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeContentAsync([new TextBlock("new task")]));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync(null, new() { InterruptResponses = [new("unknown", Json("true"))] }));
        var response = new InterruptResponse(paused.Interrupts[0].Id, Json("true"));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync(null, new() { InterruptResponses = [response, response] }));
        Assert.Equal(before, JsonSerializer.Serialize(agent.InterruptState)); Assert.Equal(1, model.Calls); Assert.Equal(2, agent.Messages.Count);
        Assert.Equal(1, afterInvocations);
    }

    [Fact]
    public async Task DisposingAtACompletedSiblingRetainsItsEffectAndOutstandingApproval()
    {
        var effects = 0;
        await using var agent = new Agent(new() { Model = new Model("done", "approve"), Tools = [Tool("done", _ => { effects++; return "committed"; }), Tool("approve", context => context.Interrupt("approval").ToString())] });
        await using (var iterator = agent.StreamAsync("task").GetAsyncEnumerator())
            while (await iterator.MoveNextAsync())
                if (iterator.Current is ToolResultEvent) break;
        Assert.Equal(1, effects);
        var pending = agent.InterruptState!;
        Assert.NotNull(pending); Assert.Equal("committed", Assert.Single(pending.CompletedToolResults).Content);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(pending.Interrupts).Id, Json("true"))] });
        Assert.Equal(StopReasons.EndTurn, result.StopReason); Assert.Equal(1, effects);
    }

    [Fact]
    public async Task StructuredSuccessCompletedBeforeAPauseCanBeRecoveredWithoutRepeatingOtherTools()
    {
        var effects = 0;
        var model = new Model(StructuredOutputTool<Answer>.ToolName, "approve");
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("approve", context => { context.Interrupt("approval"); effects++; return "done"; })] });
        var options = new StructuredOutputOptions<Answer> { Schema = Json("{\"type\":\"object\"}"), Deserializer = _ => new(42) };
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("task", options));
        var pending = agent.InterruptState!;
        Assert.Equal(StructuredOutputTool<Answer>.ToolName, Assert.Single(pending.CompletedToolResults).ToolUseId == "call-0" ? pending.AssistantMessage.Content.OfType<ToolUseBlock>().First().Name : "wrong");
        var result = await agent.InvokeStructuredAsync(null, options, new() { InterruptResponses = [new(Assert.Single(pending.Interrupts).Id, Json("true"))] });
        Assert.Equal(42, result.Output.Value); Assert.Equal(1, effects); Assert.Equal(1, model.Calls);
    }

    private sealed record Answer(int Value);

    [Fact]
    public async Task MiddlewareApprovalAfterNextNeverRepeatsTheCompletedBody()
    {
        var effects = 0;
        var middleware = new MiddlewareRegistry();
        middleware.RegisterWrap(MiddlewareStages.ExecuteTool, ApproveAfterExecution);
        await using var agent = new Agent(new() { Model = new Model("effect"), Middleware = middleware, Tools = [Tool("effect", _ => { effects++; return "actual output"; })] });
        var paused = await agent.InvokeAsync("task");
        Assert.Equal("interrupt", paused.StopReason); Assert.Equal(1, effects);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, Json("true"))] });
        Assert.Equal(StopReasons.EndTurn, result.StopReason); Assert.Equal(1, effects);
        Assert.Equal("actual output", Assert.Single(agent.Messages[2].Content.OfType<ToolResultBlock>()).Content);
        async IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> ApproveAfterExecution(ExecuteToolContext context, MiddlewareNext<ExecuteToolContext, ExecuteToolResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in next(context))
            {
                if (item is MiddlewareResultEvent<ExecuteToolResult>) Assert.True(context.Interrupt("release output").Response.GetBoolean());
                yield return item;
            }
        }
    }

    [Fact]
    public async Task InterruptionDoesNotHideAMiddlewareCleanupFailure()
    {
        var cleanup = new IOException("dispose failed");
        var middleware = new MiddlewareRegistry();
        middleware.RegisterWrap(MiddlewareStages.ExecuteTool, (context, next, _) => new FailingDisposal(next(context), cleanup));
        await using var agent = new Agent(new() { Model = new Model("approve"), Middleware = middleware, Tools = [Tool("approve", context => context.Interrupt("approval").ToString())] });
        var failure = await Assert.ThrowsAsync<InterruptException>(() => agent.InvokeAsync("task"));
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(failure.Data["Strands.CleanupErrors"]));
        Assert.NotNull(agent.InterruptState); Assert.Equal(Role.Assistant, agent.Messages.Last().Role);
    }

    private sealed class FailingDisposal(IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>> source, Exception cleanup) : IAsyncEnumerable<IMiddlewareEvent<ExecuteToolResult>>
    {
        public IAsyncEnumerator<IMiddlewareEvent<ExecuteToolResult>> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Iterator(source.GetAsyncEnumerator(cancellationToken), cleanup);
        private sealed class Iterator(IAsyncEnumerator<IMiddlewareEvent<ExecuteToolResult>> inner, Exception cleanup) : IAsyncEnumerator<IMiddlewareEvent<ExecuteToolResult>>
        {
            public IMiddlewareEvent<ExecuteToolResult> Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); throw cleanup; }
        }
    }

    [Fact]
    public async Task AllBeforeToolCallbacksCanRequestIndependentApprovalsInOnePause()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => { value.Interrupt("first"); return default; });
        hooks.Register<BeforeToolCallEvent>((value, _) => { value.Interrupt("second"); return default; });
        await using var agent = new Agent(new() { Model = new Model("effect"), Hooks = hooks, Tools = [Tool("effect", _ => "done")] });
        var paused = await agent.InvokeAsync("task");
        Assert.Equal(new[] { "first", "second" }, paused.Interrupts.Select(value => value.Name));
        var completed = await agent.InvokeAsync(null, new() { InterruptResponses = paused.Interrupts.Select(value => new InterruptResponse(value.Id, Json("true"))).ToArray() });
        Assert.Equal(StopReasons.EndTurn, completed.StopReason);
    }

    [Fact]
    public async Task HookTransformsCannotChangeTheOriginalInterruptCorrelation()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => { value.ToolUse = value.ToolUse with { ToolUseId = "forged" }; return default; });
        hooks.Register<BeforeToolCallEvent>((value, _) => { value.Interrupt("approval"); return default; });
        await using var agent = new Agent(new() { Model = new Model("effect"), Hooks = hooks, Tools = [Tool("effect", _ => "done")] });
        var paused = await agent.InvokeAsync("task");
        Assert.Equal("call-0", Assert.Single(paused.Interrupts).ToolUseId);
    }

    [Fact]
    public async Task DuplicateInterruptNamesAcrossTheSameHookBoundaryAreRejected()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((value, _) => { value.Interrupt("same"); return default; });
        hooks.Register<BeforeToolCallEvent>((value, _) => { value.Interrupt("same"); return default; });
        await using var agent = new Agent(new() { Model = new Model("effect"), Hooks = hooks, Tools = [Tool("effect", _ => "done")] });
        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync("task"));
        Assert.Null(agent.InterruptState);
    }

    [Fact]
    public async Task BeforeToolsApprovalGatesTheWholeBatchAndRetainsItsScopeOnResume()
    {
        var effects = 0;
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolsEvent>((value, _) => { Assert.True(value.Interrupt("batch").GetBoolean()); return default; });
        await using var agent = new Agent(new() { Model = new Model("one", "two"), Hooks = hooks, Tools = [Tool("one", _ => { effects++; return "one"; }), Tool("two", _ => { effects++; return "two"; })] });
        var paused = await agent.InvokeAsync("task");
        var question = Assert.Single(paused.Interrupts); Assert.True(question.IsBatch); Assert.Equal(0, effects);
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] });
        Assert.Equal(StopReasons.EndTurn, result.StopReason); Assert.Equal(2, effects);
    }

    [Fact]
    public async Task PreemptiveResponsesSupportExplicitNullAndMiddlewareUsesAResponseWrapper()
    {
        var middleware = new MiddlewareRegistry();
        middleware.RegisterInput(MiddlewareStages.ExecuteTool, (context, _) => { Assert.Equal(JsonValueKind.Null, context.Interrupt("middleware", response: Json("null")).Response.ValueKind); return new(context); });
        var hooks = new HookRegistry();
        hooks.Register<BeforeToolCallEvent>((context, _) => { Assert.True(context.Interrupt("hook", response: Json("true")).GetBoolean()); return default; });
        await using var agent = new Agent(new() { Model = new Model("effect"), Hooks = hooks, Middleware = middleware, Tools = [Tool("effect", context => { Assert.True(context.Interrupt("tool", response: Json("true")).GetBoolean()); return "done"; })] });
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync("task")).StopReason);
        Assert.Null(agent.InterruptState);
    }

    [Fact]
    public async Task CancellationRetainsApprovalAndSettledSiblingResultsForResume()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var waiting = new FunctionTool(new("waiting", "waiting", Json("{\"type\":\"object\"}")), async (_, _, token) =>
        {
            executions++; started.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); return "unreachable";
        });
        await using var agent = new Agent(new() { Model = new Model("approve", "waiting"), Tools = [Tool("approve", context => context.Interrupt("approval").ToString()), waiting] });
        var invocation = agent.InvokeAsync("task", cancellationToken: cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        var pending = agent.InterruptState!;
        Assert.NotNull(pending); Assert.Equal(ToolResultStatus.Error, Assert.Single(pending.CompletedToolResults).Status);
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(pending.Interrupts).Id, Json("true"))] })).StopReason);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task StatefulPauseRetainsAcknowledgedAssistantAndSendsOnlyPendingOutputsAfterResume()
    {
        var model = new StatefulModel();
        await using var agent = new Agent(new() { Model = model, Tools = [Tool("approve", context => context.Interrupt("approval").ToString())] });
        var paused = await agent.InvokeAsync("task");
        Assert.Equal("interrupt", paused.StopReason); Assert.Equal(2, agent.Messages.Count); Assert.Equal("server-1", agent.ModelState.Get<string>("id"));
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(Assert.Single(paused.Interrupts).Id, Json("true"))] });
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        var sent = Assert.Single(model.Requests[1].Messages);
        Assert.Equal("call-0", Assert.Single(sent.Content.OfType<ToolResultBlock>()).ToolUseId);
        Assert.Equal("server-1", model.Requests[1].ModelState!["id"].GetString());
        Assert.Empty(agent.Messages); Assert.Null(agent.InterruptState);
    }

    private sealed class StatefulModel : IStatefulModel
    {
        private readonly Model _inner = new("approve");
        public bool Stateful => true;
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await foreach (var item in _inner.StreamAsync(request, cancellationToken))
                yield return item is ModelMessageStopEvent stop ? stop with { ModelState = new Dictionary<string, JsonElement> { ["id"] = Json("\"server-" + Requests.Count + "\"") } } : item;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedResponseIsDurableBeforeAResumeLifecycleFailure(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-accepted-answer-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var hooks = new HookRegistry();
        var failure = new IOException("resume callback failed");
        try
        {
            var store = new FileSessionStore(directory);
            await using var agent = new Agent(new() { Model = new Model("approve"), Tools = [Tool("approve", context => context.Interrupt("approval").ToString())], Hooks = hooks, SessionId = "conversation", SessionStore = store });
            var question = Assert.Single((await agent.InvokeAsync("task")).Interrupts);
            hooks.Register<BeforeInvocationEvent>((_, _) => { if (cancel) { cancellation.Cancel(); return default; } throw failure; });
            var invocation = agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, Json("true"))] }, cancellation.Token);
            if (cancel) Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
            else Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => invocation));
            var saved = await store.LoadAsync("conversation");
            Assert.True(Assert.Single(saved!.InterruptState!.Responses).Response.GetBoolean());
            Assert.Equal(question.Id, saved.InterruptState.Responses[0].InterruptId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OldToolContextsCannotTargetANewBatchWithRepeatedMessageAndCallIdentifiers()
    {
        ToolContext? captured = null;
        var rejected = false;
        var middleware = new MiddlewareRegistry();
        middleware.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => new(result with { Message = new(result.Message.Role, result.Message.Content, "reused") }));
        await using var agent = new Agent(new()
        {
            Model = new Model("effect") { AlternateToolTurns = true },
            Middleware = middleware,
            Tools = [Tool("effect", context =>
        {
            if (captured is null) captured = context;
            else try { captured.Interrupt("stale", response: Json("true")); } catch (InvalidOperationException) { rejected = true; }
            return "done";
        })]
        });
        await agent.InvokeAsync("first");
        await agent.InvokeAsync("second");
        Assert.True(rejected);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("drop")]
    [InlineData("add")]
    public async Task RealStatefulModelCallsCannotBeRewrittenBeforeServerHistoryAcknowledgement(string mutation)
    {
        var middleware = new MiddlewareRegistry();
        middleware.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) =>
        {
            if (result.StopReason != StopReasons.ToolUse) return new(result);
            var original = result.Message.Content.OfType<ToolUseBlock>().Single();
            return new(result with
            {
                Message = mutation switch
                {
                    "replace" => new(Role.Assistant, [original with { ToolUseId = "forged" }]),
                    "drop" => new(Role.Assistant, [new TextBlock("filtered")]),
                    _ => new(Role.Assistant, [original, original with { ToolUseId = "extra" }])
                },
                StopReason = mutation == "drop" ? StopReasons.EndTurn : result.StopReason
            });
        });
        var effects = 0;
        await using var agent = new Agent(new() { Model = new StatefulModel(), Middleware = middleware, Tools = [Tool("approve", _ => { effects++; return "done"; })] });
        agent.ModelState.Set("id", "before");
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("task"));
        Assert.Equal(0, effects); Assert.Equal("before", agent.ModelState.Get<string>("id"));
        Assert.Equal("task", Assert.Single(agent.Messages).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadedPauseRejectsMissingQuestionsOrAFullyCompletedBatch(bool fullyCompleted)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-invalid-pause-" + Guid.NewGuid().ToString("N"));
        var assistant = new Message(Role.Assistant, [new ToolUseBlock("call", "tool", Json("{}"))]);
        var pending = new InterruptState(assistant, fullyCompleted ? [new("call", "done")] : [], fullyCompleted ? [new("question", "approval", "call")] : [], []);
        try
        {
            var store = new FileSessionStore(directory);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("conversation", new(1, [assistant]) { InterruptState = pending }).AsTask());
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedResumedDecoderStillCommitsACompletedSiblingEffect()
    {
        var effects = 0;
        var options = new StructuredOutputOptions<Answer> { Schema = Json("{\"type\":\"object\"}"), Deserializer = _ => new(42) };
        await using var agent = new Agent(new() { Model = new Model(StructuredOutputTool<Answer>.ToolName, "approve"), Tools = [Tool("approve", context => { context.Interrupt("approval"); effects++; return "committed"; })] });
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync("task", options));
        var question = Assert.Single(agent.InterruptState!.Interrupts);
        var invalidDecoder = options with { Deserializer = _ => throw new FormatException("changed decoder rejects accepted input") };
        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeStructuredAsync(null, invalidDecoder, new() { InterruptResponses = [new(question.Id, Json("true"))] }));
        Assert.Equal(1, effects);
        Assert.Null(agent.InterruptState);
        var outputs = agent.Messages.Last().Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(2, outputs.Length); Assert.Equal("committed", outputs[1].Content);
        await agent.InvokeAsync(null);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task OrdinaryFileSessionRestoresCompletedResultsAndOriginalQuestionIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-interrupt-" + Guid.NewGuid().ToString("N"));
        var effects = 0;
        var model = new Model("done", "approve");
        var tools = new[] { Tool("done", _ => { effects++; return "committed"; }), Tool("approve", context => context.Interrupt("approval").ToString()) };
        try
        {
            var store = new FileSessionStore(directory);
            string id;
            await using (var original = new Agent(new() { Model = model, Tools = tools, SessionId = "conversation", SessionStore = store }))
                id = Assert.Single((await original.InvokeAsync("task")).Interrupts).Id;
            await using var restored = new Agent(new() { Model = model, Tools = tools, SessionId = "conversation", SessionStore = store });
            await restored.InitializeAsync();
            Assert.Equal(id, Assert.Single(restored.InterruptState!.Interrupts).Id);
            Assert.Equal(StopReasons.EndTurn, (await restored.InvokeAsync(null, new() { InterruptResponses = [new(id, Json("true"))] })).StopReason);
            Assert.Equal(1, effects); Assert.Equal(2, model.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void PendingStateDetachesAndValidatesJsonAndCorrelationBeforeSerialization()
    {
        using var document = JsonDocument.Parse("{\"nested\":[1]}");
        var call = new ToolUseBlock("call", "tool", document.RootElement) { ProviderMetadata = document.RootElement };
        var message = new Message(Role.Assistant, [call]);
        var questions = new[] { new Interrupt("id", "name", "call", document.RootElement, InterruptSource.Tool) };
        var responses = new[] { new InterruptResponse("id", document.RootElement) };
        var value = new InterruptState(message, [], questions, responses);
        questions[0] = new("other", "other", "call");
        document.Dispose();
        var restored = JsonSerializer.Deserialize<InterruptState>(JsonSerializer.Serialize(value))!;
        Assert.Equal("id", restored.Interrupts[0].Id); Assert.Equal(1, restored.Responses[0].Response.GetProperty("nested")[0].GetInt32());
        Assert.Equal(1, restored.AssistantMessage.Content.OfType<ToolUseBlock>().Single().ProviderMetadata!.Value.GetProperty("nested")[0].GetInt32());
        Assert.Throws<ArgumentException>(() => new InterruptState(message, [new("unrelated", "result")], [], []));
        Assert.Throws<ArgumentException>(() => new InterruptResponse("id", default));
    }

    private sealed class Model(params string[] tools) : IModel
    {
        public int Calls { get; private set; }
        public bool AlternateToolTurns { get; init; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            Calls++;
            yield return new ModelMessageStartEvent();
            if (Calls == 1 || AlternateToolTurns && Calls % 2 == 1)
            {
                for (var index = 0; index < tools.Length; index++)
                {
                    yield return new ModelContentBlockStartEvent(index, new ToolUseBlock("call-" + index, tools[index], Json("{}")));
                    yield return new ModelContentBlockDeltaEvent(index, ToolInput: "{}");
                    yield return new ModelContentBlockStopEvent(index);
                }
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "finished"); yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
        }
    }
}
