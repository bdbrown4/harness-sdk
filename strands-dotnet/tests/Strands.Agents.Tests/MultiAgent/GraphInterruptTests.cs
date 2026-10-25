using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphInterruptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswersForQueuedNodesRemainAcceptedWhenAnEarlierNodePausesAgain(bool nested)
    {
        var effects = 0;
        FunctionTool Tool(bool secondary) => new(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            Assert.True(context.Interrupt("approval").GetBoolean());
            if (secondary) Assert.True(context.Interrupt("secondary").GetBoolean());
            effects++;
            return new("accepted");
        });
        await using var left = new Agent(new() { Id = "left", Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}")), AgentTests.Text("left done")), Tools = [Tool(true)] });
        await using var right = new Agent(new() { Id = "right", Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}")), AgentTests.Text("right done")), Tools = [Tool(false)] });
        Node Wrap(Agent agent) => nested ? new MultiAgentNode(new Graph(new() { Id = "inner-" + agent.Id, Nodes = [new AgentNode(agent)] }), agent.Id) : new AgentNode(agent);
        Graph Create(int concurrency) => new(new() { Id = "graph", Nodes = [Wrap(left), Wrap(right)], MaxConcurrency = concurrency });
        var first = Create(2);
        var paused = await first.InvokeAsync("task");
        Assert.Equal(2, paused.Interrupts.Count);
        var serial = Create(1);
        await serial.LoadSnapshotAsync(await first.TakeSnapshotAsync());

        var partial = await serial.InvokeAsync(MultiAgentInput.Resume(paused.Interrupts.Select(question => new InterruptResponse(question.Id, JsonSerializer.SerializeToElement(true))).ToArray()));

        Assert.Equal(Status.Interrupted, partial.Status);
        Assert.Equal("secondary", Assert.Single(serial.PendingInterrupts).Name);
        var restored = Create(1);
        await restored.LoadSnapshotAsync(JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(await serial.TakeSnapshotAsync()))!);
        var complete = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(serial.PendingInterrupts).Id, JsonSerializer.SerializeToElement(true))]));
        Assert.Equal(Status.Completed, complete.Status);
        Assert.Equal(2, effects);
    }

    [Fact]
    public async Task FreshParentDoesNotAdoptAnUnrelatedPauseFromABorrowedNestedGraph()
    {
        var tool = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var agent = new Agent(new() { Id = "agent", Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}"))), Tools = [tool] });
        var inner = new Graph(new() { Id = "inner", Nodes = [new AgentNode(agent)] });
        var paused = await inner.InvokeAsync("original task");
        var originalState = inner.State;
        var outer = new Graph(new() { Id = "outer", Nodes = [new MultiAgentNode(inner)] });

        var result = await outer.InvokeAsync("unrelated task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.False(outer.CanResume);
        Assert.Empty(outer.PendingInterrupts);
        Assert.True(inner.CanResume);
        Assert.Same(originalState, inner.State);
        Assert.Equal(Assert.Single(paused.Interrupts).Id, Assert.Single(inner.PendingInterrupts).Id);
    }

    [Fact]
    public async Task BeforeNodeQuestionsAggregateAndResumeTheirTypedTaskAcrossFreshCheckpoints()
    {
        var effects = 0;
        BeforeNodeCallEvent? stale = null;
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { stale = value; Assert.True(value.Interrupt("approval").GetBoolean()); return default; });
        hooks.Register<BeforeNodeCallEvent>((value, _) => { Assert.Equal("allowed", value.Interrupt("compliance").GetString()); return default; });
        Graph Create() => new(new()
        {
            Id = "graph",
            Hooks = hooks,
            Nodes = [new FunctionNode("node", (input, _, _) =>
        { Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single(input.Content)).Json.GetInt32()); effects++; return Output("done"); })]
        });
        var first = Create();

        var paused = await first.InvokeAsync(new MultiAgentInput([new JsonBlock(JsonSerializer.SerializeToElement(42))]));

        Assert.Equal(Status.Interrupted, paused.Status);
        Assert.Equal(2, paused.Interrupts.Count);
        Assert.Equal(0, effects);
        Assert.Throws<InvalidOperationException>(() => stale!.Interrupt("stale"));
        var next = Create();
        await next.LoadSnapshotAsync(JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(await first.TakeSnapshotAsync()))!);
        var partial = await next.InvokeAsync(MultiAgentInput.Resume([new(paused.Interrupts.Single(value => value.Name == "approval").Id, JsonSerializer.SerializeToElement(true))]));
        Assert.Equal(Status.Interrupted, partial.Status);
        Assert.Equal("compliance", Assert.Single(partial.Interrupts).Name);
        var completed = await next.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(partial.Interrupts).Id, JsonSerializer.SerializeToElement("allowed"))]));
        Assert.Equal(Status.Completed, completed.Status);
        Assert.Equal(1, effects);
        Assert.Single(completed.Results);
    }

    [Fact]
    public async Task AcceptedAnswerSurvivesAResumeCallbackFailureAndCanBeRetriedWithoutAnotherAnswer()
    {
        var calls = 0;
        var effects = 0;
        var failure = new IOException("callback failed after accepting the answer");
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((_, _) => { if (++calls == 2) throw failure; return default; });
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}")), AgentTests.Text("done"));
        var tool = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { Assert.True(context.Interrupt("approval").GetBoolean()); effects++; return new("accepted"); });
        await using var agent = new Agent(new() { Id = "agent", Model = model, Hooks = hooks, Tools = [tool] });
        var graph = new Graph(new() { Nodes = [new AgentNode(agent)] });
        var paused = await graph.InvokeAsync("task");

        var failed = await graph.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Failed, failed.Status);
        Assert.Same(failure, Assert.Single(failed.Results).Error);
        Assert.Empty(agent.Messages);
        var completed = await graph.InvokeAsync(MultiAgentInput.Resume([]));
        Assert.Equal(Status.Completed, completed.Status);
        Assert.Equal("done", completed.ToString());
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task NestedResponsesKeepQuestionIdentityAndReachOnlyTheOriginalInnerAgent()
    {
        var caller = new Dictionary<string, object?>();
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("approval", "approve", "{}")), AgentTests.Text("done", new(2, 3)));
        var tool = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { Assert.Same(caller, context.InvocationState); return new(context.Interrupt("approval").ToString()); });
        await using var agent = new Agent(new() { Id = "agent", Model = model, Tools = [tool] });
        var inner = new Graph(new() { Id = "inner", Nodes = [new AgentNode(agent)] });
        var outer = new Graph(new() { Id = "outer", Nodes = [new MultiAgentNode(inner, "nested")] });

        var paused = await outer.InvokeAsync("task", new() { InvocationState = caller });

        Assert.Equal(Status.Interrupted, paused.Status);
        var question = Assert.Single(paused.Interrupts);
        Assert.Equal(Assert.Single(inner.State!.Results).Interrupts[0].Id, question.Id);
        var result = await outer.InvokeAsync(MultiAgentInput.Resume([new(question.Id, JsonSerializer.SerializeToElement(true))]), new() { InvocationState = caller });

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("done", result.ToString());
        Assert.Equal(new Usage(2, 3), result.Usage);
        Assert.Single(result.Results);
        Assert.Empty(caller);
    }

    [Fact]
    public async Task ResumeRetainsUnstartedSourcesAndOriginalTypedInputAlongsideReadySuccessors()
    {
        var siblingEffects = 0;
        var successors = 0;
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("approval", "approve", "{}")), AgentTests.Text("approved"));
        var tool = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var agent = new Agent(new() { Id = "agent", Model = model, Tools = [tool] });
        var graph = new Graph(new()
        {
            Nodes = [new AgentNode(agent), new FunctionNode("sibling", (input, _, _) =>
            {
                Assert.Equal(42, Assert.IsType<JsonBlock>(input.Content[0]).Json.GetProperty("value").GetInt32());
                siblingEffects++; return Output("saved");
            }), new FunctionNode("successor", (_, _, _) => { successors++; return Output("done"); })],
            Edges = [new("agent", "successor")],
            MaxConcurrency = 1
        });
        MultiAgentInput input;
        using (var document = JsonDocument.Parse("{\"value\":42}")) input = new([new JsonBlock(document.RootElement)]);

        var paused = await graph.InvokeAsync(input);
        Assert.Equal(0, siblingEffects);
        var result = await graph.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, siblingEffects);
        Assert.Equal(1, successors);
        Assert.Equal(3, result.Results.Count);
    }

    [Fact]
    public async Task AgentPauseResumesItsPendingCallWithoutRepeatingACompletedIndependentSibling()
    {
        var siblingEffects = 0;
        var approvedEffects = 0;
        var caller = new Dictionary<string, object?> { ["request"] = 42 };
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("approval", "approve", "{}")), AgentTests.Text("approved"));
        var tool = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        {
            Assert.Same(caller, context.InvocationState);
            Assert.True(context.Interrupt("approval").GetBoolean());
            approvedEffects++;
            return new("accepted");
        });
        await using var agent = new Agent(new() { Id = "agent", Model = model, Tools = [tool] });
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("sibling", (_, _, _) => { siblingEffects++; return Output("saved"); }), new AgentNode(agent)]
        });

        var paused = await graph.InvokeAsync("task", new() { InvocationState = caller });

        Assert.Equal(Status.Interrupted, paused.Status);
        Assert.Empty(agent.Messages);
        var question = Assert.Single(graph.State!.Nodes["agent"].Results.Last().Interrupts);
        var resumed = await graph.InvokeAsync(MultiAgentInput.Resume([new(question.Id, JsonSerializer.SerializeToElement(true))]), new() { InvocationState = caller });

        Assert.Equal(Status.Completed, resumed.Status);
        Assert.Equal(1, siblingEffects);
        Assert.Equal(1, approvedEffects);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, resumed.Results.Count);
        Assert.Empty(agent.Messages);
        Assert.Single(caller);
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(string value)
    { yield return new NodeResultUpdateEvent([new TextBlock(value)]); await Task.CompletedTask; }
}
