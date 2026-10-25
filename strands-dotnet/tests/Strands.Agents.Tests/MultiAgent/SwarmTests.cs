using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.MultiAgent;

namespace Strands.Agents.Tests.MultiAgent;

public sealed class SwarmTests
{
    [Fact]
    public async Task PythonCommitsAcceptedKnowledgeAtAPauseAndNeverReappliesItAfterAnExternalUpdate()
    {
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var first = new Agent(new() { Id = "first", Model = new ApprovalHandoffModel(), Tools = [approve] });
        await using var last = new Agent(new() { Id = "last", Model = new ToolRoutingModel(null) });
        var options = SwarmOptions.Python with { Nodes = [new(first), new(last)] };
        var swarm = await Swarm.CreateAsync(options);

        var paused = await swarm.InvokeAsync("task");

        Assert.Equal(42, swarm.SharedContext.GetAll()["first"]["value"].GetInt32());
        swarm.SharedContext.AddContext("first", "value", JsonSerializer.SerializeToElement(99));
        var restored = new Swarm(options); await restored.LoadSnapshotAsync(await swarm.TakeSnapshotAsync());
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));
        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal(99, restored.SharedContext.GetAll()["first"]["value"].GetInt32());
    }

    [Theory]
    [InlineData(SwarmFailurePolicy.Throw)]
    [InlineData(SwarmFailurePolicy.FailedResult)]
    public async Task OrdinaryFailurePolicyPreservesTheOriginalModelErrorAndBorrowedAgent(SwarmFailurePolicy policy)
    {
        var failure = new IOException("model"); Exception? observed = null;
        var callbacks = new MultiAgentHookRegistry(); callbacks.Register<AfterMultiAgentInvocationEvent>((value, _) => { observed = value.Error; return default; });
        await using var agent = new Agent(new() { Model = new FailureModel(failure) });
        var swarm = new Swarm(new() { Nodes = [new(agent)], FailurePolicy = policy, Hooks = callbacks });

        if (policy == SwarmFailurePolicy.Throw) Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => swarm.InvokeAsync("task")));
        else
        {
            var result = await swarm.InvokeAsync("task");
            Assert.Equal(Status.Failed, result.Status); Assert.Same(failure, result.Error);
        }

        Assert.Same(failure, observed);
        Assert.Equal(Status.Failed, Assert.Single(swarm.State!.Results).Status);
        Assert.Empty(agent.Messages);
        await agent.InitializeAsync();
    }

    [Fact]
    public async Task CallerCancellationAlwaysPropagatesEvenWithPythonFailedResultPolicies()
    {
        using var cancellation = new CancellationTokenSource();
        var hooks = new MultiAgentHookRegistry(); hooks.Register<NodeStreamUpdateEvent>((value, _) => { if (value.Inner is ModelStreamUpdateEvent) cancellation.Cancel(); return default; });
        var model = new BlockingModel();
        await using var agent = new Agent(new() { Model = model });
        var swarm = await Swarm.CreateAsync(SwarmOptions.Python with { Nodes = [new(agent)], Hooks = hooks });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => swarm.InvokeAsync("task", cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(model.Closed);
        Assert.False(swarm.CanResume);
    }

    [Fact]
    public async Task ANewSwarmNeverAdoptsAnUnrelatedPauseFromItsBorrowedAgent()
    {
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var agent = new Agent(new() { Model = new ApprovalHandoffModel(), Tools = [approve] });
        var external = await agent.InvokeAsync("external task");
        var swarm = new Swarm(new() { Nodes = [new(agent)] });

        await Assert.ThrowsAsync<InvalidOperationException>(() => swarm.InvokeAsync("unrelated task"));

        Assert.False(swarm.CanResume);
        Assert.Equal(Assert.Single(external.Interrupts).Id, Assert.Single(agent.InterruptState!.Interrupts).Id);
    }

    [Fact]
    public async Task AGraphCanCheckpointAndResumeANestedSwarmWithoutChangingItsHookIdentity()
    {
        var model = new RoutingModel("""{"message":"finished"}""");
        await using var agent = new Agent(new() { Id = "agent", Model = model });
        Graph Create()
        {
            var callbacks = new MultiAgentHookRegistry();
            callbacks.Register<BeforeNodeCallEvent>((value, _) => { Assert.IsType<Swarm>(value.Orchestrator); Assert.True(value.Interrupt("approval").GetBoolean()); return default; });
            var inner = new Swarm(new() { Nodes = [new(agent)], Hooks = callbacks });
            return new(new() { Nodes = [new MultiAgentNode(inner)] });
        }
        var original = Create(); var paused = await original.InvokeAsync("task");
        var restored = Create(); await restored.LoadSnapshotAsync(await original.TakeSnapshotAsync());

        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status); Assert.Equal("finished", result.ToString());
        Assert.Single(model.Requests); Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task ParallelHandoffsCommitInAssistantCallOrderDespiteReverseCompletion()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>(async (value, token) =>
        { if (value.ToolUse.ToolUseId == "first") await TaskCancellation.WaitAsync(gate.Task, token); else if (value.ToolUse.ToolUseId == "second") gate.TrySetResult(true); });
        await using var first = new Agent(new() { Id = "source", Model = new ParallelHandoffModel(), Hooks = hooks });
        var unusedModel = new ToolRoutingModel(null);
        await using var unused = new Agent(new() { Id = "unused", Model = unusedModel });
        await using var last = new Agent(new() { Id = "last", Model = new ToolRoutingModel(null) });
        var swarm = new Swarm(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(first), new(unused), new(last)] });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await swarm.InvokeAsync("task", cancellationToken: safety.Token);

        Assert.Equal(new[] { "source", "last" }, result.Results.Select(value => value.NodeId));
        Assert.Empty(unusedModel.Requests);
        Assert.Equal("second", swarm.SharedContext.GetAll()["source"]["order"].GetString());
    }

    [Fact]
    public async Task RestoredResetModeCannotSilentlyRecaptureAMissingConstructionBaseline()
    {
        var callbacks = new MultiAgentHookRegistry();
        callbacks.Register<BeforeNodeCallEvent>((value, _) => { value.Interrupt("approval"); return default; });
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"finished"}""") });
        agent.State.Set("counter", 7);
        var options = new SwarmOptions { AgentContext = AgentContextPolicy.ResetOnRevisit, Nodes = [new(agent)], Hooks = callbacks };
        var original = await Swarm.CreateAsync(options);
        await original.InvokeAsync("task");
        agent.State.Set("counter", 99);
        var snapshot = await original.TakeSnapshotAsync();
        var fields = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        fields["baselines"] = JsonSerializer.SerializeToElement(new { });
        var restored = new Swarm(options);

        await Assert.ThrowsAsync<InvalidDataException>(() => restored.LoadSnapshotAsync(new(fields)).AsTask());

        Assert.Null(restored.State);
        Assert.Equal(99, agent.State.Get<int>("counter"));
    }

    [Fact]
    public async Task MalformedCommittedHandoffIsRejectedBeforeAnyRestoredStateChanges()
    {
        await using var first = new Agent(new() { Id = "first", Model = new RoutingModel("""{"agentId":"last","message":"continue"}""") });
        var model = new RoutingModel("""{"message":"finished"}""");
        await using var last = new Agent(new() { Id = "last", Model = model });
        var hooks = new MultiAgentHookRegistry(); hooks.Register<MultiAgentHandoffEvent>((_, _) => throw new IOException("stop"));
        var store = new SnapshotStore();
        await Assert.ThrowsAsync<IOException>(() => new Swarm(new() { Nodes = [new(first), new(last)], SessionId = "session", SessionStore = store, Hooks = hooks }).InvokeAsync("task"));
        var fields = store.Snapshot!.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        var state = JsonNode.Parse(fields["state"].GetRawText())!;
        state["results"]![0]!["result"]!["structuredOutput"]!["message"] = 42;
        fields["state"] = JsonSerializer.SerializeToElement(state);
        var restored = new Swarm(new() { Nodes = [new(first), new(last)] });

        await Assert.ThrowsAsync<InvalidDataException>(() => restored.LoadSnapshotAsync(new(fields)).AsTask());

        Assert.Null(restored.State);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task ToolModePreservesTypedInputAndRichFinalOutputIncludingCitationDisplayText()
    {
        var model = new RichContentModel();
        await using var agent = new Agent(new() { Model = model });
        MultiAgentInput input;
        using (var producer = JsonDocument.Parse("42")) input = new([new JsonBlock(producer.RootElement), new ImageBlock("image/png", "AA==")]);
        var swarm = new Swarm(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(agent)] });

        var result = await swarm.InvokeAsync(input);

        Assert.Equal("cited answer", result.ToString());
        Assert.IsType<CitationsBlock>(result.Content[0]);
        Assert.Equal("AA==", Assert.IsType<ImageBlock>(result.Content[1]).Data);
        var content = Assert.Single(model.Requests).Messages[0].Content;
        Assert.IsType<TextBlock>(content[0]);
        Assert.Equal(42, Assert.IsType<JsonBlock>(content[1]).Json.GetInt32());
        Assert.Equal("AA==", Assert.IsType<ImageBlock>(content[2]).Data);
    }

    [Fact]
    public async Task NormalCheckpointWritesUseCallerCancellationAndFinalCleanupSavesTheCommittedFrontier()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new SnapshotStore { OnSave = (_, token) => { if (token.CanBeCanceled) cancellation.Cancel(); return default; } };
        var model = new RoutingModel("""{"agentId":"last","message":"continue"}""");
        await using var first = new Agent(new() { Id = "first", Model = model });
        await using var last = new Agent(new() { Id = "last", Model = new RoutingModel("""{"message":"finished"}""") });
        Swarm Create() => new(new() { Nodes = [new(first), new(last)], SessionId = "session", SessionStore = store });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().InvokeAsync("task", cancellationToken: cancellation.Token));
        Assert.Equal(new[] { true, false }, store.Tokens.Select(token => token.CanBeCanceled));
        var result = await Create().InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData(MultiAgentSaveLatestStrategy.Node, 2)]
    [InlineData(MultiAgentSaveLatestStrategy.Invocation, 1)]
    public async Task SessionWriteFrequencyMatchesTheSelectedLatestPolicy(MultiAgentSaveLatestStrategy policy, int writes)
    {
        var store = new SnapshotStore();
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"finished"}""") });
        var swarm = new Swarm(new() { Nodes = [new(agent)], SessionId = "session", SessionStore = store, SaveLatestOn = policy });

        await swarm.InvokeAsync("task");

        Assert.Equal(writes, store.Writes);
        Assert.NotNull(store.Snapshot);
    }

    [Fact]
    public async Task CheckpointFailurePreservesItsIdentityWhenTheFinalSaveAlsoFails()
    {
        var primary = new IOException("normal save"); var cleanup = new IOException("cleanup save");
        var store = new SnapshotStore { OnSave = (count, _) => throw (count == 1 ? primary : cleanup) };
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"finished"}""") });
        var swarm = new Swarm(new() { Nodes = [new(agent)], SessionId = "session", SessionStore = store });

        var error = await Assert.ThrowsAsync<IOException>(() => swarm.InvokeAsync("task"));

        Assert.Same(primary, error);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.Equal(2, store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashFrontierRetriesAnAbortedVisitWithoutKeepingItsCancelledResult(bool responseOnly)
    {
        using var cancellation = new CancellationTokenSource();
        var callbacks = new MultiAgentHookRegistry();
        callbacks.Register<NodeStreamUpdateEvent>((value, _) => { if (value.Inner is ModelStreamUpdateEvent) cancellation.Cancel(); return default; });
        var blocked = new BlockingModel();
        await using var original = new Agent(new() { Id = "node", Model = blocked });
        var store = new SnapshotStore();
        var first = new Swarm(new() { Nodes = [new(original)], Hooks = callbacks, SessionId = "session", SessionStore = store });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.InvokeAsync("original task", cancellationToken: cancellation.Token));
        Assert.True(blocked.Closed);
        var model = new RoutingModel("""{"message":"finished"}""");
        await using var replacement = new Agent(new() { Id = "node", Model = model });
        var restored = new Swarm(new() { Nodes = [new(replacement)], SessionId = "session", SessionStore = store });

        var result = await restored.InvokeAsync(responseOnly ? MultiAgentInput.Resume([]) : new MultiAgentInput("new task"));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Single(result.Results);
        Assert.Equal(responseOnly ? "original task" : "new task", Assert.Single(model.Requests).Messages[0].Text);
    }

    [Theory]
    [InlineData(SwarmTimeoutScope.Invocation)]
    [InlineData(SwarmTimeoutScope.Execution)]
    public async Task RestoredTimeoutBudgetCanMatchEitherUpstreamBoundary(SwarmTimeoutScope scope)
    {
        await using var first = new Agent(new() { Id = "first", Model = new RoutingModel("""{"agentId":"last","message":"continue"}""") });
        var model = new RoutingModel("""{"message":"finished"}""");
        await using var last = new Agent(new() { Id = "last", Model = model });
        var hooks = new MultiAgentHookRegistry(); hooks.Register<MultiAgentHandoffEvent>((_, _) => throw new IOException("stop"));
        var store = new SnapshotStore();
        await Assert.ThrowsAsync<IOException>(() => new Swarm(new() { Nodes = [new(first), new(last)], SessionId = "session", SessionStore = store, Hooks = hooks }).InvokeAsync("task"));
        var fields = store.Snapshot!.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        var state = JsonNode.Parse(fields["state"].GetRawText())!; state["elapsedExecutionTicks"] = TimeSpan.FromSeconds(2).Ticks;
        fields["state"] = JsonSerializer.SerializeToElement(state);
        var restored = new Swarm(new() { Nodes = [new(first), new(last)], TimeoutScope = scope, Timeout = TimeSpan.FromSeconds(1) });
        await restored.LoadSnapshotAsync(new(fields));

        if (scope == SwarmTimeoutScope.Execution)
        {
            var error = await Assert.ThrowsAsync<SwarmLimitException>(() => restored.InvokeAsync(MultiAgentInput.Resume([])));
            Assert.Equal("timeout", error.Budget);
            Assert.Empty(model.Requests);
        }
        else Assert.Equal(Status.Completed, (await restored.InvokeAsync(MultiAgentInput.Resume([]))).Status);
    }

    [Fact]
    public async Task InvalidSharedContextKeysBecomeToolErrorsWithoutCommittingAHandoff()
    {
        var model = new InvalidContextModel();
        await using var first = new Agent(new() { Id = "first", Model = model });
        var target = new ToolRoutingModel(null);
        await using var last = new Agent(new() { Id = "last", Model = target });
        var swarm = new Swarm(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(first), new(last)] });

        var result = await swarm.InvokeAsync("task");

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("recovered", result.ToString());
        Assert.Empty(swarm.SharedContext.GetAll());
        Assert.Empty(target.Requests);
    }

    [Theory]
    [InlineData(GraphRestoredInputPolicy.Incoming)]
    [InlineData(GraphRestoredInputPolicy.Stored)]
    public async Task RestoredStructuredHandoffKeepsItsCommittedMessageAndContext(GraphRestoredInputPolicy policy)
    {
        await using var first = new Agent(new() { Id = "first", Model = new RoutingModel("""{"agentId":"last","message":"continue","context":{"value":42}}""") });
        var target = new RoutingModel("""{"message":"finished"}""");
        await using var last = new Agent(new() { Id = "last", Model = target });
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<MultiAgentHandoffEvent>((_, _) => throw new IOException("stop after commit"));
        var store = new SnapshotStore();
        Swarm Create(MultiAgentHookRegistry callbacks) => new(new() { Nodes = [new(first), new(last)], SessionId = "session", SessionStore = store, Hooks = callbacks, RestoredInput = policy });
        await Assert.ThrowsAsync<IOException>(() => Create(hooks).InvokeAsync("original task"));

        await Create(new()).InvokeAsync("new task");

        var message = Assert.Single(target.Requests).Messages[0];
        Assert.Equal("continue", Assert.IsType<TextBlock>(message.Content[0]).Text);
        Assert.Contains("42", Assert.IsType<TextBlock>(message.Content[1]).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffRecoveryDoesNotRequireUniqueTrackingIdsOrUnreducedHistoricalMessages(bool reduce)
    {
        var model = new HistoricalHandoffModel();
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) =>
        { if (value.Message is { } message) value.Message = new(message.Role, message.Content, "repeated-id"); return default; });
        hooks.Register<BeforeModelCallEvent>(async (value, token) =>
        { if (reduce && model.Calls == 1) await value.Agent.ReplaceContextAsync([Message.User("reduced history")], token); });
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var first = new Agent(new() { Id = "first", Model = model, Tools = [approve], Hooks = hooks });
        await using var last = new Agent(new() { Id = "last", Model = new ToolRoutingModel(null) });
        Swarm Create() => new(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(first), new(last)] });
        var original = Create();
        var paused = await original.InvokeAsync("task");
        var restored = Create();

        await restored.LoadSnapshotAsync(await original.TakeSnapshotAsync());
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("finished", result.ToString());
        Assert.Equal(42, restored.SharedContext.GetAll()["first"]["value"].GetInt32());
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task AsyncConstructionGuardsSnapshotAccessFromInitializedCallbacks()
    {
        var callbacks = new MultiAgentHookRegistry();
        callbacks.Register<MultiAgentInitializedEvent>(async (value, _) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ((Swarm)value.Orchestrator).TakeSnapshotAsync().AsTask());
        });
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"unused"}""") });

        await Swarm.CreateAsync(new() { Nodes = [new(agent)], Hooks = callbacks });
    }

    [Theory]
    [InlineData(SwarmRepetitionScope.Invocation, Status.Completed)]
    [InlineData(SwarmRepetitionScope.Execution, Status.Failed)]
    public async Task RepetitionHistoryCanMatchEitherUpstreamResumeBoundary(SwarmRepetitionScope scope, Status expected)
    {
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var first = new Agent(new() { Id = "first", Model = new RepeatedApprovalModel(), Tools = [approve] });
        await using var unused = new Agent(new() { Id = "unused", Model = new ToolRoutingModel(null) });
        var swarm = new Swarm(new()
        {
            Routing = SwarmRoutingMode.Tool,
            Nodes = [new(first), new(unused)],
            RepetitionScope = scope,
            RepetitiveHandoffDetectionWindow = 2,
            RepetitiveHandoffMinUniqueAgents = 2
        });
        var paused = await swarm.InvokeAsync("task");

        var result = await swarm.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == Status.Failed ? 2 : 3, result.Results.Count);
    }

    [Fact]
    public async Task SessionPersistsAcceptedHumanAnswersBeforeInvocationCallbacks()
    {
        var calls = 0;
        var failure = new IOException("resume callback");
        var callbacks = new MultiAgentHookRegistry();
        callbacks.Register<BeforeMultiAgentInvocationEvent>((_, _) => { if (++calls == 2) throw failure; return default; });
        callbacks.Register<BeforeNodeCallEvent>((value, _) => { Assert.True(value.Interrupt("approval").GetBoolean()); return default; });
        var store = new SnapshotStore();
        var model = new RoutingModel("""{"message":"finished"}""");
        await using var agent = new Agent(new() { Model = model });
        Swarm Create() => new(new() { Nodes = [new(agent)], SessionId = "session", SessionStore = store, Hooks = callbacks });
        var swarm = Create();
        var paused = await swarm.InvokeAsync("original task");

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => swarm.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]))));
        var restored = Create();
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("finished", result.ToString());
        Assert.Single(model.Requests);
        Assert.False(restored.CanResume);
    }

    [Fact]
    public async Task SessionLoadFailureNeverOverwritesUnreadHistory()
    {
        var failure = new IOException("load");
        var store = new SnapshotStore { LoadFailure = failure };
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"unused"}""") });
        var swarm = new Swarm(new() { Nodes = [new(agent)], SessionId = "session", SessionStore = store });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => swarm.InvokeAsync("task")));

        Assert.Equal(0, store.Writes);
        Assert.Null(swarm.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedVisitBudgetSurvivesDirectAndFreshInstanceHumanResume(bool fresh)
    {
        var firstModel = new ToolRoutingModel("second");
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var first = new Agent(new() { Id = "first", Model = firstModel });
        await using var second = new Agent(new() { Id = "second", Model = new ApprovalHandoffModel("first"), Tools = [approve] });
        Swarm Create() => new(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(first), new(second)], MaxHandoffs = 2 });
        var swarm = Create();
        var paused = await swarm.InvokeAsync("task");
        if (fresh)
        {
            var snapshot = await swarm.TakeSnapshotAsync(); swarm = Create(); await swarm.LoadSnapshotAsync(snapshot);
        }

        var error = await Assert.ThrowsAsync<SwarmLimitException>(() => swarm.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))])));

        Assert.Equal("handoffs", error.Budget);
        Assert.Equal(2, firstModel.Requests.Count);
        Assert.Equal(new[] { "first", "second" }, error.Result!.Results.Select(result => result.NodeId));
    }

    [Fact]
    public async Task EarlyDisposalPreservesTheFirstCleanupFailureAndAttachesOuterCleanup()
    {
        var primary = new IOException("agent cleanup");
        var secondary = new IOException("swarm cleanup");
        var agentHooks = new HookRegistry();
        agentHooks.Register<AfterInvocationEvent>((_, _) => throw primary);
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<AfterMultiAgentInvocationEvent>((_, _) => throw secondary);
        var model = new BlockingModel();
        await using var agent = new Agent(new() { Model = model, Hooks = agentHooks });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Hooks = hooks });
        var iterator = swarm.StreamAsync(new("task")).GetAsyncEnumerator();
        while (await iterator.MoveNextAsync()) if (iterator.Current is NodeStreamUpdateEvent { Inner: ModelStreamUpdateEvent }) break;

        var error = await Assert.ThrowsAsync<IOException>(() => iterator.DisposeAsync().AsTask());

        Assert.Same(primary, error);
        Assert.Contains(secondary, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        Assert.True(model.Closed);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task SnapshotCannotPromoteAHandoffRejectedByTheAuthoritativeToolResult()
    {
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((value, _) =>
        {
            if (value.Result.ToolUseId == "handoff") value.Result = value.Result with { Status = ToolResultStatus.Error };
            return default;
        });
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new(context.Interrupt("approval").ToString()));
        await using var first = new Agent(new() { Id = "first", Model = new ApprovalHandoffModel(), Tools = [approve], Hooks = hooks });
        await using var last = new Agent(new() { Id = "last", Model = new ToolRoutingModel(null) });
        Swarm Create() => new(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(first), new(last)] });
        var original = Create();
        await original.InvokeAsync("task");
        var snapshot = await original.TakeSnapshotAsync();
        var state = JsonNode.Parse(snapshot.Data["state"].GetRawText())!;
        var turn = state["toolTurns"]!["first"]!;
        var batch = Assert.Single(turn["batches"]!.AsArray())!;
        Assert.Single(batch["results"]!.AsArray())!["status"] = "success";
        var fields = snapshot.Data.ToDictionary(pair => pair.Key, pair => pair.Value);
        fields["state"] = JsonSerializer.SerializeToElement(state);
        var restored = Create();

        await Assert.ThrowsAsync<InvalidDataException>(() => restored.LoadSnapshotAsync(new(fields)).AsTask());

        Assert.Null(restored.State);
        Assert.Empty(restored.SharedContext.GetAll());
    }

    [Fact]
    public async Task SessionCheckpointCommitsTheSourceBeforeAHandoffCallbackCanFail()
    {
        var model = new RoutingModel("""{"agentId":"last","message":"continue"}""");
        await using var first = new Agent(new() { Id = "first", Model = model });
        await using var last = new Agent(new() { Id = "last", Model = new RoutingModel("""{"message":"finished"}""") });
        var failure = new IOException("handoff callback");
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<MultiAgentHandoffEvent>((_, _) => throw failure);
        var store = new SnapshotStore();
        Swarm Create(MultiAgentHookRegistry callbacks) => new(new() { Nodes = [new(first), new(last)], SessionId = "session", SessionStore = store, Hooks = callbacks });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => Create(hooks).InvokeAsync("original task")));
        Assert.NotNull(store.Snapshot);
        var restored = Create(new());
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("finished", result.ToString());
        Assert.Single(model.Requests);
        Assert.Equal(new[] { "first", "last" }, result.Results.Select(value => value.NodeId));
    }

    [Fact]
    public async Task ANewSwarmRestoresThePausedBatchAndItsUncommittedHandoffFromJson()
    {
        var effects = 0;
        var model = new ApprovalHandoffModel();
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { Assert.True(context.Interrupt("approval").GetBoolean()); effects++; return new("accepted"); });
        await using var first = new Agent(new() { Id = "first", Model = model, Tools = [approve] });
        await using var last = new Agent(new() { Id = "last", Model = new ToolRoutingModel(null) });
        Swarm Create() => new(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(first), new(last)] });
        var original = Create();
        original.SharedContext.AddContext("application", "seed", JsonSerializer.SerializeToElement(7));
        var paused = await original.InvokeAsync("original task");

        var snapshot = JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(await original.TakeSnapshotAsync()))!;
        var restored = Create();
        await restored.LoadSnapshotAsync(snapshot);
        var result = await restored.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("finished", result.ToString());
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Calls);
        Assert.Equal(7, restored.SharedContext.GetAll()["application"]["seed"].GetInt32());
        Assert.Equal(42, restored.SharedContext.GetAll()["first"]["value"].GetInt32());
        Assert.False(restored.CanResume);
        Assert.Empty(first.Messages);
    }

    [Fact]
    public async Task ToolHandoffSurvivesAHumanPauseWithoutReplayingCompletedSiblingEffects()
    {
        var effects = 0;
        var model = new ApprovalHandoffModel();
        var approve = new FunctionTool(new("approve", "Approval", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
        { Assert.True(context.Interrupt("approval").GetBoolean()); effects++; return new("accepted"); });
        await using var first = new Agent(new() { Id = "first", Model = model, Tools = [approve] });
        await using var last = new Agent(new() { Id = "last", Model = new ToolRoutingModel(null) });
        var swarm = new Swarm(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(first), new(last)] });

        var paused = await swarm.InvokeAsync("task");

        Assert.Equal(Status.Interrupted, paused.Status);
        Assert.Empty(swarm.SharedContext.GetAll());
        Assert.Empty(first.Messages);
        var completed = await swarm.InvokeAsync(MultiAgentInput.Resume([new(Assert.Single(paused.Interrupts).Id, JsonSerializer.SerializeToElement(true))]));
        Assert.Equal(Status.Completed, completed.Status);
        Assert.Equal("finished", completed.ToString());
        Assert.Equal(42, swarm.SharedContext.GetAll()["first"]["value"].GetInt32());
        Assert.Equal(1, effects);
        Assert.Equal(2, model.Calls);
        Assert.Equal(new[] { "first", "last" }, completed.Results.Select(result => result.NodeId));
        Assert.Empty(first.Messages);
    }

    [Fact]
    public async Task NodeApprovalPausesBeforeTheBodyAndResumesTheOriginalTask()
    {
        var model = new RoutingModel("""{"message":"finished"}""");
        await using var agent = new Agent(new() { Model = model });
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeNodeCallEvent>((value, _) => { Assert.True(value.Interrupt("approval").GetBoolean()); return default; });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Hooks = hooks });

        var paused = await swarm.InvokeAsync("original task");

        Assert.Equal(Status.Interrupted, paused.Status);
        Assert.True(swarm.CanResume);
        Assert.Empty(model.Requests);
        var question = Assert.Single(paused.Interrupts);
        var completed = await swarm.InvokeAsync(MultiAgentInput.Resume([new(question.Id, JsonSerializer.SerializeToElement(true))]));
        Assert.Equal(Status.Completed, completed.Status);
        Assert.Equal("finished", completed.ToString());
        Assert.Equal("original task", Assert.Single(model.Requests).Messages[0].Text);
        Assert.Single(completed.Results);
        Assert.False(swarm.CanResume);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task PythonProfileReportsAnExhaustedBudgetAsAFailedResult()
    {
        await using var first = new Agent(new() { Id = "first", Model = new ToolRoutingModel("second") });
        await using var second = new Agent(new() { Id = "second", Model = new ToolRoutingModel(null) });
        var swarm = await Swarm.CreateAsync(SwarmOptions.Python with { Nodes = [new(first), new(second)], MaxSteps = 1 });

        var result = await swarm.InvokeAsync("task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Equal("steps", Assert.IsType<SwarmLimitException>(result.Error).Budget);
        Assert.Single(result.Results);
        Assert.Equal("default_swarm", swarm.Id);
        Assert.Equal(20, swarm.Options.MaxHandoffs);
        Assert.Equal(TimeSpan.FromSeconds(900), swarm.Options.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(300), swarm.Options.NodeTimeout);
    }

    [Fact]
    public async Task ResetModeUsesTheConstructionBaselineOnEveryInvocation()
    {
        var observed = new List<int>();
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            observed.Add(value.Agent.State.Get<int>("counter"));
            value.Agent.State.Set("counter", 8);
            return default;
        });
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"first"}""", """{"message":"second"}"""), Hooks = hooks });
        agent.State.Set("counter", 7);
        var swarm = await Swarm.CreateAsync(new() { Nodes = [new(agent)], AgentContext = AgentContextPolicy.ResetOnRevisit });
        agent.State.Set("counter", 99);

        await swarm.InvokeAsync("one");
        await swarm.InvokeAsync("two");

        Assert.Equal(new[] { 7, 7 }, observed);
        Assert.Equal(8, agent.State.Get<int>("counter"));
    }

    [Fact]
    public async Task ToolRoutingUsesABorrowedCoordinationToolAndPassesSharedKnowledgeToTheTarget()
    {
        var first = new ToolRoutingModel("writer");
        var last = new ToolRoutingModel(null);
        await using var researcher = new Agent(new() { Id = "researcher", Model = first });
        await using var writer = new Agent(new() { Id = "writer", Model = last });
        var swarm = new Swarm(new() { Routing = SwarmRoutingMode.Tool, Nodes = [new(researcher), new(writer)] });

        var result = await swarm.InvokeAsync("original task");

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("finished", result.ToString());
        Assert.Equal(new[] { "researcher", "writer" }, result.Results.Select(value => value.NodeId));
        Assert.Contains("Handoff Message: write it", last.Requests[0].Messages[0].Text);
        Assert.Contains("User Request: original task", last.Requests[0].Messages[0].Text);
        Assert.Contains("researcher", last.Requests[0].Messages[0].Text);
        Assert.Equal(42, swarm.SharedContext.GetAll()["researcher"]["value"].GetInt32());
        Assert.Empty(researcher.Tools);
        Assert.Empty(writer.Tools);
        Assert.Empty(researcher.Messages);
        Assert.Empty(writer.Messages);
    }

    [Fact]
    public async Task AwaitedInvocationCallbackObservesTheExactEventPublishedToTheConsumer()
    {
        BeforeMultiAgentInvocationEvent? observed = null;
        var hooks = new MultiAgentHookRegistry();
        hooks.Register<BeforeMultiAgentInvocationEvent>((value, _) => { observed = value; return default; });
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"finished"}""") });
        var swarm = new Swarm(new() { Nodes = [new(agent)], Hooks = hooks });
        var events = new List<IMultiAgentStreamEvent>();

        await foreach (var item in swarm.StreamAsync(new("task"))) events.Add(item);

        Assert.Same(observed, Assert.Single(events.OfType<BeforeMultiAgentInvocationEvent>()));
        Assert.Same(swarm, observed!.Orchestrator);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("nodeTimeout")]
    public async Task CooperativeDeadlinesClassifyTheBudgetAndDrainEveryModelThatStarted(string budget)
    {
        var model = new BlockingModel();
        await using var agent = new Agent(new() { Model = model });
        var swarm = new Swarm(new()
        {
            Nodes = [new(agent)],
            Timeout = budget == "timeout" ? TimeSpan.FromMilliseconds(50) : null,
            NodeTimeout = budget == "nodeTimeout" ? TimeSpan.FromMilliseconds(50) : null
        });
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<SwarmLimitException>(() => swarm.InvokeAsync("task", cancellationToken: safety.Token));

        Assert.Equal(budget, error.Budget);
        Assert.Equal(model.Started, model.Closed);
        Assert.Equal(Status.Failed, Assert.IsType<MultiAgentResult>(error.Result).Status);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task FinalAnswerOnTheLastAllowedVisitSucceeds()
    {
        await using var agent = new Agent(new() { Model = new RoutingModel("""{"message":"finished"}""") });
        var swarm = new Swarm(new() { Nodes = [new(agent)], MaxSteps = 1 });

        var result = await swarm.InvokeAsync("task");

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("finished", result.ToString());
        Assert.Single(result.Results);
        Assert.True(swarm.State!.Nodes[agent.Id].Terminus);
    }

    [Fact]
    public async Task RepetitiveHandoffsReturnAFailedResultBeforeReenteringTheLoop()
    {
        var first = new RoutingModel("""{"agentId":"second","message":"next"}""", """{"agentId":"second","message":"next"}""", """{"message":"unexpected extra visit"}""");
        var second = new RoutingModel("""{"agentId":"first","message":"again"}""", """{"agentId":"first","message":"again"}""");
        var unused = new RoutingModel("""{"message":"unused"}""");
        await using var firstAgent = new Agent(new() { Id = "first", Model = first });
        await using var secondAgent = new Agent(new() { Id = "second", Model = second });
        await using var thirdAgent = new Agent(new() { Id = "third", Model = unused });
        var swarm = new Swarm(new()
        {
            Nodes = [new(firstAgent), new(secondAgent), new(thirdAgent)],
            RepetitiveHandoffDetectionWindow = 4,
            RepetitiveHandoffMinUniqueAgents = 3
        });

        var result = await swarm.InvokeAsync("task");

        Assert.Equal(Status.Failed, result.Status);
        Assert.Contains("Repetitive handoff", Assert.IsAssignableFrom<Exception>(result.Error).Message);
        Assert.Equal(4, result.Results.Count);
        Assert.Equal(2, first.Requests.Count);
        Assert.Equal(2, second.Requests.Count);
        Assert.Empty(unused.Requests);
    }

    [Fact]
    public async Task StepLimitStopsAPendingHandoffBeforeAnotherModelCallAndRetainsThePartialResult()
    {
        var first = new RoutingModel("""{"agentId":"second","message":"next"}""", """{"message":"unexpected extra visit"}""");
        var second = new RoutingModel("""{"agentId":"first","message":"again"}""");
        await using var firstAgent = new Agent(new() { Id = "first", Model = first });
        await using var secondAgent = new Agent(new() { Id = "second", Model = second });
        var swarm = new Swarm(new() { Nodes = [new(firstAgent), new(secondAgent)], MaxSteps = 2 });

        var error = await Assert.ThrowsAsync<SwarmLimitException>(() => swarm.InvokeAsync("task"));

        Assert.Equal("steps", error.Budget);
        Assert.Equal(Status.Failed, Assert.IsType<MultiAgentResult>(error.Result).Status);
        Assert.Equal(2, error.Result!.Results.Count);
        Assert.Single(first.Requests);
        Assert.Single(second.Requests);
        Assert.Empty(firstAgent.Messages);
        Assert.Empty(secondAgent.Messages);
    }

    [Fact]
    public async Task StructuredHandoffPassesTheChosenMessageAndContextThenReturnsTheFinalAnswer()
    {
        var first = new RoutingModel("""{"agentId":"writer","message":"write it","context":{"value":42}}""");
        var last = new RoutingModel("""{"message":"finished"}""");
        await using var researcher = new Agent(new() { Id = "researcher", Model = first });
        await using var writer = new Agent(new() { Id = "writer", Model = last, Description = "Write the answer" });
        var caller = new Dictionary<string, object?> { ["caller"] = 7 };
        var swarm = new Swarm(new() { Nodes = [new(researcher), new(writer)] });

        var result = await swarm.InvokeAsync("task", new() { InvocationState = caller });

        Assert.Equal(Status.Completed, result.Status);
        Assert.Equal("finished", result.ToString());
        Assert.Equal(new[] { "researcher", "writer" }, result.Results.Select(value => value.NodeId));
        Assert.Equal(6, result.Usage.TotalTokens);
        var content = Assert.Single(last.Requests).Messages[0].Content;
        Assert.Equal("write it", Assert.IsType<TextBlock>(content[0]).Text);
        Assert.StartsWith("Context:\n", Assert.IsType<TextBlock>(content[1]).Text, StringComparison.Ordinal);
        using var context = JsonDocument.Parse(((TextBlock)content[1]).Text.Substring("Context:\n".Length));
        Assert.Equal(42, context.RootElement.GetProperty("value").GetInt32());
        var schema = Assert.Single(first.Requests[0].Tools!).InputSchema.GetProperty("properties").GetProperty("agentId");
        Assert.Equal("writer", Assert.Single(schema.GetProperty("enum").EnumerateArray()).GetString());
        Assert.Contains("Write the answer", schema.GetProperty("description").GetString()!);
        Assert.Empty(researcher.Messages);
        Assert.Empty(writer.Messages);
        Assert.Single(caller);
    }

    private sealed class RoutingModel(params string[] decisions) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var name = Assert.Single(request.Tools!).Name;
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new("call-" + Requests.Count, name, JsonSerializer.SerializeToElement(new { })));
            yield return new ModelContentBlockDeltaEvent(0, ToolInput: decisions[Requests.Count - 1]);
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            yield return new ModelMetadataEvent(new(1, 2));
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
    private sealed class BlockingModel : IModel
    {
        internal bool Started { get; private set; }
        internal bool Closed { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { Started = true; yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0); await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { Closed = true; }
        }
    }
    private sealed class ToolRoutingModel(string? target) : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Assert.Equal("handoff_to_agent", Assert.Single(request.Tools!).Name);
            yield return new ModelMessageStartEvent();
            if (target is not null && Requests.Count == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: JsonSerializer.Serialize(new { agent_name = target, message = "write it", context = new { value = 42 } }));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: target is null ? "finished" : "handed off");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
    private sealed class ApprovalHandoffModel(string target = "last") : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            yield return new ModelMessageStartEvent();
            if (Calls == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: JsonSerializer.Serialize(new { agent_name = target, message = "finish", context = new { value = 42 } }));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelContentBlockStartEvent(1, new("approval", "approve", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(1, ToolInput: "{}");
                yield return new ModelContentBlockStopEvent(1);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "ready to hand off");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
    private sealed class SnapshotStore : IMultiAgentSessionStore
    {
        internal MultiAgentSnapshot? Snapshot { get; private set; }
        internal Exception? LoadFailure { get; init; }
        internal int Writes { get; private set; }
        internal List<CancellationToken> Tokens { get; } = [];
        internal Func<int, CancellationToken, ValueTask>? OnSave { get; init; }
        public ValueTask<MultiAgentSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => LoadFailure is { } failure ? throw failure : new(Snapshot);
        public async ValueTask SaveAsync(string sessionId, MultiAgentSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes++;
            Tokens.Add(cancellationToken);
            if (OnSave is { } callback) await callback(Writes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = JsonSerializer.Deserialize<MultiAgentSnapshot>(JsonSerializer.Serialize(snapshot));
        }
    }
    private sealed class RepeatedApprovalModel : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++;
            yield return new ModelMessageStartEvent();
            if (_calls is 1 or 3)
            {
                yield return new ModelContentBlockStartEvent(0, new("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: """{"agent_name":"first","message":"again"}""");
                yield return new ModelContentBlockStopEvent(0);
                if (_calls == 3)
                {
                    yield return new ModelContentBlockStartEvent(1, new("approve", "approve", JsonSerializer.SerializeToElement(new { })));
                    yield return new ModelContentBlockDeltaEvent(1, ToolInput: "{}");
                    yield return new ModelContentBlockStopEvent(1);
                }
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            cancellationToken.ThrowIfCancellationRequested(); await Task.Yield();
        }
    }
    private sealed class HistoricalHandoffModel : IModel
    {
        internal int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++; yield return new ModelMessageStartEvent();
            if (Calls <= 2)
            {
                var name = Calls == 1 ? "handoff_to_agent" : "approve";
                yield return new ModelContentBlockStartEvent(0, new("repeated-call", name, JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: Calls == 1 ? """{"agent_name":"last","message":"finish","context":{"value":42}}""" : "{}");
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            cancellationToken.ThrowIfCancellationRequested(); await Task.Yield();
        }
    }
    private sealed class InvalidContextModel : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++; yield return new ModelMessageStartEvent();
            if (_calls == 1)
            {
                yield return new ModelContentBlockStartEvent(0, new("handoff", "handoff_to_agent", JsonSerializer.SerializeToElement(new { })));
                yield return new ModelContentBlockDeltaEvent(0, ToolInput: """{"agent_name":"last","message":"finish","context":{"valid":1," ":2}}""");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                Assert.Equal(ToolResultStatus.Error, Assert.Single(request.Messages.Last().Content.OfType<ToolResultBlock>()).Status);
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "recovered");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            cancellationToken.ThrowIfCancellationRequested(); await Task.Yield();
        }
    }
    private sealed class RichContentModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request); yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "cited answer");
            yield return new ModelContentBlockStopEvent(0) { ContentBlock = new CitationsBlock([], [new("cited answer")]) };
            yield return new ModelContentBlockStartEvent(1) { ContentBlock = new ImageBlock("image/png", "AA==") };
            yield return new ModelContentBlockStopEvent(1); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            cancellationToken.ThrowIfCancellationRequested(); await Task.Yield();
        }
    }
    private sealed class FailureModel(Exception failure) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return new ModelMessageStartEvent(); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); throw failure; }
    }
    private sealed class ParallelHandoffModel : IModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls++; yield return new ModelMessageStartEvent();
            if (_calls == 1)
            {
                var names = new[] { "first", "second" }; var targets = new[] { "unused", "last" };
                for (var index = 0; index < 2; index++)
                {
                    yield return new ModelContentBlockStartEvent(index, new(names[index], "handoff_to_agent", JsonSerializer.SerializeToElement(new { })));
                    yield return new ModelContentBlockDeltaEvent(index, ToolInput: JsonSerializer.Serialize(new { agent_name = targets[index], message = "continue", context = new { order = names[index] } }));
                    yield return new ModelContentBlockStopEvent(index);
                }
                yield return new ModelMessageStopEvent(StopReasons.ToolUse);
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0); yield return new ModelContentBlockDeltaEvent(0, Text: "ready");
                yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.EndTurn);
            }
            cancellationToken.ThrowIfCancellationRequested(); await Task.Yield();
        }
    }
}
