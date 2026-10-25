using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Goals;
using Xunit;

namespace Strands.Agents.Tests.Goals;

public sealed class GoalLoopTests
{
    [Fact]
    public async Task InterruptedAttemptsAreNotJudgedUntilTheirApprovedToolBodyCompletes()
    {
        var model = new ApprovalResponses();
        var validations = 0;
        var effects = 0;
        var goal = new GoalLoop((_, _, _) => { validations++; return new ValueTask<ValidationOutcome>(new ValidationOutcome(true)); }, new() { MaxAttempts = 2 });
        var tool = new FunctionTool(new("approval", "Request approval.", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            context.Interrupt("approve");
            effects++;
            return new ValueTask<string>("approved");
        });
        await using var agent = new Agent(new() { Model = model, Tools = new[] { tool }, Plugins = new[] { goal } });

        var pause = await agent.InvokeAsync("the task");

        Assert.Equal(StopReasons.Interrupt, pause.StopReason);
        Assert.Equal(0, validations);
        Assert.Equal(0, effects);
        Assert.Null(goal.LastResult(agent));
        var result = await agent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(Assert.Single(pause.Interrupts).Id, JsonSerializer.SerializeToElement(true)) } });
        Assert.Equal("completed", result.ToString());
        Assert.Equal(1, validations);
        Assert.Equal(1, effects);
        Assert.Single(goal.LastResult(agent)!.Attempts);
    }

    [Fact]
    public async Task CancellationBeforeModelIoCannotStartValidation()
    {
        var model = new Responses("unused");
        var validations = 0;
        using var cancellation = new CancellationTokenSource();
        var goal = new GoalLoop((_, _, _) => { validations++; return new ValueTask<ValidationOutcome>(new ValidationOutcome(false)); }, new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { cancellation.Cancel(); return default; });

        var result = await agent.InvokeAsync("the task", cancellationToken: cancellation.Token);

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Empty(model.Requests);
        Assert.Equal(0, validations);
        Assert.Null(goal.LastResult(agent));
    }

    [Fact]
    public async Task EarlyModelStreamDisposalDoesNotJudgeAPartialResponse()
    {
        var model = new Responses("partial", "good");
        var validations = 0;
        var goal = new GoalLoop((_, _, _) => { validations++; return new ValueTask<ValidationOutcome>(new ValidationOutcome(true)); }, new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        await using (var iterator = agent.StreamAsync("the first task").GetAsyncEnumerator())
            while (await iterator.MoveNextAsync()) if (iterator.Current is ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent }) break;

        Assert.Equal(0, validations);
        Assert.Null(goal.LastResult(agent));
        Assert.Equal("good", (await agent.InvokeAsync("another task")).ToString());
        Assert.Equal(1, validations);
    }

    [Fact]
    public async Task ANewFailedInvocationCannotExposeAnEarlierSuccessfulGoalResult()
    {
        var failure = new IOException("next invocation failed");
        var model = new Responses("good", "unused") { BeforeResponse = (index, _) => index == 1 ? throw failure : Task.CompletedTask };
        var goal = new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(true)), new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        await agent.InvokeAsync("first task");
        var completed = Assert.IsType<GoalResult>(goal.LastResult(agent));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("next task")));

        Assert.Null(goal.LastResult(agent));
        Assert.True(completed.Passed);
        Assert.Single(completed.Attempts);
    }

    [Fact]
    public async Task StatefulContextResetAlsoRemovesEarlierAttemptsFromTheJudgeTranscript()
    {
        var main = new StatefulResponses("first draft", "corrected draft");
        var judge = new Judgments(new(false, "Correct it."), new(true));
        var goal = new GoalLoop("the goal", new() { MaxAttempts = 2, PreserveContext = false });
        await using var agent = new Agent(new() { Model = main, AuxModel = judge, Plugins = new[] { goal } });

        await agent.InvokeAsync("original task");

        var secondPrompt = judge.Requests[1].Messages[0].Text;
        Assert.Contains("original task", secondPrompt);
        Assert.Contains("corrected draft", secondPrompt);
        Assert.DoesNotContain("first draft", secondPrompt);
        Assert.Empty(main.Requests[1].ModelState!);
    }

    [Fact]
    public async Task AGoalWithoutAnAuxiliaryOverrideUsesTheBorrowedHostModel()
    {
        var model = new MainAndJudge();
        var goal = new GoalLoop("the goal", new() { MaxAttempts = 1 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });

        Assert.Equal("the response", (await agent.InvokeAsync("the task")).ToString());

        Assert.Equal(1, model.MainCalls);
        Assert.Equal(1, model.JudgeCalls);
        Assert.True(goal.LastResult(agent)!.Passed);
    }

    [Fact]
    public void JudgePromptsIncludeBoundedToolSummariesAndExcludeOpaqueReasoning()
    {
        var longText = new string('x', 700);
        var prompt = GoalJudge.BuildPrompt("the goal", new Message[]
        {
            Message.User("the task"),
            new(Role.Assistant, new ContentBlock[] { new ToolUseBlock("call", "work", JsonSerializer.SerializeToElement(new { code = longText })) }),
            new(Role.User, new ContentBlock[] { new ToolResultBlock("call", new ContentBlock[] { new TextBlock(longText), new JsonBlock(JsonSerializer.SerializeToElement(new { answer = 7 })) }, ToolResultStatus.Error) }),
            new(Role.Assistant, new ContentBlock[] { new ReasoningBlock("private thought"), new TextBlock("visible response") })
        });

        Assert.Contains("[tool-call: work] input=", prompt);
        Assert.Contains("[tool-result: error]", prompt);
        Assert.Contains("more chars]", prompt);
        Assert.Contains("visible response", prompt);
        Assert.DoesNotContain(longText, prompt);
        Assert.DoesNotContain("private thought", prompt);
    }

    [Fact]
    public async Task SharedGoalInstancesKeepConcurrentAgentResultsIndependent()
    {
        var goal = new GoalLoop((response, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(response.Text == "good")), new() { MaxAttempts = 2 });
        await using var first = new Agent(new() { Model = new Responses("bad", "good"), Plugins = new[] { goal } });
        await using var second = new Agent(new() { Model = new Responses("good"), Plugins = new[] { goal } });

        await Task.WhenAll(first.InvokeAsync("first task"), second.InvokeAsync("second task"));

        Assert.Equal(2, goal.LastResult(first)!.Attempts.Count);
        Assert.Single(goal.LastResult(second)!.Attempts);
        Assert.True(goal.LastResult(first)!.Passed);
        Assert.True(goal.LastResult(second)!.Passed);
    }

    [Fact]
    public async Task StatefulContextResetRestoresTheOriginalServerCursor()
    {
        var model = new StatefulResponses("bad", "good");
        var goal = new GoalLoop((response, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(response.Text == "good")), new() { MaxAttempts = 2, PreserveContext = false });
        await using var agent = new Agent(new()
        {
            Model = model,
            Plugins = new[] { goal },
            ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("baseline") }
        });

        await agent.InvokeAsync("original task");

        Assert.Equal("baseline", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Equal(new[] { Role.User, Role.User }, model.Requests[1].Messages.Select(message => message.Role));
        Assert.DoesNotContain(model.Requests[1].Messages, message => message.Text == "bad");
        Assert.Equal("response-1", agent.ModelState.Get<string>("responseId"));
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task ExplicitJudgeConfigurationWinsOverTheAuxiliaryModel()
    {
        var main = new Responses("response");
        var auxiliary = new Responses("unused");
        var judge = new Judgments(new ValidationOutcome(true));
        var goal = new GoalLoop("the goal", new() { MaxAttempts = 1, JudgeModel = judge, JudgeSystemPrompt = "custom evaluation instructions" });
        await using var agent = new Agent(new() { Model = main, AuxModel = auxiliary, Plugins = new[] { goal } });

        await agent.InvokeAsync("the task");

        Assert.Empty(auxiliary.Requests);
        Assert.Equal("custom evaluation instructions", Assert.Single(judge.Requests).SystemPrompt);
        Assert.Equal(0, judge.Disposals);
    }

    [Fact]
    public async Task CustomRetryInputReceivesTrimmedFeedbackAndPreservesTypedContent()
    {
        var model = new Responses("bad", "good");
        var goal = new GoalLoop((response, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(response.Text == "good", "  fix this  ")), new()
        {
            MaxAttempts = 2,
            ResumePromptTemplate = feedback => new InvocationContinuation(new ContentBlock[]
            {
                new JsonBlock(JsonSerializer.SerializeToElement(new { feedback })),
                new ImageBlock("image/png", "AQID")
            })
        });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });

        await agent.InvokeAsync("the task");

        var retry = model.Requests[1].Messages.Last();
        Assert.Equal("fix this", Assert.IsType<JsonBlock>(retry.Content[0]).Json.GetProperty("feedback").GetString());
        Assert.Equal("AQID", Assert.IsType<ImageBlock>(retry.Content[1]).Data);
    }

    [Fact]
    public async Task CancellationDuringValidationRetainsTheCallerTokenWithoutRetrying()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new Responses("response");
        using var cancellation = new CancellationTokenSource();
        var goal = new GoalLoop(async (_, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new ValidationOutcome(false);
        }, new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        var invocation = agent.InvokeAsync("the task", cancellationToken: cancellation.Token);
        Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(10_000)));

        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(model.Requests);
        Assert.Null(goal.LastResult(agent));
    }

    [Fact]
    public async Task ModelFailuresRetainTheirIdentityAndDoNotStartJudging()
    {
        var failure = new IOException("model failed");
        var validations = 0;
        var model = new Responses("unused") { BeforeResponse = (_, _) => throw failure };
        var goal = new GoalLoop((_, _, _) => { validations++; return new ValueTask<ValidationOutcome>(new ValidationOutcome(false)); }, new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("the task")));

        Assert.Equal(0, validations);
        Assert.Null(goal.LastResult(agent));
    }

    [Fact]
    public async Task DeniedInvocationsCannotStartValidationOrModelCalls()
    {
        var validations = 0;
        var model = new Responses("unused");
        var goal = new GoalLoop((_, _, _) => { validations++; return new ValueTask<ValidationOutcome>(new ValidationOutcome(false)); }, new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        agent.Hooks.Register<BeforeInvocationEvent>((value, _) => { value.CancelReason = "blocked"; return default; });

        Assert.Equal("blocked", (await agent.InvokeAsync("the task")).ToString());

        Assert.Equal(0, validations);
        Assert.Empty(model.Requests);
        Assert.Null(goal.LastResult(agent));
    }

    [Fact]
    public async Task DisposingAtTheAfterBoundaryCannotResumeOrMergeAnotherInvocation()
    {
        var model = new Responses("bad", "good");
        var goal = new GoalLoop((response, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(response.Text == "good")), new() { MaxAttempts = 3 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        await using (var iterator = agent.StreamAsync("first task").GetAsyncEnumerator())
            while (await iterator.MoveNextAsync()) if (iterator.Current is AfterInvocationEvent) break;

        Assert.Single(model.Requests);
        Assert.Equal("good", (await agent.InvokeAsync("independent task")).ToString());
        Assert.Single(goal.LastResult(agent)!.Attempts);
    }

    [Fact]
    public void UnboundedEvaluationWarnsOncePerPluginName()
    {
        var name = "goal-warning-" + Guid.NewGuid().ToString("N");
        using var listener = new WarningListener(name);
        Trace.Listeners.Add(listener);
        try
        {
            _ = new GoalLoop("the goal", new() { Name = name });
            _ = new GoalLoop("the goal", new() { Name = name });
            _ = new GoalLoop("bounded goal", new() { Name = name, MaxAttempts = 1 });
            Assert.Equal(1, listener.Warnings);
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task MissingValidatorOutcomesAreReportedAsFailedValidation()
    {
        var model = new Responses("first", "second");
        var validations = 0;
        var goal = new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(++validations == 1 ? null! : new ValidationOutcome(true)), new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });

        Assert.Equal("second", (await agent.InvokeAsync("the task")).ToString());

        Assert.Contains("Validator error:", goal.LastResult(agent)!.Attempts[0].Feedback!);
        Assert.Contains("no outcome", goal.LastResult(agent)!.Attempts[0].Feedback!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TimeoutBudgetsMustBePositive(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GoalLoop("the goal", new() { Timeout = TimeSpan.FromMilliseconds(milliseconds) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AttemptBudgetsMustBePositive(int attempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(true)),
            new() { MaxAttempts = attempts }));
    }

    [Fact]
    public async Task StatefulJudgingRetainsTheAuthoritativeResponseAfterAcknowledgedHistoryIsCleared()
    {
        var main = new StatefulResponses("authoritative visible response");
        var judge = new Judgments(new ValidationOutcome(true));
        var goal = new GoalLoop("Give the actual response.", new() { MaxAttempts = 1 });
        await using var agent = new Agent(new() { Model = main, AuxModel = judge, Plugins = new[] { goal } });

        await agent.InvokeAsync("original task");

        Assert.Empty(agent.Messages);
        var prompt = Assert.Single(judge.Requests).Messages[0].Text;
        Assert.Contains("original task", prompt);
        Assert.Contains("authoritative visible response", prompt);
        Assert.True(goal.LastResult(agent)!.Passed);
    }

    [Fact]
    public async Task ContextResetHidesEarlierAttemptsWithoutRollingBackApplicationState()
    {
        var model = new Responses("bad attempt", "good attempt");
        var goal = new GoalLoop((response, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(response.Text == "good attempt", "Correct the answer.")),
            new() { MaxAttempts = 2, PreserveContext = false });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal }, SystemPrompt = "original instructions" });
        agent.Hooks.Register<AfterModelCallEvent>((_, _) => { agent.State.Set("effects", agent.State.Get<int>("effects") + 1); return default; });

        var result = await agent.InvokeAsync("original task");

        Assert.Equal("good attempt", result.ToString());
        Assert.Equal(2, agent.State.Get<int>("effects"));
        Assert.Equal(new[] { Role.User, Role.User }, model.Requests[1].Messages.Select(message => message.Role));
        Assert.Equal("original task", model.Requests[1].Messages[0].Text);
        Assert.DoesNotContain(model.Requests[1].Messages, message => message.Text.Contains("bad attempt"));
        Assert.Equal("original instructions", model.Requests[1].SystemPrompt);
    }

    [Fact]
    public async Task NaturalLanguageGoalsUseFreshStructuredJudgesOnTheAuxiliaryModel()
    {
        var main = new Responses("first draft", "corrected draft");
        var judge = new Judgments(new(false, "Name the actual result."), new(true));
        var goal = new GoalLoop("Name the actual result.", new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = main, AuxModel = judge, Plugins = new[] { goal } });

        var result = await agent.InvokeAsync("Solve it.");

        Assert.Equal("corrected draft", result.ToString());
        Assert.Equal(2, main.Requests.Count);
        Assert.Equal(2, judge.Requests.Count);
        Assert.All(judge.Requests, request =>
        {
            Assert.Single(request.Messages);
            Assert.Contains("Goal:", request.Messages[0].Text);
            Assert.Contains("Name the actual result.", request.Messages[0].Text);
            Assert.Contains("Goal Evaluation", request.SystemPrompt!);
            Assert.Contains(request.Tools!, tool => tool.Name == StructuredOutputTool<ValidationOutcome>.ToolName);
        });
        Assert.Contains("first draft", judge.Requests[0].Messages[0].Text);
        Assert.Contains("corrected draft", judge.Requests[1].Messages[0].Text);
        Assert.True(goal.LastResult(agent)!.Passed);
        Assert.Equal(0, judge.Disposals);
    }

    [Fact]
    public async Task ADiscardedContinuationCannotMergeTheNextIndependentGoalRun()
    {
        var model = new Responses("first", "second");
        var goal = new GoalLoop((response, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(response.Text == "second")), new() { MaxAttempts = 3 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        agent.Hooks.Register<AfterInvocationEvent>((value, _) => { value.Resume = null; return default; });

        Assert.Equal("first", (await agent.InvokeAsync("first task")).ToString());
        Assert.Null(goal.LastResult(agent));
        Assert.Equal("second", (await agent.InvokeAsync("independent task")).ToString());

        Assert.Single(goal.LastResult(agent)!.Attempts);
    }

    [Fact]
    public async Task TwoGoalLoopsCannotCompeteForOneAgentsContinuation()
    {
        var first = new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(true)), new() { Name = "first-goal" });
        var second = new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(true)), new() { Name = "second-goal" });
        await using var agent = new Agent(new() { Model = new Responses("unused"), Plugins = new[] { first, second } });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InitializeAsync());

        Assert.Contains("one GoalLoop", error.Message);
    }

    [Fact]
    public async Task ValidatorFailureBecomesCorrectiveFeedbackForTheNextAttempt()
    {
        var model = new Responses("first", "second");
        var validations = 0;
        var goal = new GoalLoop((_, _, _) =>
        {
            if (++validations == 1) throw new IOException("judge failed");
            return new ValueTask<ValidationOutcome>(new ValidationOutcome(true));
        }, new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });

        var result = await agent.InvokeAsync("Solve it.");

        Assert.Equal("second", result.ToString());
        Assert.Contains("Validator error: judge failed", model.Requests[1].Messages.Last(message => message.Role == Role.User).Text);
        Assert.Equal("Validator error: judge failed", goal.LastResult(agent)!.Attempts[0].Feedback);
    }

    [Fact]
    public async Task ElapsedTimeoutStopsBeforeInvokingTheValidator()
    {
        var model = new Responses("late response", "unexpected retry") { BeforeResponse = (_, token) => Task.Delay(50, token) };
        var validations = 0;
        var goal = new GoalLoop((_, _, _) =>
        {
            validations++;
            return new ValueTask<ValidationOutcome>(new ValidationOutcome(false));
        }, new() { MaxAttempts = 2, Timeout = TimeSpan.FromMilliseconds(10) });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });

        await agent.InvokeAsync("Solve it.");

        Assert.Equal(0, validations);
        Assert.Single(model.Requests);
        var summary = Assert.IsType<GoalResult>(goal.LastResult(agent));
        Assert.False(summary.Passed);
        Assert.Equal(GoalStopReason.Timeout, summary.StopReason);
        Assert.Empty(summary.Attempts);
    }

    [Fact]
    public async Task RejectedAttemptsStopAtTheConfiguredAttemptBudget()
    {
        var model = new Responses("first", "second", "unexpected third", "unexpected fourth");
        var goal = new GoalLoop((_, _, _) => new ValueTask<ValidationOutcome>(new ValidationOutcome(false, "Try again.")), new() { MaxAttempts = 2 });
        await using var agent = new Agent(new() { Model = model, MaxTurns = 4, Plugins = new[] { goal } });

        var result = await agent.InvokeAsync("Solve it.");

        Assert.Equal("second", result.ToString());
        Assert.Equal(2, model.Requests.Count);
        var summary = Assert.IsType<GoalResult>(goal.LastResult(agent));
        Assert.False(summary.Passed);
        Assert.Equal(GoalStopReason.MaxAttempts, summary.StopReason);
        Assert.Equal(2, summary.Attempts.Count);
    }

    [Fact]
    public async Task FailedValidationResumesWithFeedbackUntilTheGoalIsSatisfied()
    {
        var model = new Responses("incorrect response", "correct response");
        var goal = new GoalLoop((response, _, _) => new ValueTask<ValidationOutcome>(
            new ValidationOutcome(response.Text == "correct response", response.Text == "correct response" ? null : "Fix the calculation.")), new() { MaxAttempts = 3 });
        await using var agent = new Agent(new() { Model = model, Plugins = new[] { goal } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await agent.InvokeAsync("Solve it.", cancellationToken: deadline.Token);

        Assert.Equal("correct response", result.ToString());
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("Fix the calculation.", model.Requests[1].Messages.Last(message => message.Role == Role.User).Text);
        var summary = Assert.IsType<GoalResult>(goal.LastResult(agent));
        Assert.True(summary.Passed);
        Assert.Equal(GoalStopReason.Satisfied, summary.StopReason);
        Assert.Equal(new[] { false, true }, summary.Attempts.Select(attempt => attempt.Passed));
        Assert.Equal(new[] { 1, 2 }, summary.Attempts.Select(attempt => attempt.Attempt));
    }

    private class Responses(params string[] text) : IModel
    {
        internal List<ModelRequest> Requests { get; } = new();
        internal Func<int, CancellationToken, Task>? BeforeResponse { get; init; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(request);
            if (BeforeResponse is { } before) await before(index, cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: text[index]);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMetadataEvent(new Usage(2, 3));
            yield return new ModelMessageStopEvent(StopReasons.EndTurn)
            {
                ModelState = this is IStatefulModel { Stateful: true }
                    ? new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("response-" + index) } : null
            };
        }
    }

    private sealed class StatefulResponses(params string[] text) : Responses(text), IStatefulModel
    {
        public bool Stateful => true;
    }

    private sealed class Judgments(params ValidationOutcome[] outcomes) : IModel, IAsyncDisposable
    {
        internal List<ModelRequest> Requests { get; } = new();
        internal int Disposals { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Requests.Count;
            Requests.Add(request);
            var answer = new Dictionary<string, object?> { ["passed"] = outcomes[index].Passed };
            if (outcomes[index].Feedback is { } feedback) answer.Add("feedback", feedback);
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("judge-" + index, StructuredOutputTool<ValidationOutcome>.ToolName, JsonSerializer.SerializeToElement(answer)));
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMetadataEvent(new Usage(2, 3));
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }

        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class WarningListener(string name) : TraceListener
    {
        internal int Warnings { get; private set; }
        public override void Write(string? message) { if (message?.IndexOf(name, StringComparison.Ordinal) >= 0) Warnings++; }
        public override void WriteLine(string? message) => Write(message);
    }

    private sealed class MainAndJudge : IModel
    {
        private readonly Responses _main = new("the response");
        private readonly Judgments _judge = new(new ValidationOutcome(true));
        internal int MainCalls => _main.Requests.Count;
        internal int JudgeCalls => _judge.Requests.Count;
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            request.Tools?.Any(tool => tool.Name == StructuredOutputTool<ValidationOutcome>.ToolName) == true
                ? _judge.StreamAsync(request, cancellationToken) : _main.StreamAsync(request, cancellationToken);
    }

    private sealed class ApprovalResponses : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var approval = _calls++ == 0;
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            if (approval) yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("approval-call", "approval", JsonSerializer.SerializeToElement(new { })));
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "completed");
            }
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(approval ? StopReasons.ToolUse : StopReasons.EndTurn);
        }
    }
}
