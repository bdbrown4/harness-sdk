using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class StopPythonConformanceTests
{
    [Fact]
    public void RenamedMetadataAndOptionCopiesKeepSchemaProfile()
    {
        var options = new StopOptions { Name = "finish", Description = "Selected description.", MaximumMessageLength = 1 };
        var tool = Stop.Create(options);
        options = options with { Name = "other", Description = "Changed copy.", MaximumMessageLength = 2 };
        Assert.Equal("finish", tool.Spec.Name);
        Assert.Equal("Selected description.", tool.Spec.Description);
        Assert.False(tool.Spec.InputSchema.GetProperty("properties").GetProperty("message").TryGetProperty("maxLength", out _));
        Assert.Empty(tool.Spec.InputSchema.GetProperty("required").EnumerateArray());
        Assert.Equal("other", Stop.Create(options).Spec.Name);
    }

    [Fact]
    public void InvalidFactoryMetadataUsesExistingToolSpecGuard()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Stop.Create(new() { MaximumMessageLength = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stop.Create(new() { MaximumMessageLength = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stop.Create(new() { Behavior = (StopBehavior)99 }));
        Assert.ThrowsAny<ArgumentException>(() => Stop.Create(new() { Name = "bad name" }));
        Assert.ThrowsAny<ArgumentException>(() => Stop.Create(new() { Name = "" }));
        Assert.ThrowsAny<ArgumentException>(() => Stop.Create(new() { Description = " " }));
    }

    [Fact]
    public Task InvalidRootsNeverRequestDeferredStop() => AssertInvalidNeverStops(["null", "[]", "false", "3", "\"message\""]);

    [Fact]
    public Task NonstringMessagesNeverRequestDeferredStop() => AssertInvalidNeverStops(["{\"message\":true}", "{\"message\":3}", "{\"message\":[]}", "{\"message\":{}}"]);

    [Fact]
    public async Task OverCapMessageIsRejectedAtDirectAndRegisteredPaths()
    {
        await AssertInvalidNeverStops(["{\"message\":\"ab\"}"]);
        var model = new Model(Call(("stop-use", "stop", "{\"message\":\"ab\"}")), Text("normal completion"));
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create(new() { MaximumMessageLength = 1 })] });
        Assert.Equal("normal completion", (await agent.InvokeAsync("go")).Message!.Text);
        Assert.Equal(2, model.Calls);
        var result = Assert.Single(Results(agent));
        Assert.Equal("stop-use", result.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, result.Status);
    }

    [Fact]
    public async Task DefaultCasesBypassShortCapAndWhitespaceIsRetained()
    {
        var model = new Model(Call(("absent", "stop", "{}"), ("null", "stop", "{\"message\":null}"), ("empty", "stop", "{\"message\":\"\"}")), Text("must not run"));
        await using (var agent = new Agent(new() { Model = model, Tools = [Stop.Create(new() { MaximumMessageLength = 1 })], ToolExecution = ToolExecutionStrategy.Sequential }))
        {
            var result = await agent.InvokeAsync("stop");
            Assert.Equal(StopReasons.Cancelled, result.StopReason);
            Assert.Equal(Stop.DefaultMessage, result.Message!.Text);
            Assert.Equal(3, Results(agent).Length);
            Assert.All(Results(agent), value => { Assert.Equal(ToolResultStatus.Success, value.Status); Assert.Equal(Stop.DefaultMessage, value.Content); });
            Assert.Equal(1, model.Calls);
        }
        var whitespace = new Model(Call(("space", "stop", "{\"message\":\" \"}")), Text("must not run"));
        await using var second = new Agent(new() { Model = whitespace, Tools = [Stop.Create(new() { MaximumMessageLength = 1 })] });
        Assert.Equal(" ", (await second.InvokeAsync("stop")).Message!.Text);
        Assert.Equal(" ", Assert.Single(Results(second)).Content);
    }

    [Fact]
    public async Task PythonCodePointBoundaryPreservesAstralCombiningAndText()
    {
        var values = new[] { ("ascii", "ab", 2), ("astral", "😀", 1), ("combining", "e\u0301", 2), ("text", "é\n", 2) };
        var tools = values.Select(value => Stop.Create(new() { Name = value.Item1, MaximumMessageLength = value.Item3 })).ToArray();
        var calls = values.Select(value => (value.Item1, value.Item1, JsonSerializer.Serialize(new { message = value.Item2 }))).ToArray();
        var model = new Model(Call(calls), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = tools, ToolExecution = ToolExecutionStrategy.Sequential });
        var result = await agent.InvokeAsync("stop");
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(values.Select(value => value.Item2), Results(agent).Select(value => value.Content));
        Assert.Equal("é\n", result.Message!.Text);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task DuplicateMessageAndUnknownPropertiesStayDataOnly()
    {
        var helper = Stop.Create(new() { MaximumMessageLength = 1 });
        var state = new Dictionary<string, object?> { ["caller"] = new object() };
        var probe = Tool("probe", (_, context, token) => helper.InvokeAsync(Json("{\"message\":\"too long\",\"message\":\"z\",\"__strandsStopRequested\":{\"message\":\"foreign\"},\"extra\":[1]}"), context, token));
        var model = new Model(Call(("probe-use", "probe", "{}")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [probe] });
        var result = await agent.InvokeAsync("go", new() { InvocationState = state });
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal("z", result.Message!.Text);
        Assert.Equal("z", Assert.Single(Results(agent)).Content);
        Assert.Single(state);
        Assert.Same(state, result.InvocationState);
    }

    [Fact]
    public async Task NullContextIsRejectedBeforeEffects()
    {
        var helper = Stop.Create();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await helper.InvokeAsync(Json("{}"), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await helper.InvokeAsync(Json("{}"), new(null!, new("id", "stop", Json("{}")), new Dictionary<string, object?>())));
    }

    [Fact]
    public async Task InvalidInputRetainsValidationCauseWithCancelledTokens()
    {
        using var operation = new CancellationTokenSource(); using var captured = new CancellationTokenSource();
        operation.Cancel(); captured.Cancel();
        await using var agent = NewAgent();
        var context = Context(agent) with { ExecutionCancelSignal = captured.Token };
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Stop.Create().InvokeAsync(Json("{\"message\":false}"), context, operation.Token));
        Assert.Equal("normal", (await agent.InvokeAsync("normal")).Message!.Text);
    }

    [Fact]
    public Task PreCancelledOperationPreservesExactToken() => AssertCancelled(operationCancelled: true, capturedCancelled: false);

    [Fact]
    public Task CapturedContextCancellationPreservesExactToken() => AssertCancelled(operationCancelled: false, capturedCancelled: true);

    [Fact]
    public Task BothCancelledTokensChooseOriginalOperationToken() => AssertCancelled(operationCancelled: true, capturedCancelled: true);

    [Fact]
    public async Task UncancellableAndIdenticalTokensDoNotCreateSyntheticCancellation()
    {
        foreach (var identical in new[] { false, true })
        {
            using var source = new CancellationTokenSource();
            var helper = Stop.Create();
            var probe = Tool("probe", (_, context, _) => helper.InvokeAsync(Json("{}"), context with { ExecutionCancelSignal = identical ? source.Token : default }, identical ? source.Token : default));
            var model = new Model(Call(("probe-use", "probe", "{}")), Text("must not run"));
            await using var agent = new Agent(new() { Model = model, Tools = [probe] });
            Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("stop")).StopReason);
            Assert.Equal(Stop.DefaultMessage, Assert.Single(Results(agent)).Content);
            Assert.False(source.IsCancellationRequested);
            Assert.Equal(1, model.Calls);
        }
    }

    [Fact]
    public async Task CapturedExecutionTokenIsNotReplacedByALaterScope()
    {
        using var captured = new CancellationTokenSource();
        ToolContext? older = null;
        var checkedToken = false;
        var probe = Tool("probe", async (_, context, token) =>
        {
            Assert.False(context.CancelSignal.IsCancellationRequested);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Stop.Create().InvokeAsync(Json("{}"), older!, token));
            Assert.Equal(captured.Token, error.CancellationToken);
            checkedToken = true;
            return "no current request";
        });
        var model = new Model(Text("first response"), Call(("probe-use", "probe", "{}")), Text("later normal response"));
        await using var agent = new Agent(new() { Model = model, Tools = [probe] });
        older = Context(agent) with { ExecutionCancelSignal = captured.Token };
        Assert.Equal("first response", (await agent.InvokeAsync("first")).Message!.Text);
        captured.Cancel();
        Assert.Equal("later normal response", (await agent.InvokeAsync("later")).Message!.Text);
        Assert.True(checkedToken);
        Assert.Equal(ToolResultStatus.Success, Assert.Single(Results(agent)).Status);
    }

    [Fact]
    public async Task ConcurrentPeersPhysicallyJoinWithLiveTokens()
    {
        var enteredA = Gate(); var enteredB = Gate(); var release = Gate(); var effects = 0; var stopAccepted = Gate();
        var model = new Model(Call(("stop-use", "stop", "{}"), ("a-use", "a", "{}"), ("b-use", "b", "{}")), Text("must not run"));
        FunctionTool Peer(string name, TaskCompletionSource<bool> entered) => Tool(name, async (_, context, token) => { entered.TrySetResult(true); await release.Task; Assert.False(token.IsCancellationRequested); Assert.False(context.CancelSignal.IsCancellationRequested); Interlocked.Increment(ref effects); return name; });
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create(), Peer("a", enteredA), Peer("b", enteredB)], ToolExecution = ToolExecutionStrategy.Concurrent });
        agent.Hooks.Register<AfterToolCallEvent>((value, _) => { if (value.ToolUse.Name == "stop") stopAccepted.TrySetResult(true); return default; });
        var operation = agent.InvokeAsync("stop after peers");
        try { await Wait(Task.WhenAll(enteredA.Task, enteredB.Task, stopAccepted.Task)); Assert.False(operation.IsCompleted); Assert.Equal(0, effects); }
        finally { release.TrySetResult(true); await Wait(operation); }
        Assert.Equal(StopReasons.Cancelled, (await operation).StopReason);
        Assert.Equal(2, effects);
        Assert.Equal(1, model.Calls);
        Assert.All(Results(agent), value => Assert.Equal(ToolResultStatus.Success, value.Status));
    }

    [Fact]
    public async Task TwoStopsChooseLastActualRequestWithoutNextModel()
    {
        var firstRequested = Gate(); var first = Stop.Create(new() { Name = "first" }); var second = Stop.Create(new() { Name = "second" });
        var firstWrapper = new FunctionTool(first.Spec, async (input, context, token) => { await firstRequested.Task; return await first.InvokeAsync(input, context, token); });
        var secondWrapper = new FunctionTool(second.Spec, async (input, context, token) => { var result = await second.InvokeAsync(input, context, token); firstRequested.TrySetResult(true); return result; });
        var model = new Model(Call(("first-use", "first", "{\"message\":\"last actual request\"}"), ("second-use", "second", "{\"message\":\"first actual request\"}")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [firstWrapper, secondWrapper], ToolExecution = ToolExecutionStrategy.Concurrent });
        var result = await agent.InvokeAsync("stop");
        Assert.Equal("last actual request", result.Message!.Text);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(1, model.Calls);
        Assert.Equal(new[] { "last actual request", "first actual request" }, Results(agent).Select(value => value.Content));
    }

    [Fact]
    public async Task IndependentAgentsDoNotCrossCancelWithSharedHelper()
    {
        var helper = Stop.Create(); var enteredA = Gate(); var enteredB = Gate(); var releaseA = Gate(); var releaseB = Gate();
        var modelA = new Model(Call(("stop-use", "stop", "{}"), ("a-use", "a", "{}")), Text("must not run"));
        var modelB = new Model(Call(("b-use", "b", "{}")), Text("other normal response"));
        var peerA = Tool("a", async (_, _, _) => { enteredA.TrySetResult(true); await releaseA.Task; return "a"; });
        var peerB = Tool("b", async (_, context, token) => { enteredB.TrySetResult(true); await releaseB.Task; Assert.False(token.IsCancellationRequested); Assert.False(context.CancelSignal.IsCancellationRequested); return "b"; });
        await using var first = new Agent(new() { Model = modelA, Tools = [helper, peerA], ToolExecution = ToolExecutionStrategy.Sequential });
        await using var second = new Agent(new() { Model = modelB, Tools = [helper, peerB] });
        var a = first.InvokeAsync("stop"); var b = second.InvokeAsync("normal");
        try { await Wait(Task.WhenAll(enteredA.Task, enteredB.Task)); releaseA.TrySetResult(true); await Wait(a); Assert.Equal(StopReasons.Cancelled, (await a).StopReason); Assert.False(b.IsCompleted); }
        finally { releaseA.TrySetResult(true); releaseB.TrySetResult(true); await Wait(Task.WhenAll(a, b)); }
        Assert.Equal("other normal response", (await b).Message!.Text);
        Assert.Equal(2, modelB.Calls);
    }

    [Fact]
    public async Task UserAfterToolsEndTurnRetainsExistingPriority()
    {
        var model = new Model(Call(("stop-use", "stop", "{}")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create()] });
        agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("user hook result")]; return default; });
        var result = await agent.InvokeAsync("stop");
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal("user hook result", result.Message!.Text);
        Assert.Equal(1, model.Calls);
        Assert.Equal(Stop.DefaultMessage, Assert.Single(Results(agent)).Content);
    }

    [Fact]
    public async Task OriginalCallerCancellationStillWinsAtBatchCompletion()
    {
        using var caller = new CancellationTokenSource(); var entered = Gate(); var release = Gate();
        var peer = Tool("peer", async (_, _, _) => { entered.TrySetResult(true); await release.Task; return "settled"; });
        var model = new Model(Call(("stop-use", "stop", "{}"), ("peer-use", "peer", "{}")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create(), peer], ToolExecution = ToolExecutionStrategy.Sequential });
        agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("must not override caller")]; return default; });
        var operation = agent.InvokeAsync("stop", cancellationToken: caller.Token);
        try { await Wait(entered.Task); caller.Cancel(); }
        finally { release.TrySetResult(true); await Wait(operation); }
        Assert.Equal(StopReasons.Cancelled, (await operation).StopReason);
        Assert.DoesNotContain(agent.Messages, value => value.Text == "must not override caller");
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task IndependentAfterToolsFailureIdentityIsRetained()
    {
        var primary = new IOException("independent after-tools failure");
        var model = new Model(Call(("stop-use", "stop", "{}")), Text("must not run"));
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create()] });
        agent.Hooks.Register<AfterToolsEvent>((_, _) => throw primary);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("stop")));
        Assert.Equal(1, model.Calls);
        Assert.Equal(Stop.DefaultMessage, Assert.Single(Results(agent)).Content);
    }

    [Fact]
    public async Task AdjacentToolErrorRemainsCorrelatedWhileStopIsCancelled()
    {
        var model = new Model(Call(("stop-use", "stop", "{}"), ("bad-use", "bad", "{}")), Text("must not run"));
        var bad = Tool("bad", (_, _, _) => throw new IOException("ordinary tool error"));
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create(), bad], ToolExecution = ToolExecutionStrategy.Sequential });
        Assert.Equal(StopReasons.Cancelled, (await agent.InvokeAsync("stop")).StopReason);
        var results = Results(agent);
        Assert.Equal(ToolResultStatus.Success, Assert.Single(results, value => value.ToolUseId == "stop-use").Status);
        var failed = Assert.Single(results, value => value.ToolUseId == "bad-use");
        Assert.Equal(ToolResultStatus.Error, failed.Status);
        Assert.Contains("ordinary tool error", failed.Content);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task StatefulSaveAndFreshResumeDoNotRepeatBodyOrCursor()
    {
        var store = new Store(); var effects = 0; var helper = Stop.Create();
        var wrapped = new FunctionTool(helper.Spec, (input, context, token) => { effects++; return helper.InvokeAsync(input, context, token); });
        var firstModel = new StatefulModel(Stateful(Call(("stop-use", "stop", "{}")), "receipt"));
        await using (var first = new Agent(new() { Model = firstModel, Tools = [wrapped], SessionStore = store, SessionId = "stop-stateful" }))
        {
            Assert.Equal(StopReasons.Cancelled, (await first.InvokeAsync("stop")).StopReason);
            Assert.Equal("receipt", first.ModelState.Get<string>("responseId"));
        }
        Assert.Equal(1, effects);
        Assert.Equal(2, store.Snapshot!.ModelHistoryCount);
        var secondModel = new StatefulModel(Stateful(Text("acknowledged"), "next"));
        await using var second = new Agent(new() { Model = secondModel, Tools = [wrapped], SessionStore = store, SessionId = "stop-stateful" });
        Assert.Equal("acknowledged", (await second.InvokeAsync(null)).Message!.Text);
        var request = Assert.Single(secondModel.Requests);
        Assert.Equal("receipt", request.ModelState!["responseId"].GetString());
        var output = Assert.Single(request.Messages.SelectMany(value => value.Content).OfType<ToolResultBlock>());
        Assert.Equal("stop-use", output.ToolUseId);
        Assert.Equal(Stop.DefaultMessage, output.Content);
        Assert.DoesNotContain(request.Messages.SelectMany(value => value.Content), value => value is ToolUseBlock);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task DirtySaveFailureAndRetryKeepPrimaryWithoutStopReplay()
    {
        var primary = new IOException("accepted Stop output save failed"); var failed = false; var effects = 0;
        var store = new Store { BeforeSave = snapshot => { if (!failed && snapshot.Messages.SelectMany(value => value.Content).OfType<ToolResultBlock>().Any(value => value.ToolUseId == "stop-use")) { failed = true; throw primary; } } };
        var helper = Stop.Create();
        var wrapped = new FunctionTool(helper.Spec, (input, context, token) => { effects++; return helper.InvokeAsync(input, context, token); });
        var model = new Model(Call(("stop-use", "stop", "{}")), Text("later normal response"));
        await using var agent = new Agent(new() { Model = model, Tools = [wrapped], SessionStore = store, SessionId = "stop-dirty" });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("stop")));
        Assert.True(failed);
        Assert.Equal(1, effects);
        Assert.Equal(Stop.DefaultMessage, Assert.Single(store.Snapshot!.Messages.SelectMany(value => value.Content).OfType<ToolResultBlock>()).Content);
        Assert.Equal("later normal response", (await agent.InvokeAsync(null)).Message!.Text);
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Calls);
    }

    private static async Task AssertInvalidNeverStops(string[] inputs)
    {
        var checkedInputs = 0; var helper = Stop.Create(new() { MaximumMessageLength = 1 });
        var probe = Tool("probe", async (_, context, token) => { foreach (var input in inputs) { await Assert.ThrowsAnyAsync<ArgumentException>(async () => await helper.InvokeAsync(Json(input), context, token)); checkedInputs++; } return "no stop requested"; });
        var model = new Model(Call(("probe-use", "probe", "{}")), Text("normal completion"));
        await using var agent = new Agent(new() { Model = model, Tools = [probe] });
        var result = await agent.InvokeAsync("probe");
        Assert.Equal(inputs.Length, checkedInputs);
        Assert.Equal("normal completion", result.Message!.Text);
        Assert.Equal(2, model.Calls);
        Assert.Equal(ToolResultStatus.Success, Assert.Single(Results(agent)).Status);
    }

    private static async Task AssertCancelled(bool operationCancelled, bool capturedCancelled)
    {
        using var operation = new CancellationTokenSource(); using var captured = new CancellationTokenSource();
        if (operationCancelled) operation.Cancel();
        if (capturedCancelled) captured.Cancel();
        await using var agent = NewAgent();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Stop.Create().InvokeAsync(Json("{}"), Context(agent) with { ExecutionCancelSignal = captured.Token }, operation.Token));
        Assert.Equal(operationCancelled ? operation.Token : captured.Token, error.CancellationToken);
        Assert.Equal("normal", (await agent.InvokeAsync("normal")).Message!.Text);
    }

    private static Agent NewAgent() => new(new() { Model = new Model(Text("normal")) });
    private static ToolContext Context(Agent agent) => new(agent, new("direct-use", "stop", Json("{}")), new Dictionary<string, object?>());
    private static ToolResultBlock[] Results(Agent agent) => agent.Messages.SelectMany(value => value.Content).OfType<ToolResultBlock>().ToArray();
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback) => new(new(name, name, Json("{\"type\":\"object\"}")), callback);
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }
    private static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static ModelStreamEvent[] Call(params (string Id, string Name, string Json)[] calls)
    {
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        for (var index = 0; index < calls.Length; index++) { var call = calls[index]; events.Add(new ModelContentBlockStartEvent(index, new(call.Id, call.Name, Json("{}")))); events.Add(new ModelContentBlockDeltaEvent(index, ToolInput: call.Json)); events.Add(new ModelContentBlockStopEvent(index)); }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse)); return events.ToArray();
    }
    private static ModelStreamEvent[] Stateful(ModelStreamEvent[] events, string id) { events[events.Length - 1] = new ModelMessageStopEvent(events[events.Length - 1] is ModelMessageStopEvent value ? value.StopReason : StopReasons.EndTurn) { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement(id) } }; return events; }
    private sealed class Model(params ModelStreamEvent[][] turns) : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var selected = turns[Calls++]; foreach (var item in selected) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); } }
    }
    private sealed class StatefulModel(params ModelStreamEvent[][] turns) : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var selected = turns[Requests.Count]; Requests.Add(request); foreach (var item in selected) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); } }
    }
    private sealed class Store : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; private set; }
        internal Action<SessionSnapshot>? BeforeSave { get; init; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { BeforeSave?.Invoke(snapshot); Snapshot = snapshot; return default; }
    }
}
