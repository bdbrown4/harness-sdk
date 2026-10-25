using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class GraphTests
{
    [Theory]
    [InlineData(GraphInputFormat.Typed)]
    [InlineData(GraphInputFormat.Python)]
    public async Task DependencyProjectionConvertsVisibleProviderTextAndOmitsOpaqueServerReplay(GraphInputFormat format)
    {
        MultiAgentInput? received = null;
        var first = new FunctionNode("first", (_, _, _) => Output([
            new Strands.Agents.Models.AnthropicContentBlock(JsonSerializer.SerializeToElement(new { type = "text", text = "native" })),
            new Strands.Agents.Models.AnthropicContentBlock(JsonSerializer.SerializeToElement(new { type = "server_tool_use", id = "server", name = "web_search", input = new { query = "private" } })),
            new CitationsBlock([new Citation(new WebCitationLocation("https://example.com"))], [new CitationText("cited")]),
            new JsonBlock(JsonSerializer.SerializeToElement(new { value = 42 }))]));
        var second = new FunctionNode("second", (input, _, _) => { received = input; return Output([new TextBlock("done")]); });
        var graph = new Graph(new() { Nodes = [first, second], Edges = [new("first", "second")], InputFormat = format });

        await graph.InvokeAsync("task");

        Assert.NotNull(received);
        Assert.DoesNotContain(received.Content, block => block is Strands.Agents.Models.AnthropicContentBlock or CitationsBlock);
        if (format == GraphInputFormat.Typed)
        {
            Assert.Equal(["task", "[node: first]", "native", "cited"], received.Content.OfType<TextBlock>().Select(block => block.Text));
            Assert.Equal(42, Assert.Single(received.Content.OfType<JsonBlock>()).Json.GetProperty("value").GetInt32());
        }
        else Assert.Equal("  - Agent: nativecited", Assert.IsType<TextBlock>(received.Content.Last()).Text);
    }

    [Theory]
    [InlineData(GraphScheduling.ReadyNodes)]
    [InlineData(GraphScheduling.CompletedBatches)]
    public async Task DiamondJoinsBothPredecessorsOnceAndPreservesTheirDeclaredOrder(GraphScheduling scheduling)
    {
        var joinCalls = 0;
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("source", (_, _, _) => Output([new TextBlock("source")])),
                new FunctionNode("left", (_, _, _) => Output([new TextBlock("left")])),
                new FunctionNode("right", (_, _, _) => Output([new TextBlock("right")])),
                new FunctionNode("join", (input, _, _) =>
                {
                    Interlocked.Increment(ref joinCalls);
                    Assert.Equal(["task", "[node: left]", "left", "[node: right]", "right"], input.Content.OfType<TextBlock>().Select(block => block.Text));
                    return Output([new TextBlock("joined")]);
                })],
            Edges = [new("source", "left"), new("source", "right"), new("left", "join"), new("right", "join")],
            Scheduling = scheduling
        });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(4, result.Results.Count);
        Assert.Equal(1, joinCalls);
        Assert.Equal("joined", result.ToString());
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(true, 4, 3)]
    public async Task BuilderCompatibilityProfileControlsBorrowedAgentStateAcrossRevisits(bool python, int messages, int secondInputMessages)
    {
        var model = new AgentTests.ScriptedModel(AgentTests.Text("first"), AgentTests.Text("second"));
        await using var agent = new Agent(new() { Model = model });
        var builder = new GraphBuilder(python ? GraphOptions.Python : null);
        builder.AddNode(agent, "agent");
        builder.SetEntryPoint("agent").AddEdge("agent", "agent", (state, _, _) => new(state.Steps < 2));

        var result = await builder.Build().InvokeAsync("task");

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(messages, agent.Messages.Count);
        Assert.Equal(secondInputMessages, model.Requests[1].Messages.Count);
    }

    [Theory]
    [InlineData(GraphInputFormat.Typed)]
    [InlineData(GraphInputFormat.Python)]
    public async Task DependencyProjectionPreservesTypedContentOrUsesTheSelectedPythonTextFormat(GraphInputFormat format)
    {
        MultiAgentInput? received = null;
        var first = new FunctionNode("first", (_, _, _) => Output([new TextBlock("answer"), new JsonBlock(JsonSerializer.SerializeToElement(new { value = 42 })), new ImageBlock("image/png", "aGVsbG8="), new ReasoningBlock("private", "signed"), new RedactedReasoningBlock("opaque")]));
        var second = new FunctionNode("second", (input, _, _) => { received = input; return Output([new TextBlock("done")]); });
        var graph = new Graph(new() { Nodes = [first, second], Edges = [new("first", "second")], InputFormat = format });

        await graph.InvokeAsync("task");

        Assert.NotNull(received);
        Assert.DoesNotContain(received.Content, block => block is ReasoningBlock or RedactedReasoningBlock);
        if (format == GraphInputFormat.Typed)
        {
            Assert.Equal(["task", "[node: first]", "answer"], received.Content.OfType<TextBlock>().Select(block => block.Text));
            Assert.Single(received.Content.OfType<JsonBlock>());
            Assert.Single(received.Content.OfType<ImageBlock>());
        }
        else
        {
            Assert.Equal(["Original Task: task", "\nInputs from previous nodes:", "\nFrom first:", "  - Agent: answer"], received.Content.OfType<TextBlock>().Select(block => block.Text));
            Assert.All(received.Content, block => Assert.IsType<TextBlock>(block));
        }
    }

    [Fact]
    public async Task NestedGraphsPreserveInnerProgressIdentityAndAggregateUsageOnce()
    {
        var caller = new Dictionary<string, object?>();
        async IAsyncEnumerable<INodeStreamEvent> Body()
        {
            yield return new NodeStreamEvent("inner progress");
            yield return new NodeResultUpdateEvent([new TextBlock("inner output")], new Usage(3, 5));
            await Task.CompletedTask;
        }
        var inner = new Graph(new() { Id = "innerGraph", Nodes = [new FunctionNode("inner", (_, context, _) => { Assert.Same(caller, context.InvocationState); return Body(); })] });
        var outer = new Graph(new() { Nodes = [new MultiAgentNode(inner, "nested")] });
        var identities = new List<string>();
        MultiAgentResult? result = null;

        await foreach (var item in outer.StreamAsync(new("task"), new() { InvocationState = caller }))
        {
            if (item is NodeStreamUpdateEvent { Inner: "inner progress" } progress) identities.Add(progress.NodeId);
            if (item is MultiAgentResultEvent terminal) result = terminal.Result;
        }

        Assert.Equal(["inner"], identities);
        Assert.NotNull(result);
        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("nested", Assert.Single(result.Results).NodeId);
        Assert.Equal(new Usage(3, 5), result.Usage);
        Assert.Equal("inner output", result.ToString());
    }

    [Fact]
    public async Task LifecycleCallbacksAreAwaitedInNodeAndInvocationOrder()
    {
        var order = new List<string>();
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<MultiAgentInitializedEvent>((_, _) => { order.Add("initialized"); return default; });
        hooks.Register<BeforeMultiAgentInvocationEvent>((_, _) => { order.Add("beforeGraph"); return default; });
        hooks.Register<BeforeNodeCallEvent>((_, _) => { order.Add("beforeNode"); return default; });
        hooks.Register<NodeStreamUpdateEvent>((_, _) => { order.Add("progress"); return default; });
        hooks.Register<NodeResultEvent>((_, _) => { order.Add("result"); return default; });
        hooks.Register<AfterNodeCallEvent>((_, _) => { order.Add("afterNode"); return default; });
        hooks.Register<AfterMultiAgentInvocationEvent>((_, _) => { order.Add("afterGraph"); return default; });
        async IAsyncEnumerable<INodeStreamEvent> Body()
        {
            Assert.Equal("beforeNode", order.Last());
            yield return new NodeStreamEvent("working");
            Assert.Equal("progress", order.Last());
            yield return new NodeResultUpdateEvent([new TextBlock("done")]);
            await Task.CompletedTask;
        }
        var graph = new Graph(new() { Nodes = [new FunctionNode("a", (_, _, _) => Body())], Hooks = hooks });

        await graph.InvokeAsync("task");

        Assert.Equal(["initialized", "beforeGraph", "beforeNode", "progress", "result", "afterNode", "afterGraph"], order);
    }

    [Theory]
    [InlineData(LimitOutcome.Throw)]
    [InlineData(LimitOutcome.FailedResult)]
    public async Task CyclesRespectStepBudgetsAndTheExplicitLimitOutcome(LimitOutcome outcome)
    {
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("a", (_, _, _) => Output([new TextBlock("visit")]))],
            Sources = ["a"],
            Edges = [new("a", "a", (state, _, _) => new(state.Steps < 5))],
            MaxSteps = 2,
            LimitOutcome = outcome
        });
        MultiAgentResult result;
        if (outcome == LimitOutcome.Throw)
        {
            var error = await Assert.ThrowsAsync<GraphLimitException>(() => graph.InvokeAsync("task"));
            Assert.Equal("steps", error.Budget);
            result = Assert.IsType<MultiAgentResult>(error.Result);
        }
        else result = await graph.InvokeAsync("task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, graph.State!.Steps);
    }

    [Theory]
    [InlineData(NodeFailurePolicy.ContinueIndependent)]
    [InlineData(NodeFailurePolicy.FailFast)]
    public async Task NodeFailurePolicyPreservesOriginalFailureAndCompletedSiblingResults(NodeFailurePolicy policy)
    {
        var failure = new IOException("node failed");
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<AfterNodeCallEvent>((value, _) => { if (value.Result.NodeId == "good") completed.TrySetResult(true); return default; });
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("bad", (_, _, token) => Fail(token)), new FunctionNode("good", (_, _, _) => Output([new TextBlock("saved")]))],
            NodeFailure = policy,
            Hooks = hooks
        });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        if (policy == NodeFailurePolicy.FailFast) Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync("task", cancellationToken: safety.Token)));
        else Assert.Equal(Status.Failed, (await graph.InvokeAsync("task", cancellationToken: safety.Token)).Status);

        var results = graph.State!.Results;
        Assert.Same(failure, results.Single(result => result.NodeId == "bad").Error);
        Assert.Equal(Status.Completed, results.Single(result => result.NodeId == "good").Status);

        async IAsyncEnumerable<INodeStreamEvent> Fail([EnumeratorCancellation] CancellationToken token)
        { await completed.Task.WaitAsync(token); yield return await Task.FromException<INodeStreamEvent>(failure); }
    }

    [Fact]
    public async Task FailFastCancelsAndDrainsASiblingThatHasEnteredItsBody()
    {
        var failure = new IOException("node failed");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("bad", (_, _, token) => Fail(token)), new FunctionNode("active", (_, _, token) => Active(token))],
            NodeFailure = NodeFailurePolicy.FailFast
        });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => graph.InvokeAsync("task", cancellationToken: safety.Token)));

        Assert.True(disposed);
        Assert.Equal(Status.Cancelled, graph.State!.Results.Single(result => result.NodeId == "active").Status);

        async IAsyncEnumerable<INodeStreamEvent> Fail([EnumeratorCancellation] CancellationToken token)
        { await entered.Task.WaitAsync(token); yield return await Task.FromException<INodeStreamEvent>(failure); }
        async IAsyncEnumerable<INodeStreamEvent> Active([EnumeratorCancellation] CancellationToken token)
        { try { entered.TrySetResult(true); yield return new NodeStreamEvent("waiting"); await Task.Delay(Timeout.Infinite, token); } finally { disposed = true; } }
    }

    [Theory]
    [InlineData(AgentContextPolicy.RestoreAfterExecution, 0)]
    [InlineData(AgentContextPolicy.Retain, 2)]
    public async Task AgentNodesPreserveTypedInputAndFollowTheirExplicitStatePolicy(AgentContextPolicy policy, int retainedMessages)
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((value, _) => { value.Agent.State.Set("changed", true); return default; });
        var model = new AgentTests.ScriptedModel(AgentTests.Text("answer"));
        await using var agent = new Agent(new() { Id = "agent", Model = model, Hooks = hooks });
        var graph = new Graph(new() { Nodes = [new AgentNode(agent, options: new() { ContextPolicy = policy })] });
        using var document = JsonDocument.Parse("{\"value\":42}");

        var result = await graph.InvokeAsync(new MultiAgentInput([new TextBlock("task"), new JsonBlock(document.RootElement)]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("answer", result.ToString());
        Assert.Equal(42, Assert.IsType<JsonBlock>(model.Requests[0].Messages[0].Content[1]).Json.GetProperty("value").GetInt32());
        Assert.Equal(retainedMessages, agent.Messages.Count);
        Assert.Equal(policy == AgentContextPolicy.Retain, agent.State.Get<bool>("changed"));
    }

    [Theory]
    [InlineData(DependencyPolicy.AllSatisfied, false)]
    [InlineData(DependencyPolicy.AnySatisfied, true)]
    public async Task IncomingConditionsFollowTheExplicitAndOrPolicy(DependencyPolicy policy, bool executes)
    {
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("a", (_, _, _) => Output([new TextBlock("a")])), new FunctionNode("b", (_, _, _) => Output([new TextBlock("b")])), new FunctionNode("c", (_, _, _) => Output([new TextBlock("c")]))],
            Edges = [new("a", "c", (_, _, _) => new(false)), new("b", "c")],
            Dependencies = policy
        });

        var result = await graph.InvokeAsync("task");

        Assert.Equal(executes, result.Results.Any(result => result.NodeId == "c"));
    }

    [Theory]
    [InlineData(GraphScheduling.ReadyNodes, true)]
    [InlineData(GraphScheduling.CompletedBatches, false)]
    public async Task BatchSchedulingWaitsForAllCurrentSourcesWhileReadyNodesCanAdvance(GraphScheduling scheduling, bool advancesEarly)
    {
        var successorStarted = 0;
        var observedEarly = false;
        async IAsyncEnumerable<INodeStreamEvent> Slow([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var index = 0; index < 5 || scheduling == GraphScheduling.ReadyNodes && Volatile.Read(ref successorStarted) == 0; index++)
            { cancellationToken.ThrowIfCancellationRequested(); yield return new NodeStreamEvent(index); }
            observedEarly = Volatile.Read(ref successorStarted) != 0;
            yield return new NodeResultUpdateEvent([new TextBlock("slow")]);
            await Task.CompletedTask;
        }
        var graph = new Graph(new()
        {
            Nodes = [new FunctionNode("a", (_, _, _) => Output([new TextBlock("fast")])), new FunctionNode("b", (_, _, token) => Slow(token)), new FunctionNode("c", (_, _, _) => { Interlocked.Exchange(ref successorStarted, 1); return Output([new TextBlock("next")]); })],
            Edges = [new("a", "c")],
            MaxConcurrency = 2,
            Scheduling = scheduling
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await graph.InvokeAsync("task", cancellationToken: deadline.Token);

        Assert.Equal(advancesEarly, observedEarly);
    }

    [Fact]
    public async Task IndependentNodesExecuteConcurrentlyWithinTheConfiguredBound()
    {
        var started = 0;
        var active = 0;
        var maximum = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<INodeStreamEvent> Body()
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            if (Interlocked.Increment(ref started) == 2) release.TrySetResult(true);
            try { await release.Task.WaitAsync(TimeSpan.FromSeconds(2)); yield return new NodeResultUpdateEvent([new TextBlock("done")]); }
            finally { Interlocked.Decrement(ref active); }
        }
        var graph = new Graph(new() { Nodes = [new FunctionNode("a", (_, _, _) => Body()), new FunctionNode("b", (_, _, _) => Body()), new FunctionNode("c", (_, _, _) => Body())], MaxConcurrency = 2 });

        var result = await graph.InvokeAsync("go");

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(2, maximum);
        Assert.Equal(3, started);
    }

    [Fact]
    public async Task TwoNodeChainPreservesTypedPredecessorContentAndCallerState()
    {
        var caller = new Dictionary<string, object?> { ["caller"] = 7 };
        using var document = JsonDocument.Parse("{\"value\":42}");
        var first = new FunctionNode("first", (_, context, _) =>
        {
            Assert.Same(caller, context.InvocationState);
            context.State.App.Set("first", true);
            return Output([new TextBlock("answer"), new JsonBlock(document.RootElement)], new Usage(2, 3));
        });
        var second = new FunctionNode("second", (input, context, _) =>
        {
            Assert.Same(caller, context.InvocationState);
            Assert.True(context.State.App.Get<bool>("first"));
            Assert.Equal("task", Assert.IsType<TextBlock>(input.Content[0]).Text);
            Assert.Equal("[node: first]", Assert.IsType<TextBlock>(input.Content[1]).Text);
            Assert.Equal("answer", Assert.IsType<TextBlock>(input.Content[2]).Text);
            Assert.Equal(42, Assert.IsType<JsonBlock>(input.Content[3]).Json.GetProperty("value").GetInt32());
            return Output([new TextBlock("final")], new Usage(5, 7));
        });
        var graph = new Graph(new() { Nodes = [first, second], Edges = [new("first", "second")] });

        var result = await graph.InvokeAsync("task", new() { InvocationState = caller });

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(["first", "second"], result.Results.Select(result => result.NodeId));
        Assert.Equal(new Usage(7, 10), result.Usage);
        Assert.Equal("final", result.ToString());
        Assert.Single(caller);
    }

    private static async IAsyncEnumerable<INodeStreamEvent> Output(IReadOnlyList<ContentBlock> content, Usage? usage = null)
    {
        yield return new NodeResultUpdateEvent(content, usage);
        await Task.CompletedTask;
    }
}
