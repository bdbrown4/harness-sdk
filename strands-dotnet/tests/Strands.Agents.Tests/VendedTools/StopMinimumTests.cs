using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.VendedTools;

namespace Strands.Agents.Tests.VendedTools;

public sealed class StopMinimumTests
{
    [Fact]
    public void FactoryMetadataAndCustomCapAreInert()
    {
        var tool = Stop.Create();
        Assert.Equal("stop", tool.Spec.Name);
        Assert.Equal(Stop.DefaultDescription, tool.Spec.Description);
        Assert.Equal("Agent loop stopped.", Stop.DefaultMessage);
        var schema = tool.Spec.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
        var message = Assert.Single(schema.GetProperty("properties").EnumerateObject());
        Assert.Equal("message", message.Name);
        Assert.Equal(new[] { "string", "null" }, message.Value.GetProperty("type").EnumerateArray().Select(x => x.GetString()));
        Assert.False(message.Value.TryGetProperty("maxLength", out _));
        var custom = Stop.Create(new() { Name = "finish", Description = "Selected description.", MaximumMessageLength = 1, Behavior = StopBehavior.TypeScriptEndTurn });
        Assert.Equal("finish", custom.Spec.Name);
        Assert.Equal("Selected description.", custom.Spec.Description);
        Assert.Equal(1, custom.Spec.InputSchema.GetProperty("properties").GetProperty("message").GetProperty("maxLength").GetInt32());
        Assert.Throws<ArgumentOutOfRangeException>(() => Stop.Create(new() { MaximumMessageLength = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stop.Create(new() { MaximumMessageLength = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stop.Create(new() { Behavior = (StopBehavior)99 }));
        Assert.False(tool is IDisposable);
        Assert.False(tool is IAsyncDisposable);
    }

    [Fact]
    public async Task PythonDeferredStopJoinsGatedSequentialSibling()
    {
        var model = new ScriptedModel(Call(("stop-use", "stop", "{}"), ("peer-use", "peer", "{}")), Text("unwanted second turn"));
        var entered = Gate(); var release = Gate(); var effects = 0; var peerCancelled = false;
        var peer = Tool("peer", async (_, context, token) =>
        {
            peerCancelled = context.CancelSignal.IsCancellationRequested || token.IsCancellationRequested;
            entered.TrySetResult(true);
            await release.Task;
            effects++;
            return "peer completed";
        });
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create(), peer], ToolExecution = ToolExecutionStrategy.Sequential });
        var operation = agent.InvokeAsync("finish the accepted batch");
        try
        {
            await Wait(entered.Task);
            Assert.False(operation.IsCompleted);
            Assert.False(peerCancelled);
        }
        finally { release.TrySetResult(true); await Wait(operation); }
        var result = await operation;
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(Stop.DefaultMessage, result.Message!.Text);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, effects);
        var stopResult = Assert.Single(agent.Messages.SelectMany(x => x.Content).OfType<ToolResultBlock>(), x => x.ToolUseId == "stop-use");
        Assert.Equal(ToolResultStatus.Success, stopResult.Status);
        Assert.Equal(Stop.DefaultMessage, stopResult.Content);
    }

    [Fact]
    public async Task PythonNullEmptyAndAbsentReturnDefaultWithoutRecapping()
    {
        foreach (var input in new[] { "{}", "{\"message\":null}", "{\"message\":\"\"}" })
        {
            await using var agent = new Agent(new() { Model = new ScriptedModel(Text("not invoked")) });
            var tool = Stop.Create(new() { MaximumMessageLength = 1 });
            Assert.Equal(Stop.DefaultMessage, await tool.InvokeAsync(Json(input), Context(agent)));
        }
        await using var unicode = new Agent(new() { Model = new ScriptedModel(Text("not invoked")) });
        Assert.Equal("😀", await Stop.Create(new() { MaximumMessageLength = 1 }).InvokeAsync(Json("{\"message\":\"😀\"}"), Context(unicode)));
        Assert.Equal(" ", await Stop.Create(new() { MaximumMessageLength = 1 }).InvokeAsync(Json("{\"message\":\" \"}"), Context(unicode)));
    }

    [Fact]
    public async Task TypeScriptAfterToolsEndsAfterGatedSibling()
    {
        var state = new Dictionary<string, object?> { ["caller"] = new object() };
        var model = new ScriptedModel(Call(("stop-use", "finish", "{\"message\":\"  completed\\n\"}"), ("peer-use", "peer", "{}")), Text("unwanted second turn"));
        var entered = Gate(); var release = Gate(); var effects = 0;
        var peer = Tool("peer", async (_, context, token) =>
        {
            Assert.False(context.CancelSignal.IsCancellationRequested);
            Assert.False(token.IsCancellationRequested);
            entered.TrySetResult(true);
            await release.Task;
            effects++;
            return "peer completed";
        });
        await using var agent = new Agent(new() { Model = model, Tools = [Stop.Create(new() { Name = "finish", Behavior = StopBehavior.TypeScriptEndTurn }), peer], ToolExecution = ToolExecutionStrategy.Sequential });
        var operation = agent.InvokeAsync("finish", new() { InvocationState = state });
        try { await Wait(entered.Task); Assert.False(operation.IsCompleted); }
        finally { release.TrySetResult(true); await Wait(operation); }
        var result = await operation;
        Assert.Equal("  completed\n", result.Message!.Text);
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, effects);
        Assert.Single(state);
        Assert.Same(state, result.InvocationState);
        Assert.False(agent.CancelSignal.IsCancellationRequested);
        Assert.Equal("  completed\n", Assert.Single(agent.Messages.SelectMany(x => x.Content).OfType<ToolResultBlock>(), x => x.ToolUseId == "stop-use").Content);
    }

    [Fact]
    public async Task TypeScriptReuseIsNotStickyAcrossAgentsOrInvocations()
    {
        var helper = Stop.Create(new() { Behavior = StopBehavior.TypeScriptEndTurn });
        var state = new Dictionary<string, object?> { ["caller"] = 7 };
        var hooks = new HookRegistry();
        var firstModel = new ScriptedModel(Call(("stop-use", "stop", "{\"message\":\"first complete\"}")), Text("later normal response"), Text("third response"));
        await using var first = new Agent(new() { Model = firstModel, Tools = [helper], Hooks = hooks });
        var stopped = await first.InvokeAsync("stop", new() { InvocationState = state });
        Assert.Equal("first complete", stopped.Message!.Text);
        Assert.Equal(1, firstModel.Calls);
        var later = await first.InvokeAsync("normal", new() { InvocationState = state });
        Assert.Equal("later normal response", later.Message!.Text);
        Assert.Equal(2, firstModel.Calls);
        var secondModel = new ScriptedModel(Text("other agent normal response"), Call(("second-use", "stop", "{\"message\":\"second complete\"}")), Text("unwanted response"));
        await using var second = new Agent(new() { Model = secondModel, Tools = [helper], Hooks = hooks });
        Assert.Equal("other agent normal response", (await second.InvokeAsync("normal", new() { InvocationState = state })).Message!.Text);
        Assert.Equal("second complete", (await second.InvokeAsync("stop", new() { InvocationState = state })).Message!.Text);
        Assert.Equal(2, secondModel.Calls);
        Assert.Equal("third response", (await first.InvokeAsync("normal", new() { InvocationState = state })).Message!.Text);
        Assert.Single(state);
    }

    [Fact]
    public async Task InvalidInputMakesNoStopRequest()
    {
        var invalid = new[] { "null", "[]", "1", "{\"message\":false}", "{\"message\":12}", "{\"message\":{}}", "{\"message\":\"ab\"}" };
        var validated = 0;
        var probe = Tool("probe", async (_, context, token) =>
        {
            foreach (var behavior in new[] { StopBehavior.PythonCancellation, StopBehavior.TypeScriptEndTurn })
            {
                var helper = Stop.Create(new() { MaximumMessageLength = 1, Behavior = behavior });
                foreach (var input in invalid)
                {
                    await Assert.ThrowsAnyAsync<ArgumentException>(async () => await helper.InvokeAsync(Json(input), context, token));
                    validated++;
                }
            }
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Stop.Create(new() { MaximumMessageLength = 1, Behavior = StopBehavior.TypeScriptEndTurn }).InvokeAsync(Json("{\"message\":\"😀\"}"), context, token));
            validated++;
            return "validated invalid requests";
        });
        var model = new ScriptedModel(Call(("probe-use", "probe", "{}")), Text("normal completion"));
        await using var agent = new Agent(new() { Model = model, Tools = [probe] });
        var result = await agent.InvokeAsync("probe");
        Assert.Equal(15, validated);
        Assert.Equal("normal completion", result.Message!.Text);
        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Equal(2, model.Calls);
        Assert.Equal(ToolResultStatus.Success, Assert.Single(agent.Messages.SelectMany(x => x.Content).OfType<ToolResultBlock>()).Status);
    }

    [Fact]
    public async Task OriginalCancelledOperationWinsBeforeEffects()
    {
        foreach (var behavior in new[] { StopBehavior.PythonCancellation, StopBehavior.TypeScriptEndTurn })
            foreach (var captured in new[] { false, true })
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var model = new ScriptedModel(Text("normal completion"));
                await using var agent = new Agent(new() { Model = model });
                var context = Context(agent) with { ExecutionCancelSignal = captured ? cancellation.Token : CancellationToken.None };
                var helper = Stop.Create(new() { Behavior = behavior });
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await helper.InvokeAsync(Json("{}"), context, captured ? default : cancellation.Token));
                Assert.Equal(cancellation.Token, error.CancellationToken);
                var after = new AfterToolsEvent(agent, [], context.InvocationState);
                await agent.Hooks.InvokeAsync(after);
                Assert.Null(after.EndTurn);
                Assert.False(agent.CancelSignal.IsCancellationRequested);
                Assert.Equal("normal completion", (await agent.InvokeAsync("normal")).Message!.Text);
            }
    }

    [Fact]
    public async Task ExistingStatefulAndDelegationGuardsRemainControls()
    {
        var output = Call(("ordinary-use", "work", "{}"));
        output[output.Length - 1] = new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = Json("\"provider-receipt\"") } };
        var stateful = new StatefulModel(output);
        await using (var agent = new Agent(new() { Model = stateful, Tools = [Tool("work", (_, _, _) => new("accepted"))] }))
        {
            agent.Hooks.Register<AfterToolsEvent>((value, _) => { value.EndTurn = [new TextBlock("must be refused")]; return default; });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("go"));
            Assert.Contains("server-side tool acknowledgement", error.Message);
            Assert.Equal("provider-receipt", agent.ModelState.Get<string>("responseId"));
            Assert.Single(stateful.Requests);
            Assert.DoesNotContain(agent.Messages, x => x.Text == "must be refused");
            Assert.Equal("accepted", Assert.Single(agent.Messages.SelectMany(x => x.Content).OfType<ToolResultBlock>()).Content);
        }
        var childModel = new ScriptedModel(Text("must not run"));
        await using var child = new Agent(new() { Model = childModel });
        var delegated = child.AsTool(new AgentToolOptions { Name = "specialist", Delegate = true });
        var effects = 0;
        var parentModel = new ScriptedModel(Call(("delegate-use", "specialist", "{\"input\":\"go\"}"), ("ordinary-use", "ordinary", "{}")), Text("parent fallback"));
        await using var parent = new Agent(new() { Model = parentModel, Tools = [delegated, Tool("ordinary", (_, _, _) => { effects++; return new("must not run"); })] });
        Assert.Equal("parent fallback", (await parent.InvokeAsync("mixed batch")).Message!.Text);
        Assert.Equal(0, effects);
        Assert.Equal(0, childModel.Calls);
        var denied = parent.Messages.SelectMany(x => x.Content).OfType<ToolResultBlock>().ToArray();
        Assert.Equal(2, denied.Length);
        Assert.All(denied, x => Assert.Equal(ToolResultStatus.Error, x.Status));
    }

    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static ToolContext Context(Agent agent) => new(agent, new("direct-use", "stop", Json("{}")), new Dictionary<string, object?>());
    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> callback) => new(new(name, name, Json("{\"type\":\"object\"}")), callback);
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(10_000))); await task; }

    private static ModelStreamEvent[] Text(string text) => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelContentBlockDeltaEvent(0, Text: text), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)];
    private static ModelStreamEvent[] Call(params (string Id, string Name, string Json)[] calls)
    {
        var events = new List<ModelStreamEvent> { new ModelMessageStartEvent() };
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            events.Add(new ModelContentBlockStartEvent(index, new(call.Id, call.Name, Json("{}"))));
            events.Add(new ModelContentBlockDeltaEvent(index, ToolInput: call.Json));
            events.Add(new ModelContentBlockStopEvent(index));
        }
        events.Add(new ModelMessageStopEvent(StopReasons.ToolUse));
        return events.ToArray();
    }
    private sealed class ScriptedModel(params ModelStreamEvent[][] turns) : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var selected = turns[Calls++];
            foreach (var item in selected) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }
    }
    private sealed class StatefulModel(ModelStreamEvent[] events) : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            foreach (var item in events) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
        }
    }
}
