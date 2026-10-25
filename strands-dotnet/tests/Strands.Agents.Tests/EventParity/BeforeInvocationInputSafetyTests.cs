using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Strands.Agents.Models;

namespace Strands.Agents.Tests.EventParity;

public sealed class BeforeInvocationInputSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedPausedAnswersAreNotRevalidatedAsNewAnswers(bool explicitlyEmpty)
    {
        var effects = 0; var beforeCalls = 0; var hooks = new HookRegistry(); var store = new IncomingInputStore();
        var model = new EventTestModel(ApprovalTurn(), EventTestModel.Text("finished"));
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            if (++beforeCalls == 2 && explicitlyEmpty) value.Messages = Array.Empty<Message>();
            return default;
        });
        var tool = ApprovalTool(() => effects++);
        await using var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks, SessionStore = store, SessionId = "paused" });
        var paused = await agent.InvokeAsync("task");
        var question = Assert.Single(paused.Interrupts);
        Assert.Equal(0, effects);

        var result = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] });

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(1, effects); Assert.Equal(2, model.Requests.Count);
        Assert.Null(agent.InterruptState);
        Assert.Equal("approved", Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[1].Messages[2].Content)).Content);
    }

    [Fact]
    public async Task RejectedHookInputPreservesAlreadyAcceptedAnswerForTheNextResume()
    {
        var effects = 0; var beforeCalls = 0; var hooks = new HookRegistry(); var store = new IncomingInputStore();
        var model = new EventTestModel(ApprovalTurn(), EventTestModel.Text("finished"));
        using var rejection = hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            if (++beforeCalls == 2) value.Messages = new[] { Message.User("injected") };
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Tools = [ApprovalTool(() => effects++)], Hooks = hooks, SessionStore = store, SessionId = "paused" });
        var paused = await agent.InvokeAsync("task"); var question = Assert.Single(paused.Interrupts);

        await Assert.ThrowsAsync<ArgumentException>(() => agent.InvokeAsync(null,
            new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] }));

        Assert.Equal(0, effects); Assert.Single(model.Requests);
        Assert.Equal(question.Id, Assert.Single(agent.InterruptState!.Responses).InterruptId);
        Assert.True(Assert.Single(store.Snapshot!.InterruptState!.Responses).Response.GetBoolean());
        Assert.DoesNotContain(agent.Messages, message => message.Text == "injected");
        rejection.Dispose();
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync(null)).StopReason);
        Assert.Equal(1, effects); Assert.Equal(2, model.Requests.Count);
        Assert.Null(agent.InterruptState);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly, false)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, false)]
    [InlineData(ExplicitCancellationTiming.NativeEarly, true)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool, true)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool, true)]
    public async Task ReplacementRetainsExistingExplicitCancellationAndDenialPrecedence(ExplicitCancellationTiming timing, bool denied)
    {
        var hooks = new HookRegistry(); var model = new EventTestModel(EventTestModel.Text("not accepted"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, ExplicitCancellationTiming = timing });
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            value.Messages = denied ? new Message[] { null! } : new[] { Message.User("replacement") };
            if (denied) value.CancelReason = "denied";
            agent.Cancel(); return default;
        });

        var result = await agent.InvokeAsync("original");

        Assert.Equal(denied && timing != ExplicitCancellationTiming.NativeEarly ? StopReasons.EndTurn : StopReasons.Cancelled, result.StopReason);
        var acceptedInput = !denied && timing == ExplicitCancellationTiming.ModelOrTool;
        Assert.Equal(acceptedInput ? 1 : 0, agent.Messages.Count(message => message.Role == Role.User));
        Assert.Equal(acceptedInput ? 1 : 0, model.Requests.Count);
        if (acceptedInput) Assert.Equal("replacement", Assert.Single(agent.Messages).Text);
        if (denied && timing != ExplicitCancellationTiming.NativeEarly) Assert.Equal("denied", result.ToString());
        Assert.Equal(CancellationToken.None, agent.CancelSignal);
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task CallerCancellationInterruptsAndPhysicallyJoinsTheHookBeforeAnyInputCommit(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false; CancellationToken? thrownToken = null; var hooks = new HookRegistry();
        var model = new EventTestModel(EventTestModel.Text("unused"));
        hooks.Register<BeforeInvocationEvent>(async (value, token) =>
        {
            Assert.Equal(caller.Token, token); value.Messages = new[] { Message.User("replacement") }; entered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException error) { thrownToken = error.CancellationToken; throw; }
            finally { exited = true; }
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, ExplicitCancellationTiming = timing });
        var operation = agent.InvokeAsync("original", cancellationToken: caller.Token);
        try
        {
            var winner = await Task.WhenAny(entered.Task, operation, Task.Delay(TimeSpan.FromSeconds(10)));
            if (ReferenceEquals(winner, operation)) await operation;
            Assert.Same(entered.Task, winner);
            caller.Cancel();
            Assert.Equal(StopReasons.Cancelled, (await operation).StopReason);
            Assert.True(exited); Assert.Equal(caller.Token, thrownToken);
            Assert.Empty(agent.Messages); Assert.Empty(model.Requests);
        }
        finally { caller.Cancel(); await operation; }
    }

    [Theory]
    [InlineData(ExplicitCancellationTiming.NativeEarly)]
    [InlineData(ExplicitCancellationTiming.BeforeInputAndModelOrTool)]
    [InlineData(ExplicitCancellationTiming.ModelOrTool)]
    public async Task ARequestedCallerTokenWinsOverDenialAndAnUnusedMalformedReplacement(ExplicitCancellationTiming timing)
    {
        using var caller = new CancellationTokenSource(); var hooks = new HookRegistry();
        var model = new EventTestModel(EventTestModel.Text("unused"));
        hooks.Register<BeforeInvocationEvent>((value, token) =>
        {
            Assert.Equal(caller.Token, token); value.Messages = new Message[] { null! }; value.CancelReason = "denied";
            caller.Cancel(); return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, ExplicitCancellationTiming = timing });

        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("original", cancellationToken: caller.Token)).StopReason);

        Assert.Empty(agent.Messages); Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("late-null")]
    [InlineData("orphan")]
    [InlineData("mismatch")]
    public async Task AnInvalidLaterBatchEntryCannotPartiallyAppendOrOverwriteTheRestoredSession(string invalid)
    {
        var history = new[] { Message.User("old"), Message.Assistant("saved") };
        var store = new IncomingInputStore { Snapshot = new(1, history) };
        var hooks = new HookRegistry(); var observed = 0; var model = new EventTestModel(EventTestModel.Text("unused"));
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            value.Messages = invalid switch
            {
                "null" => new Message[] { null! },
                "late-null" => new[] { Message.User("must not append"), null! },
                "orphan" => new[] { Message.User("must not append"), new Message(Role.User, new ContentBlock[] { new ToolResultBlock("orphan", "result") }) },
                _ => new[] { new Message(Role.Assistant, new ContentBlock[] { new ToolUseBlock("one", "tool", JsonSerializer.SerializeToElement(new { })) }),
                    new Message(Role.User, new ContentBlock[] { new ToolResultBlock("two", "result") }) }
            };
            return default;
        });
        hooks.Register<MessageAddedEvent>((_, _) => { observed++; return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, SessionStore = store, SessionId = "original" });

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InvokeAsync("original"));

        Assert.Equal(history, agent.Messages); Assert.Equal(history, store.Snapshot!.Messages);
        Assert.Empty(store.Attempts); Assert.Empty(model.Requests); Assert.Equal(0, observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedAcceptedInputSaveRetainsPrimaryAndRetriesItsDirtyWrite(bool afterFailure)
    {
        var primary = new IOException("input save"); var cleanup = new IOException("after callback");
        var store = new IncomingInputStore(); var failed = false; var afterCalls = 0;
        store.SaveFailure = _ => { if (failed) return null; failed = true; return primary; };
        var hooks = new HookRegistry(); var model = new EventTestModel(EventTestModel.Text("unused"));
        hooks.Register<BeforeInvocationEvent>((value, _) => { value.Messages = new[] { Message.User("replacement") }; return default; });
        hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            afterCalls++; Assert.Same(primary, value.Error);
            if (afterFailure) throw cleanup;
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, SessionStore = store, SessionId = "dirty" });

        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("original")));

        Assert.Equal(1, afterCalls); Assert.Equal(2, store.Attempts.Count); Assert.Single(store.Saves);
        Assert.Equal("replacement", Assert.Single(store.Snapshot!.Messages).Text);
        Assert.Equal("replacement", Assert.Single(agent.Messages).Text); Assert.Empty(model.Requests);
        if (afterFailure) Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
    }

    [Fact]
    public async Task AFailedResumeHookAbandonsProtectedInputOnceAndPreservesItsPrimaryOverAfterCleanup()
    {
        var primary = new IOException("before hook"); var cleanup = new IOException("after hook");
        var before = 0; var after = 0; var appended = 0; var abandoned = new List<Exception>(); var hooks = new HookRegistry();
        var model = new EventTestModel(EventTestModel.Text("first answer"));
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            if (++before == 2) { value.Agent.State.Set("hook effect", true); value.Messages = Array.Empty<Message>(); throw primary; }
            return default;
        });
        hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (++after == 1)
            {
                value.Agent.AddContinuationInput(value, new[] { Message.User("protected") }, () => { appended++; return default; }, error => { abandoned.Add(error); return default; });
                value.Resume = new InvocationContinuation("manual");
            }
            else { Assert.Same(primary, value.Error); throw cleanup; }
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("first")));

        Assert.Equal(0, appended); Assert.Same(primary, Assert.Single(abandoned));
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"])));
        Assert.Equal(new[] { "first", "first answer" }, agent.Messages.Select(message => message.Text));
        Assert.Single(model.Requests); Assert.True(agent.State.Get<bool>("hook effect"));
    }

    [Fact]
    public async Task ClosingAtTheResumeHookAbandonsProtectedInputWithoutAHiddenModelCall()
    {
        var before = 0; var appended = 0; var abandoned = 0; var hooks = new HookRegistry();
        var model = new EventTestModel(EventTestModel.Text("first answer"));
        hooks.Register<BeforeInvocationEvent>((value, _) => { if (++before == 2) value.Messages = Array.Empty<Message>(); return default; });
        hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (before == 1)
            {
                value.Agent.AddContinuationInput(value, new[] { Message.User("protected") }, () => { appended++; return default; }, _ => { abandoned++; return default; });
                value.Resume = new InvocationContinuation("manual");
            }
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        await using (var iterator = agent.StreamAsync("first").GetAsyncEnumerator())
            while (await iterator.MoveNextAsync())
                if (iterator.Current is BeforeInvocationEvent && before == 2) break;

        Assert.Equal(0, appended); Assert.Equal(1, abandoned); Assert.Single(model.Requests);
        Assert.Equal(new[] { "first", "first answer" }, agent.Messages.Select(message => message.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealResponsesRestorationKeepsItsCursorUntilAValidProviderAcknowledgesReplacement(bool invalid)
    {
        using var handler = new IncomingResponsesHandler(); using var client = new HttpClient(handler);
        var model = new OpenAIResponsesModel(client, new("model", "dummy") { Stateful = true });
        var store = new IncomingInputStore();
        await using (var first = new Agent(new() { Model = model, SessionStore = store, SessionId = "server" })) await first.InvokeAsync("first");
        Assert.Equal("response-1", store.Snapshot!.ModelState!["openaiResponses"].GetProperty("responseId").GetString());
        Assert.Empty(store.Snapshot.Messages); Assert.Equal(0, store.Snapshot.ModelHistoryCount);
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            Assert.Empty(value.Agent.Messages);
            Assert.Equal("response-1", value.Agent.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
            value.Messages = invalid ? new[] { new Message(Role.User, new ContentBlock[] { new ToolResultBlock("orphan", "invalid") }) } : new[] { Message.User("replacement") };
            return default;
        });
        await using var restored = new Agent(new() { Model = model, Hooks = hooks, SessionStore = store, SessionId = "server" });

        if (invalid)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => restored.InvokeAsync("original"));
            Assert.Single(handler.Bodies); Assert.Empty(restored.Messages);
            Assert.Equal("response-1", restored.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
            Assert.Equal(0, store.Snapshot.ModelHistoryCount);
        }
        else
        {
            Assert.Equal(StopReasons.EndTurn, (await restored.InvokeAsync("original")).StopReason);
            var body = handler.Bodies[1]; Assert.Equal("response-1", body.GetProperty("previous_response_id").GetString());
            var input = body.GetProperty("input"); Assert.Equal(1, input.GetArrayLength());
            Assert.Equal("replacement", input[0].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("response-2", restored.ModelState.Get("openaiResponses")!.Value.GetProperty("responseId").GetString());
            Assert.Empty(restored.Messages);
        }
    }

    private sealed class IncomingResponsesHandler : HttpMessageHandler
    {
        internal List<JsonElement> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            var id = "response-" + Bodies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var created = JsonSerializer.Serialize(new { type = "response.created", response = new { id } });
            var completed = JsonSerializer.Serialize(new { type = "response.completed", response = new { id, status = "completed" } });
            return new(HttpStatusCode.OK) { Content = new StringContent("event: response.created\ndata: " + created + "\n\nevent: response.completed\ndata: " + completed + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private static FunctionTool ApprovalTool(Action accepted)
        => new(new("approve", "approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            Assert.True(context.Interrupt("approval").GetBoolean());
            accepted(); return new ValueTask<string>("approved");
        });

    private static ModelStreamEvent[] ApprovalTurn()
        => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("call", "approve", JsonSerializer.SerializeToElement(new { }))),
            new ModelContentBlockDeltaEvent(0, ToolInput: "{}"), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
}
