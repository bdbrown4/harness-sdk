using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.MultiAgent;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphSnapshotTests
{
    [Fact]
    public async Task AcceptedHookAnswerIsSavedBeforeGraphCallbacksAndRestoresAfterTheirFailure()
    {
        var callbacks = 0;
        var effects = 0;
        var failure = new IOException("graph callback");
        var repository = new StorageMultiAgentSessionStore(new InMemoryStorage());
        Graph Create(bool fail)
        {
            var hooks = new MultiAgentHookRegistry();
            hooks.Register<BeforeNodeCallEvent>((value, _) => { Assert.True(value.Interrupt("gate").GetBoolean()); return default; });
            if (fail) hooks.Register<BeforeMultiAgentInvocationEvent>((_, _) => { if (++callbacks == 2) throw failure; return default; });
            return new(new() { Id = "graph", SessionId = "session", SessionStore = repository, Hooks = hooks, Nodes = [new FunctionNode("node", (_, _, _) => { effects++; return Output("done"); })] });
        }
        var first = Create(true);
        var paused = await first.InvokeAsync("task");

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]))));

        Assert.True(first.CanResume);
        Assert.Empty(first.PendingInterrupts);
        var result = await Create(false).InvokeAsync(MultiAgentInput.Resume([]));
        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, effects);
    }

    [Fact]
    public async Task AcceptedAnswerAndFailedResumeSurviveAJsonCheckpointIntoAFreshGraph()
    {
        var callbacks = 0;
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((_, _) => { if (++callbacks == 2) throw new IOException("resume callback"); return default; });
        var effects = 0;
        var tool = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { Assert.True(context.Interrupt("approval").GetBoolean()); effects++; return new("accepted"); });
        await using var firstAgent = new Agent(new() { Id = "agent", Model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}"))), Hooks = hooks, Tools = [tool] });
        await using var nextAgent = new Agent(new() { Id = "agent", Model = new AgentTests.ScriptedModel(AgentTests.Text("done")), Tools = [tool] });
        Graph Create(Agent agent) => new(new() { Id = "graph", Nodes = [new AgentNode(agent)] });
        var first = Create(firstAgent);
        var paused = await first.InvokeAsync("task");
        Assert.Equal(Status.Failed, (await first.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]))).Status);
        var checkpoint = JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(await first.TakeSnapshotAsync()))!;
        var next = Create(nextAgent);

        await next.LoadSnapshotAsync(checkpoint);
        var completed = await next.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, completed.Status);
        Assert.Equal("done", completed.ToString());
        Assert.Equal(1, effects);
        Assert.Empty(nextAgent.Messages);
    }

    [Fact]
    public async Task DuplicatePendingResultsAreRejectedBeforeReplacingLiveStateOrLoadingBorrowedAgents()
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}")));
        var tool = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var agent = new Agent(new() { Id = "agent", Model = model, Tools = [tool] });
        var graph = new Graph(new() { Nodes = [new AgentNode(agent)] });
        await graph.InvokeAsync("task");
        var originalState = graph.State;
        var snapshot = await graph.TakeSnapshotAsync();
        var data = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        var state = JsonNode.Parse(data["state"].GetRawText())!;
        var results = state["results"]!.AsArray();
        results.Add(results[0]!.DeepClone());
        state["steps"] = 2;
        data["state"] = JsonSerializer.SerializeToElement(state);

        await Assert.ThrowsAsync<InvalidDataException>(() => graph.LoadSnapshotAsync(new(data)).AsTask());

        Assert.Same(originalState, graph.State);
        Assert.Empty(agent.Messages);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task FreshNestedGraphsRehydrateTheirOwnCapturedPendingExecutions()
    {
        var firstModel = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}")));
        var nextModel = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var tool = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var firstAgent = new Agent(new() { Id = "agent", Model = firstModel, Tools = [tool] });
        await using var nextAgent = new Agent(new() { Id = "agent", Model = nextModel, Tools = [tool] });
        Graph Create(Agent agent) => new(new() { Id = "outer", Nodes = [new MultiAgentNode(new Graph(new() { Id = "inner", Nodes = [new AgentNode(agent)] }), "nested")] });
        var first = Create(firstAgent);
        var paused = await first.InvokeAsync("task");
        var checkpoint = JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(await first.TakeSnapshotAsync()))!;
        var next = Create(nextAgent);
        await next.LoadSnapshotAsync(checkpoint);

        var result = await next.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("done", result.ToString());
        Assert.Single(nextModel.Requests);
        Assert.Empty(nextAgent.Messages);
    }

    [Fact]
    public async Task SessionRepositoryAutomaticallyRestoresThePausedGraphBeforeRoutingResponses()
    {
        var storage = new InMemoryStorage();
        var repository = new StorageMultiAgentSessionStore(storage);
        var initialModel = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}")));
        var nextModel = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var effects = 0;
        var tool = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { Assert.True(context.Interrupt("approval").GetBoolean()); effects++; return new("accepted"); });
        await using var firstAgent = new Agent(new() { Id = "agent", Model = initialModel, Tools = [tool] });
        await using var nextAgent = new Agent(new() { Id = "agent", Model = nextModel, Tools = [tool] });
        Graph Create(Agent agent) => new(new() { Id = "graph", Nodes = [new AgentNode(agent)], SessionId = "opaque/session/../id", SessionStore = repository });

        var paused = await Create(firstAgent).InvokeAsync("task");
        Assert.Single(await storage.ListAsync());
        var result = await Create(nextAgent).InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, effects);
        Assert.Single(nextModel.Requests);
    }

    [Fact]
    public async Task JsonRoundTripRestoresPausedAgentIntoAFreshGraphWithoutRepeatingCompletedNodes()
    {
        var effects = 0;
        var approvals = 0;
        var initialModel = new AgentTests.ScriptedModel(AgentTests.Call(("call", "approve", "{}")));
        var resumedModel = new AgentTests.ScriptedModel(AgentTests.Text("done"));
        var tool = new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { Assert.True(context.Interrupt("approval").GetBoolean()); approvals++; return new("accepted"); });
        await using var firstAgent = new Agent(new() { Id = "agent", Model = initialModel, Tools = [tool] });
        await using var nextAgent = new Agent(new() { Id = "agent", Model = resumedModel, Tools = [tool] });
        Graph Create(Agent agent) => new(new()
        {
            Id = "graph",
            Nodes = [new FunctionNode("sibling", (_, _, _) => { effects++; return Output("saved"); }), new AgentNode(agent)]
        });
        var first = Create(firstAgent);
        var paused = await first.InvokeAsync(new MultiAgentInput([new JsonBlock(JsonSerializer.SerializeToElement(new { value = 42 }))]));
        var checkpoint = await first.TakeSnapshotAsync();
        var detached = JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(checkpoint))!;
        var restored = Create(nextAgent);

        await restored.LoadSnapshotAsync(detached);
        Assert.Empty(nextAgent.Messages);
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(1, effects);
        Assert.Equal(1, approvals);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(42, Assert.IsType<JsonBlock>(Assert.Single(resumedModel.Requests).Messages[0].Content[0]).Json.GetProperty("value").GetInt32());
        Assert.Empty(nextAgent.Messages);
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(string value)
    { yield return new NodeResultUpdateEvent([new TextBlock(value)]); await Task.CompletedTask; }
}
