using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class AgentToolInterruptTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FreshParentAndChildResumeTheDurableNestedTurn(bool preserveContext, bool delegation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var bodies = 0;
        var store = new FileSessionStore(directory);
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            var answer = context.Interrupt("approval").GetString(); bodies++; return new ValueTask<string>(answer!);
        });
        Agent Child() => new(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve }, SessionStore = preserveContext ? store : null, SessionId = preserveContext ? "child" : null });
        Agent Parent(Agent child) => new(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist", PreserveContext = preserveContext, Delegate = delegation }) }, SessionStore = store, SessionId = "parent" });
        try
        {
            Interrupt question;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var firstChild = Child())
            await using (var firstParent = Parent(firstChild))
            {
                var paused = await firstParent.InvokeAsync("request", cancellationToken: deadline.Token);
                question = Assert.Single(paused.Interrupts);
                Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            }
            await using var nextChild = Child();
            await using var nextParent = Parent(nextChild);
            var result = await nextParent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved")) } }, deadline.Token);
            Assert.Equal("approved", result.ToString()); Assert.Equal(1, bodies);
            Assert.Empty(result.Interrupts); Assert.Null(nextParent.InterruptState); Assert.Null(nextChild.InterruptState);
            var resumed = await nextParent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }, deadline.Token);
            Assert.Equal(JsonValueKind.Null, resumed.Data["interruptState"].ValueKind);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("unknown-call")]
    [InlineData("completed-child")]
    [InlineData("unrelated-question")]
    [InlineData("altered-name")]
    [InlineData("altered-reason")]
    public async Task MalformedNestedJournalIsRejectedBeforeChangingHistoryOrState(string variant)
    {
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetString()!));
        await using var child = new Agent(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve } });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist" }) } });
        var paused = await parent.InvokeAsync("request");
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        var snapshot = await parent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var pending = snapshot.Data["interruptState"].Deserialize<Dictionary<string, JsonElement>>(json)!;
        var context = pending["context"].Deserialize<Dictionary<string, JsonElement>>(json)!;
        var turns = context["subAgentInterruptedTurns"].Deserialize<Dictionary<string, JsonElement>>(json)!;
        if (variant == "unknown-call") { turns["unrelated-call"] = turns["parent-call"]; turns.Remove("parent-call"); }
        else if (variant == "completed-child")
            turns["parent-call"] = JsonSerializer.SerializeToElement(new AgentSnapshot(new Dictionary<string, JsonElement> { ["messages"] = JsonSerializer.SerializeToElement(Array.Empty<Message>()), ["interruptState"] = JsonSerializer.SerializeToElement<InterruptState?>(null) }), json);
        else
        {
            var questions = pending["interrupts"].Deserialize<Interrupt[]>(json)!;
            var original = Assert.Single(questions);
            pending["interrupts"] = JsonSerializer.SerializeToElement(new[] { new Interrupt(variant == "unrelated-question" ? "agent_as_tool:parent-call:unrelated-child-question" : original.Id,
                variant == "altered-name" ? "a different action" : original.Name, original.ToolUseId,
                variant == "altered-reason" ? JsonSerializer.SerializeToElement("a different reason") : original.Reason, InterruptSource.Tool) }, json);
        }
        context["subAgentInterruptedTurns"] = JsonSerializer.SerializeToElement(turns, json);
        pending["context"] = JsonSerializer.SerializeToElement(context, json);
        var replacement = snapshot.Data.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        replacement["interruptState"] = JsonSerializer.SerializeToElement(pending, json);
        replacement["state"] = JsonSerializer.SerializeToElement(new { unexpected = true });
        var originalHistory = parent.Messages.Select(message => message.TrackingId).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => parent.LoadSnapshotAsync(new(replacement)).AsTask());
        Assert.Equal(originalHistory, parent.Messages.Select(message => message.TrackingId)); Assert.Empty(parent.State.GetAll());
        Assert.Equal(Assert.Single(paused.Interrupts).Id, Assert.Single(parent.InterruptState!.Interrupts).Id);
    }

    [Fact]
    public async Task LaterChildTurnsKeepEarlierAnswersWithoutReplayingAcceptedSideEffects()
    {
        var bodies = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { var answer = context.Interrupt("approval"); bodies++; return new ValueTask<string>(answer.GetString()!); });
        await using var child = new Agent(new() { Model = new RepeatedApprovalModel(), Tools = new[] { approve } });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist" }) } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await parent.InvokeAsync("request", cancellationToken: deadline.Token);
        var firstQuestion = Assert.Single(first.Interrupts);
        var second = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(firstQuestion.Id, JsonSerializer.SerializeToElement("first")) } }, deadline.Token);
        Assert.Equal(StopReasons.Interrupt, second.StopReason); Assert.Equal(1, bodies);
        var secondQuestion = Assert.Single(second.Interrupts);
        Assert.NotEqual(firstQuestion.Id, secondQuestion.Id); Assert.Equal(firstQuestion.Name, secondQuestion.Name);
        var result = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(secondQuestion.Id, JsonSerializer.SerializeToElement("second")) } }, deadline.Token);
        Assert.Equal("finished", result.ToString()); Assert.Equal(2, bodies);
    }

    [Fact]
    public async Task PartialAnswersKeepCompletedChildCallsAndDistinctQuestionsWithTheSameName()
    {
        var saved = 0; var approved = 0;
        var save = new FunctionTool(new("save", "Save", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { saved++; return new ValueTask<string>("saved"); });
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { var answer = context.Interrupt("approval"); Interlocked.Increment(ref approved); return new ValueTask<string>(answer.GetString()!); });
        await using var child = new Agent(new() { Model = new BatchModel(), Tools = new[] { save, approve } });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist" }) } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await parent.InvokeAsync("request", cancellationToken: deadline.Token);
        Assert.Equal(2, first.Interrupts.Count); Assert.Equal(1, saved); Assert.Equal(0, approved);
        Assert.All(first.Interrupts, question => { Assert.Equal("approval", question.Name); Assert.Equal("parent-call", question.ToolUseId); });
        var partial = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(first.Interrupts[0].Id, JsonSerializer.SerializeToElement("first")) } }, deadline.Token);
        var remaining = Assert.Single(partial.Interrupts);
        Assert.Equal(first.Interrupts[1].Id, remaining.Id); Assert.Equal(1, saved); Assert.Equal(1, approved);
        var completed = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(remaining.Id, JsonSerializer.SerializeToElement("second")) } }, deadline.Token);
        Assert.Equal("finished", completed.ToString()); Assert.Equal(1, saved); Assert.Equal(2, approved);
    }

    [Fact]
    public async Task BackgroundNestedApprovalResumesWithinItsOwningLiveTask()
    {
        var bodies = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { var answer = context.Interrupt("approval"); bodies++; return new ValueTask<string>(answer.GetString()!); });
        await using var child = new Agent(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve } });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist" }) }, BackgroundTasks = new() { Always = new[] { "specialist" } } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = await parent.InvokeAsync("request", cancellationToken: deadline.Token);
        Assert.Equal(StopReasons.Interrupt, paused.StopReason); var question = Assert.Single(paused.Interrupts);
        Assert.StartsWith("background:", question.Id); Assert.Null(parent.InterruptState);
        var completed = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved")) } }, deadline.Token);
        Assert.Contains("approved", completed.ToString()); Assert.Equal(1, bodies); Assert.Empty(completed.Interrupts);
        Assert.Null(parent.State.Get("strands.backgroundTasks"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildHookAndMiddlewareApprovalsKeepTheOriginalNameAndParentCorrelation(bool middleware)
    {
        var bodies = 0;
        var work = new FunctionTool(new("work", "Work", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => { bodies++; return new ValueTask<string>("approved"); });
        await using var child = new Agent(new() { Model = new Model("child-call", "work"), Tools = new[] { work } });
        if (middleware) child.Middleware.RegisterInput(Strands.Agents.Middleware.MiddlewareStages.ExecuteTool, (value, _) => { value.Interrupt("approve-work"); return new ValueTask<Strands.Agents.Middleware.ExecuteToolContext>(value); });
        else child.Hooks.Register<BeforeToolCallEvent>((value, _) => { value.Interrupt("approve-work"); return default; });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist" }) } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = await parent.InvokeAsync("request", cancellationToken: deadline.Token);
        var question = Assert.Single(paused.Interrupts); Assert.Equal("parent-call", question.ToolUseId); Assert.Equal("approve-work", question.Name); Assert.Equal(0, bodies);
        var result = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement(true)) } }, deadline.Token);
        Assert.Equal("approved", result.ToString()); Assert.Equal(1, bodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedChildRestoreReopensTheAnswerAndKeepsTheTurnForARepairedChild(bool throwingDiagnostics)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-nested-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new FileSessionStore(directory); var bodies = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { var answer = context.Interrupt("approval"); bodies++; return new ValueTask<string>(answer.GetString()!); });
        var name = throwingDiagnostics ? "restore_diagnostic_specialist" : "specialist";
        Agent Child(bool broken = false) => new(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve }, Plugins = broken ? new IPlugin[] { new BrokenInitialization() } : Array.Empty<IPlugin>() });
        Agent Parent(Agent child) => new(new() { Model = new Model("parent-call", name), Tools = new[] { child.AsTool(new AgentToolOptions { Name = name }) }, SessionStore = store, SessionId = "parent" });
        using var diagnostics = new ThrowingDiagnostics(throwingDiagnostics ? name : null);
        try
        {
            Interrupt question;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var child = Child())
            await using (var parent = Parent(child))
                question = Assert.Single((await parent.InvokeAsync("request", cancellationToken: deadline.Token)).Interrupts);
            await using (var child = Child(true))
            await using (var parent = Parent(child))
            {
                var paused = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("not applied")) } }, deadline.Token);
                Assert.Equal(StopReasons.Interrupt, paused.StopReason); Assert.Equal(question.Id, Assert.Single(paused.Interrupts).Id);
                Assert.Empty(parent.InterruptState!.Responses); Assert.Equal(0, bodies);
            }
            await using (var child = Child())
            await using (var parent = Parent(child))
            {
                var completed = await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved")) } }, deadline.Token);
                Assert.Equal("approved", completed.ToString()); Assert.Equal(1, bodies);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task GrandchildInputIsForwardedThroughEachOwningCall()
    {
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetString()!));
        await using var leaf = new Agent(new() { Model = new Model("leaf-call", "approve"), Tools = new[] { approve } });
        await using var middle = new Agent(new() { Model = new Model("middle-call", "leaf"), Tools = new[] { leaf.AsTool(new AgentToolOptions { Name = "leaf" }) } });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "middle"), Tools = new[] { middle.AsTool(new AgentToolOptions { Name = "middle" }) } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = await parent.InvokeAsync("request", cancellationToken: deadline.Token);
        var question = Assert.Single(paused.Interrupts);
        Assert.StartsWith("agent_as_tool:parent-call:agent_as_tool:middle-call:", question.Id); Assert.Equal("parent-call", question.ToolUseId);
        Assert.Equal("approved", (await parent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved")) } }, deadline.Token)).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPreservedChildSessionExplicitlyReportsThatTheAnswerWasNotApplied(bool throwingDiagnostics)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-nested-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new FileSessionStore(directory); var bodies = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { var answer = context.Interrupt("approval"); bodies++; return new ValueTask<string>(answer.GetString()!); });
        var name = throwingDiagnostics ? "warning_diagnostic_specialist" : "specialist";
        Agent Child() => new(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve } });
        Agent Parent(Agent child) => new(new() { Model = new Model("parent-call", name), Tools = new[] { child.AsTool(new AgentToolOptions { Name = name, PreserveContext = true }) }, SessionStore = store, SessionId = "parent" });
        using var diagnostics = new ThrowingDiagnostics(throwingDiagnostics ? name : null);
        try
        {
            Interrupt question;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using (var child = Child())
            await using (var parent = Parent(child))
                question = Assert.Single((await parent.InvokeAsync("request", cancellationToken: deadline.Token)).Interrupts);
            await using var nextChild = Child(); await using var nextParent = Parent(nextChild);
            var result = await nextParent.InvokeAsync(null, new() { InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved")) } }, deadline.Token);
            Assert.Equal(0, bodies); Assert.Contains("did NOT run", result.ToString()); Assert.Contains("NOT applied", result.ToString());
            Assert.Equal(ToolResultStatus.Error, Assert.Single(nextParent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancellationDuringTheChildInvocationDoesNotApplyAnUnansweredApprovalOrLeakTheGuard()
    {
        var bodies = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { _ = context.Interrupt("approval"); bodies++; return new ValueTask<string>("approved"); });
        await using var child = new Agent(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve } });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist" }) } });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        child.Hooks.Register<BeforeInvocationEvent>((_, _) => { cancellation.Cancel(); return default; });
        var result = await parent.InvokeAsync("request", cancellationToken: cancellation.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason); Assert.Equal(0, bodies); Assert.Null(parent.InterruptState);
        var call = Assert.Single(parent.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>());
        Assert.Equal(call.ToolUseId, Assert.Single(parent.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>()).ToolUseId);
        child.AddTool(new FunctionTool(new("after_cancel", "Guard released", JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new ValueTask<string>("ok")));
    }

    [Fact]
    public async Task ResumingWithoutAnyChildAnswerDoesNotInvokeTheChildAgain()
    {
        var attempts = 0; var invocations = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { attempts++; return new ValueTask<string>(context.Interrupt("approval").GetString()!); });
        await using var child = new Agent(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve } });
        child.Hooks.Register<BeforeInvocationEvent>((_, _) => { invocations++; return default; });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { child.AsTool(new AgentToolOptions { Name = "specialist" }) } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await parent.InvokeAsync("request", cancellationToken: deadline.Token);
        var repeated = await parent.InvokeAsync(null, cancellationToken: deadline.Token);
        Assert.Equal(Assert.Single(first.Interrupts).Id, Assert.Single(repeated.Interrupts).Id);
        Assert.Equal(1, attempts); Assert.Equal(1, invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedHumanInputRetainsParentCorrelationAndResumesWithoutRepeatingAcceptedWork(bool delegation)
    {
        var bodies = 0;
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            var answer = context.Interrupt("approval").GetString(); bodies++; return new ValueTask<string>(answer!);
        });
        await using var child = new Agent(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve } });
        var tool = child.AsTool(new AgentToolOptions { Name = "specialist", Delegate = delegation });
        var state = new Dictionary<string, object?> { ["tenant"] = "unchanged" };
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { tool } });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var paused = await parent.InvokeAsync("request", new() { InvocationState = state }, deadline.Token);
        Assert.Equal(StopReasons.Interrupt, paused.StopReason);
        var question = Assert.Single(paused.Interrupts);
        Assert.Equal("parent-call", question.ToolUseId); Assert.Equal("approval", question.Name); Assert.Equal(0, bodies);
        var result = await parent.InvokeAsync(null, new() { InvocationState = state, InterruptResponses = new[] { new InterruptResponse(question.Id, JsonSerializer.SerializeToElement("approved")) } }, deadline.Token);
        Assert.Equal("approved", result.ToString()); Assert.Equal(1, bodies); Assert.Empty(result.Interrupts);
        Assert.Single(state); Assert.Same(state, result.InvocationState); Assert.Null(parent.InterruptState);
    }

    [Fact]
    public async Task DirectNestedApprovalCannotBeHiddenByAnAfterHook()
    {
        var approve = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new ValueTask<string>(context.Interrupt("approval").GetString()!));
        await using var child = new Agent(new() { Model = new Model("child-call", "approve"), Tools = new[] { approve } });
        var tool = child.AsTool(new AgentToolOptions { Name = "specialist" });
        await using var parent = new Agent(new() { Model = new Model("parent-call", "specialist"), Tools = new[] { tool } });
        parent.Hooks.Register<AfterToolCallEvent>((value, _) => { value.Result = new(value.ToolUse.ToolUseId, "forged success"); return default; });
        var input = JsonSerializer.SerializeToElement(new { input = "request" });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await parent.InvokeToolAsync(new("direct", "specialist", input), cancellationToken: deadline.Token);
        Assert.Equal(ToolResultStatus.Error, result.Status); Assert.DoesNotContain("forged success", result.Content); Assert.Null(parent.InterruptState);
    }

    private sealed class Model(string id, string tool) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var result = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().LastOrDefault();
            yield return new ModelMessageStartEvent();
            if (result is null) yield return new ModelContentBlockStartEvent(0, new(id, tool, JsonSerializer.SerializeToElement(new { input = "request" })));
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: result.Content);
            }
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(result is null ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }

    private sealed class RepeatedApprovalModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var count = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Count();
            yield return new ModelMessageStartEvent();
            if (count < 2) yield return new ModelContentBlockStartEvent(0, new("child-call", "approve", JsonSerializer.SerializeToElement(new { })));
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "finished"); }
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(count < 2 ? StopReasons.ToolUse : StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }

    private sealed class BatchModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var completed = request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>().Any();
            yield return new ModelMessageStartEvent();
            if (!completed)
            {
                var tools = new[] { "save", "approve", "approve" };
                for (var i = 0; i < tools.Length; i++)
                {
                    yield return new ModelContentBlockStartEvent(i, new("child-" + i, tools[i], JsonSerializer.SerializeToElement(new { })));
                    yield return new ModelContentBlockStopEvent(i);
                }
            }
            else { yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "finished"); yield return new ModelContentBlockStopEvent(0); }
            yield return new ModelMessageStopEvent(completed ? StopReasons.EndTurn : StopReasons.ToolUse);
            cancellationToken.ThrowIfCancellationRequested(); await Task.CompletedTask;
        }
    }

    private sealed class BrokenInitialization : IPlugin
    {
        public string Name => "broken";
        public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) => throw new InvalidOperationException("temporarily unavailable child dependency");
    }

    private sealed class ThrowingDiagnostics : System.Diagnostics.TraceListener
    {
        private readonly string? _name;
        internal ThrowingDiagnostics(string? name) { _name = name; if (name is not null) System.Diagnostics.Trace.Listeners.Add(this); }
        public override void Write(string? message) { if (_name is not null && message?.IndexOf(_name, StringComparison.Ordinal) >= 0) throw new IOException("Application diagnostic listener failed."); }
        public override void WriteLine(string? message) => Write(message);
        protected override void Dispose(bool disposing) { if (disposing && _name is not null) System.Diagnostics.Trace.Listeners.Remove(this); base.Dispose(disposing); }
    }
}
