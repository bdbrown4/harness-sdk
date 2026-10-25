using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using static Strands.Agents.Tests.Cancellation.CancellationTestModel;

namespace Strands.Agents.Tests.Cancellation;

public sealed class AgentCancellationCommitTests
{
    [Fact]
    public async Task ImmediateCancellationFromAfterModelKeepsTheAcceptedMessageButReturnsCancelled()
    {
        var model = new CancellationTestModel(Text("completed response"));
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal("completed response", result.Message!.Text);
        Assert.Equal("completed response", agent.Messages.Last().Text);
    }

    [Fact]
    public async Task CancellationFromModelOutputDoesNotStrandACompletedProviderReceipt()
    {
        var store = new Store();
        var effects = 0;
        var events = Call(("call", "save", "{}"));
        events[events.Length - 1] = new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = State("receipt") };
        var model = new StatefulModel(events);
        var tool = Tool("save", (_, _, _) => { effects++; return new("unexpected"); });
        await using var agent = new Agent(new() { Model = model, Tools = [tool], SessionStore = store, SessionId = "server-session" });
        agent.Middleware.RegisterOutput(MiddlewareStages.InvokeModel, (result, _) => { agent.Cancel("stopped after receipt"); return new(result); });
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(0, effects);
        Assert.Equal("receipt", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(2, store.Snapshot!.ModelHistoryCount);
        Assert.Equal("call", Assert.Single(store.Snapshot.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).ToolUseId);
        Assert.Equal("stopped after receipt", result.Message!.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatefulCancellationPersistsOnlyPendingOutputsForAFreshAgentWithoutRepeatingTheBody(bool fileStore)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-cancellation-sessions-" + Guid.NewGuid().ToString("N"));
        try
        {
            ISessionStore store = fileStore ? new FileSessionStore(directory) : new Store(); var effects = 0;
            var events = Call(("call", "save", "{}"));
            events[events.Length - 1] = new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = State("receipt") };
            var tool = Tool("save", (_, context, _) => { effects++; context.Agent.Cancel(); return new("committed effect"); });
            await using (var first = new Agent(new() { Model = new StatefulModel(events), Tools = [tool], SessionStore = store, SessionId = "server-session" }))
                Assert.Equal(StopReasons.Cancelled, (await first.InvokeAsync("go")).StopReason);
            var final = Text("acknowledged");
            final[final.Length - 1] = new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = State("second") };
            var secondModel = new StatefulModel(final);
            await using var second = new Agent(new() { Model = secondModel, Tools = [tool], SessionStore = store, SessionId = "server-session" });
            await second.InvokeAsync(null);
            Assert.Equal(1, effects);
            var request = Assert.Single(secondModel.Requests);
            Assert.Equal("receipt", request.ModelState!["responseId"].GetString());
            Assert.Equal("committed effect", Assert.IsType<ToolResultBlock>(Assert.Single(Assert.Single(request.Messages).Content)).Content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationAfterInputCommitRetriesTheAcceptedWriteBeforeReportingCancelled()
    {
        using var caller = new CancellationTokenSource();
        var entered = Gate(); var attempts = new List<CancellationToken>();
        var store = new Store
        {
            BeforeSave = async token =>
        {
            attempts.Add(token);
            if (attempts.Count == 1) { entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, token); }
        }
        };
        var model = new CancellationTestModel(Text("must not run"));
        await using var agent = new Agent(new() { Model = model, SessionStore = store, SessionId = "accepted-input" });
        var invocation = agent.InvokeAsync("already committed input", cancellationToken: caller.Token);
        await Wait(entered.Task);
        caller.Cancel();
        await Wait(invocation);
        Assert.Equal(StopReasons.Cancelled, (await invocation).StopReason);
        Assert.Equal("already committed input", Assert.Single(store.Snapshot!.Messages).Text);
        Assert.Equal(new[] { caller.Token, CancellationToken.None }, attempts);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task FailedAcceptedWriteRemainsDirtyAndRetainsPrimaryAndRetryFailure()
    {
        var primary = new IOException("accepted response save failed"); var secondary = new IOException("final save retry failed"); var saves = 0;
        var store = new Store
        {
            BeforeSave = _ =>
        {
            var count = Interlocked.Increment(ref saves);
            if (count == 2) throw primary;
            if (count == 3) throw secondary;
            return default;
        }
        };
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Text("accepted response")), SessionStore = store, SessionId = "failed-write" });
        agent.Hooks.Register<AfterModelCallEvent>((_, _) => { agent.Cancel(); return default; });
        var error = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("go"));
        Assert.Same(primary, error);
        Assert.Contains(secondary, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(3, saves);
        Assert.Equal("accepted response", agent.Messages.Last().Text);
        Assert.Equal("go", Assert.Single(store.Snapshot!.Messages).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCancellationMessageSaveRetainsThePrimaryAndRetriesTheAcceptedText(bool failingAfterHook)
    {
        var primary = new IOException("cancellation text save failed"); var secondary = new IOException("after invocation failed"); var saves = 0;
        var store = new Store { BeforeSave = _ => { if (Interlocked.Increment(ref saves) == 2) throw primary; return default; } };
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Text("must not run")), SessionStore = store, SessionId = "terminal-text" });
        agent.Hooks.Register<BeforeModelCallEvent>((_, _) => { agent.Cancel("accepted cancellation text"); return default; });
        if (failingAfterHook) agent.Hooks.Register<AfterInvocationEvent>((_, _) => throw secondary);
        var error = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("go"));
        Assert.Same(primary, error);
        if (failingAfterHook) Assert.Contains(secondary, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(3, saves);
        Assert.Equal("accepted cancellation text", store.Snapshot!.Messages.Last().Text);
    }

    [Fact]
    public async Task HookEndTurnPrecedesInternalCancellationAfterCompletedTools()
    {
        var tool = Tool("stop", (_, context, _) => { context.Agent.Cancel("lower priority cancellation"); return new("accepted"); });
        var model = new CancellationTestModel(Call(("call", "stop", "{}")));
        await using var agent = new Agent(new() { Model = model, Tools = [tool] });
        agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("hook completion")]; return default; });
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("hook completion", result.Message!.Text);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "lower priority cancellation");
    }

    [Fact]
    public async Task CallerCancellationStillTakesPriorityOverHookEndTurn()
    {
        using var caller = new CancellationTokenSource();
        var tool = Tool("stop", (_, _, _) => { caller.Cancel(); return new("accepted"); });
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Call(("call", "stop", "{}"))), Tools = [tool] });
        agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("not authoritative during caller cancellation")]; return default; });
        var result = await agent.InvokeAsync("go", cancellationToken: caller.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "not authoritative during caller cancellation");
    }

    [Fact]
    public async Task ASettledEndTurnCannotHideIndependentPipelineCleanupFailure()
    {
        var cleanup = new IOException("after-tools cleanup failed");
        var tool = Tool("stop", (_, context, _) => { context.Agent.Cancel(); return new("accepted"); });
        await using var agent = new Agent(new() { Model = new CancellationTestModel(Call(("call", "stop", "{}"))), Tools = [tool] });
        agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("not successful")]; throw cleanup; });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeAsync("go"));
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.DoesNotContain(agent.Messages, message => message.Text == "not successful");
    }

    [Fact]
    public async Task SyntheticStatefulCompletionCannotAcknowledgeProviderHistoryDuringCancellation()
    {
        var store = new Store(); var model = new StatefulModel(Text("must not run"));
        await using var agent = new Agent(new() { Model = model, ModelState = State("prior"), SessionStore = store, SessionId = "synthetic" });
        agent.Middleware.RegisterWrap(MiddlewareStages.InvokeModel, Cached);
        var result = await agent.InvokeAsync("go");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Empty(model.Requests);
        Assert.Equal("prior", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(0, store.Snapshot!.ModelHistoryCount);
        Assert.Equal(new[] { "go", "cached pending message" }, store.Snapshot.Messages.Select(message => message.Text));

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cached(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            agent.Cancel();
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached pending message"), StopReasons.EndTurn, new()));
        }
    }

    [Fact]
    public async Task DeferredCancellationPrecedesAnOtherwiseValidStructuredOutput()
    {
        var output = new StructuredOutputTool<int>(new()
        {
            Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" } }),
            Deserializer = input => input.GetProperty("value").GetInt32()
        });
        var model = new CancellationTestModel(Call(("structured", output.Spec.Name, "{\"value\":42}")));
        await using var agent = new Agent(new() { Model = model });
        agent.Hooks.Register<AfterToolCallEvent>((_, _) => { agent.Cancel("structured value was accepted but invocation stopped", true); return default; });
        var result = await agent.InvokeAsync("go", new() { StructuredOutputTool = output });
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Null(result.StructuredOutputToolUseId);
        Assert.True(output.TryGetResult("structured", out _));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task CancellationPreservesAPausedHumanLedgerAndAcceptedAnswersBeforeAnyReplay()
    {
        var store = new Store(); var attempts = 0; var effects = 0;
        var tool = Tool("approve", (_, context, _) => { attempts++; context.Interrupt("approve"); effects++; return new("approved"); });
        var model = new CancellationTestModel(Call(("call", "approve", "{}")), Text("finished"));
        await using var agent = new Agent(new() { Model = model, Tools = [tool], SessionStore = store, SessionId = "paused" });
        var paused = await agent.InvokeAsync("go");
        var question = Assert.Single(paused.Interrupts);
        using (agent.Hooks.Register<BeforeInvocationEvent>((_, _) => { agent.Cancel("cannot orphan the pause"); return default; }))
        {
            var cancelled = await agent.InvokeAsync(null, new() { InterruptResponses = [new(question.Id, JsonSerializer.SerializeToElement(true))] });
            Assert.Equal(StopReasons.Cancelled, cancelled.StopReason);
        }
        Assert.Equal(1, attempts);
        Assert.Equal(0, effects);
        Assert.Equal(question.Id, Assert.Single(store.Snapshot!.InterruptState!.Responses).InterruptId);
        Assert.Equal(Role.Assistant, agent.Messages.Last().Role);
        Assert.Equal(StopReasons.EndTurn, (await agent.InvokeAsync(null)).StopReason);
        Assert.Equal(1, effects);
    }

    private static IReadOnlyDictionary<string, JsonElement> State(string id) => new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement(id) };

    private sealed class StatefulModel(ModelStreamEvent[] events) : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var value in events) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); }
        }
    }

    private sealed class Store : ISessionStore
    {
        internal SessionSnapshot? Snapshot;
        internal Func<CancellationToken, ValueTask>? BeforeSave { get; init; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public async ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (BeforeSave is not null) await BeforeSave(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot));
        }
    }
}
