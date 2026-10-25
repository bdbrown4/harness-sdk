using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static Strands.Agents.Tests.AgentTests;

namespace Strands.Agents.Tests;

public sealed class RuntimeContractTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ToolFailuresAreCorrelatedAndTheModelCanRecover()
    {
        var validatedBodyCalls = 0;
        var typedTool = new FunctionTool(new("typed", "Accept a string", JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { value = new { type = "string" } },
            required = new[] { "value" },
            additionalProperties = false,
        })), (_, _, _) => { validatedBodyCalls++; return ValueTask.FromResult("should not run"); });
        var brokenTool = Tool("broken", (_, _, _) => ValueTask.FromException<string>(new InvalidOperationException("body failed")));
        var model = new ScriptedModel(Call(("unknown-id", "missing", "{}"), ("schema-id", "typed", "{\"value\":42}"), ("body-id", "broken", "{}")), Text("recovered"));
        var agent = new Agent(new() { Model = model, Tools = [typedTool, brokenTool] });

        var result = await agent.InvokeAsync("try the tools").WaitAsync(Deadline);

        Assert.Equal("recovered", result.ToString());
        Assert.Equal(0, validatedBodyCalls);
        var errors = model.Requests[1].Messages[^1].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(["unknown-id", "schema-id", "body-id"], errors.Select(error => error.ToolUseId));
        Assert.All(errors, error => Assert.Equal(ToolResultStatus.Error, error.Status));
        Assert.Contains("not found", errors[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schema", errors[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("body failed", errors[2].Content);
    }

    [Fact]
    public async Task ConcurrentToolsRespectTheBoundAndCommitInRequestOrder()
    {
        var entered = Gates(3);
        var release = Gates(3);
        var completed = Gates(3);
        var completionOrder = new ConcurrentQueue<int>();
        var active = 0;
        var maximumActive = 0;
        var tool = Tool("work", async (input, _, cancellationToken) =>
        {
            var index = input.GetProperty("index").GetInt32();
            var count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximumActive, Math.Max(Volatile.Read(ref maximumActive), count));
            entered[index].TrySetResult(true);
            try { await release[index].Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref active); }
            completionOrder.Enqueue(index);
            completed[index].TrySetResult(true);
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        var model = new ScriptedModel(IndexedCalls(), Text("done"));
        var agent = new Agent(new() { Model = model, Tools = [tool], MaxParallelTools = 2 });
        var invocation = agent.InvokeAsync("work");
        try
        {
            await Task.WhenAll(entered[0].Task, entered[1].Task).WaitAsync(Deadline);
            Assert.False(entered[2].Task.IsCompleted);
            release[1].TrySetResult(true);
            await completed[1].Task.WaitAsync(Deadline);
            await entered[2].Task.WaitAsync(Deadline);
            release[2].TrySetResult(true);
            await completed[2].Task.WaitAsync(Deadline);
            release[0].TrySetResult(true);
            Assert.Equal("done", (await invocation.WaitAsync(Deadline)).ToString());
        }
        finally
        {
            ReleaseAll(release);
            await invocation.WaitAsync(Deadline);
        }

        Assert.Equal(2, maximumActive);
        Assert.Equal([1, 2, 0], completionOrder);
        var results = model.Requests[1].Messages[^1].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(["call-0", "call-1", "call-2"], results.Select(result => result.ToolUseId));
        Assert.Equal(["0", "1", "2"], results.Select(result => result.Content));
        Assert.All(results, result => Assert.Equal(ToolResultStatus.Success, result.Status));
    }

    [Fact]
    public async Task SequentialToolsWaitForEachPrecedingCall()
    {
        var entered = Gates(3);
        var release = Gates(3);
        var tool = Tool("work", async (input, _, cancellationToken) =>
        {
            var index = input.GetProperty("index").GetInt32();
            entered[index].TrySetResult(true);
            await release[index].Task.WaitAsync(cancellationToken);
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
        var model = new ScriptedModel(IndexedCalls(), Text("done"));
        var agent = new Agent(new() { Model = model, Tools = [tool], ToolExecution = ToolExecutionStrategy.Sequential, MaxParallelTools = 3 });
        var invocation = agent.InvokeAsync("work");
        try
        {
            await entered[0].Task.WaitAsync(Deadline);
            Assert.False(entered[1].Task.IsCompleted);
            release[0].TrySetResult(true);
            await entered[1].Task.WaitAsync(Deadline);
            Assert.False(entered[2].Task.IsCompleted);
            release[1].TrySetResult(true);
            await entered[2].Task.WaitAsync(Deadline);
            release[2].TrySetResult(true);
            Assert.Equal("done", (await invocation.WaitAsync(Deadline)).ToString());
        }
        finally
        {
            ReleaseAll(release);
            await invocation.WaitAsync(Deadline);
        }

        Assert.Equal(["0", "1", "2"], model.Requests[1].Messages[^1].Content.OfType<ToolResultBlock>().Select(result => result.Content));
    }

    [Fact]
    public async Task StreamExposesDeltasBeforeCompletionAndDisposalReleasesTheModel()
    {
        var model = new PausedModel();
        var agent = new Agent(new() { Model = model });
        await using (var stream = agent.StreamAsync("start").GetAsyncEnumerator())
        {
            var sawDelta = false;
            while (await stream.MoveNextAsync().AsTask().WaitAsync(Deadline))
            {
                if (stream.Current is not ModelStreamUpdateEvent { Event: ModelContentBlockDeltaEvent { Text: "partial" } }) continue;
                sawDelta = true;
                break;
            }
            Assert.True(sawDelta);
            Assert.False(model.Disposed.Task.IsCompleted);
            Assert.Equal(Role.User, Assert.Single(agent.Messages).Role);
        }

        await model.Disposed.Task.WaitAsync(Deadline);
        Assert.Equal("next", (await agent.InvokeAsync(null).WaitAsync(Deadline)).ToString());
    }

    [Fact]
    public async Task DisposalAfterAToolRequestCommitsARepairWithoutRunningTheTool()
    {
        var calls = 0;
        var tool = Tool("work", (_, _, _) => { calls++; return ValueTask.FromResult("executed"); });
        var model = new ScriptedModel(Call(("pending", "work", "{}")), Text("resumed"));
        var agent = new Agent(new() { Model = model, Tools = [tool] });
        await using (var stream = agent.StreamAsync("start").GetAsyncEnumerator())
        {
            while (await stream.MoveNextAsync().AsTask().WaitAsync(Deadline))
                if (stream.Current is MessageAddedEvent { Message.Role: Role.Assistant }) break;
            Assert.Single(agent.Messages[^1].Content.OfType<ToolUseBlock>());
            Assert.Equal(0, calls);
        }

        var repaired = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[^1].Content));
        Assert.Equal("pending", repaired.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, repaired.Status);
        Assert.Equal(0, calls);
        Assert.Equal("resumed", (await agent.InvokeAsync(null).WaitAsync(Deadline)).ToString());
        Assert.Equal("pending", Assert.IsType<ToolResultBlock>(model.Requests[1].Messages[^1].Content[0]).ToolUseId);
    }

    [Theory]
    [InlineData(ToolExecutionStrategy.Sequential)]
    [InlineData(ToolExecutionStrategy.Concurrent)]
    public async Task CancellationPairsTheWholeBatchAndDoesNotRequestAnotherModelTurn(ToolExecutionStrategy strategy)
    {
        var entered = Gate();
        var neverReleased = Gate();
        var calls = 0;
        var tool = Tool("work", async (_, _, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            await neverReleased.Task.WaitAsync(cancellationToken);
            return "unreachable";
        });
        var model = new ScriptedModel(Call(("first", "work", "{}"), ("queued", "work", "{}")), Text("must not be called"));
        var store = new SnapshotStore();
        var agent = new Agent(new()
        {
            Model = model,
            Tools = [tool],
            ToolExecution = strategy,
            MaxParallelTools = 1,
            SessionStore = store,
            SessionId = "cancelled",
        });
        using var source = new CancellationTokenSource();
        var invocation = agent.InvokeAsync("start", cancellationToken: source.Token);
        AgentResult result;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            source.Cancel();
            result = await invocation.WaitAsync(Deadline);
        }
        finally
        {
            source.Cancel();
            await invocation.WaitAsync(Deadline);
        }

        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(1, calls);
        Assert.Single(model.Requests);
        var paired = agent.Messages[^1].Content.OfType<ToolResultBlock>().ToArray();
        Assert.Equal(["first", "queued"], paired.Select(item => item.ToolUseId));
        Assert.All(paired, item => Assert.Equal(ToolResultStatus.Error, item.Status));
        Assert.Equal(agent.Messages.Select(message => message.TrackingId), store.Snapshot!.Messages.Select(message => message.TrackingId));
    }

    [Fact]
    public async Task SessionsSaveEveryMessageAndRestoreTrackingIdentifiers()
    {
        var store = new SnapshotStore();
        var model = new ScriptedModel(Call(("tool", "echo", "{}")), Text("answer"));
        var agent = new Agent(new()
        {
            Model = model,
            Tools = [Tool("echo", (_, _, _) => ValueTask.FromResult("tool output"))],
            SessionStore = store,
            SessionId = "session",
        });
        await agent.InvokeAsync("question").WaitAsync(Deadline);

        Assert.Equal([1, 2, 3, 4], store.Saves.Select(snapshot => snapshot.Messages.Count));
        var ids = agent.Messages.Select(message => message.TrackingId).ToArray();
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var snapshot in store.Saves)
            Assert.Equal(ids.Take(snapshot.Messages.Count), snapshot.Messages.Select(message => message.TrackingId));

        var resumedModel = new ScriptedModel(Text("resumed"));
        var resumed = new Agent(new()
        {
            Model = resumedModel,
            SessionStore = store,
            SessionId = "session",
            Messages = [Message.User("seed replaced by session")],
        });
        await resumed.InvokeAsync("follow-up").WaitAsync(Deadline);

        var request = Assert.Single(resumedModel.Requests);
        Assert.Equal(ids, request.Messages.Take(4).Select(message => message.TrackingId));
        Assert.Equal("follow-up", request.Messages[^1].Text);
        Assert.Equal([Role.User, Role.Assistant, Role.User, Role.Assistant, Role.User], request.Messages.Select(message => message.Role));
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task AgentAsToolCreatesAFreshConversationForEveryCall()
    {
        var store = new SnapshotStore();
        var hooks = new HookRegistry();
        var children = new List<Agent>();
        var state = new Dictionary<string, object?> { ["caller"] = "owned" };
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            Assert.Same(state, value.InvocationState);
            children.Add(value.Agent);
            return ValueTask.CompletedTask;
        });
        var model = new ScriptedModel(Text("first result"), Text("second result"));
        var specialist = new Agent(new()
        {
            Model = model,
            Name = "specialist",
            Hooks = hooks,
            Messages = [Message.User("existing conversation")],
            SessionStore = store,
            SessionId = "parent-session",
        });
        var parent = new Agent(new() { Model = new ScriptedModel(Text("unused")) });
        var tool = specialist.AsTool();

        foreach (var task in new[] { "first task", "second task" })
        {
            var input = JsonSerializer.SerializeToElement(new { task });
            var call = new ToolUseBlock(task, tool.Spec.Name, input);
            var result = await tool.InvokeAsync(input, new(parent, call, state)).AsTask().WaitAsync(Deadline);
            Assert.Equal(task == "first task" ? "first result" : "second result", result);
        }

        Assert.Equal(["first task", "second task"], model.Requests.Select(request => Assert.Single(request.Messages).Text));
        Assert.Equal("existing conversation", Assert.Single(specialist.Messages).Text);
        Assert.Equal(0, store.Loads);
        Assert.Empty(store.Saves);
        Assert.Equal(2, children.Count);
        Assert.NotSame(children[0], children[1]);
        Assert.All(children, child => { Assert.NotSame(specialist, child); Assert.Null(child.SessionId); });
        Assert.Single(state);
    }

    [Theory]
    [InlineData("before_start")]
    [InlineData("unclosed_block")]
    [InlineData("missing_stop")]
    [InlineData("invalid_tool_json")]
    [InlineData("duplicate_tool_ids")]
    [InlineData("tool_stop_mismatch")]
    [InlineData("after_stop")]
    [InlineData("negative_usage")]
    [InlineData("negative_reported_total")]
    [InlineData("overflowing_total")]
    [InlineData("overflowing_cumulative_usage")]
    public async Task MalformedModelStreamsFailAndReleaseTheInvocationGuard(string scenario)
    {
        ModelStreamEvent[] malformed = scenario switch
        {
            "before_start" => [new ModelContentBlockStartEvent(0)],
            "unclosed_block" => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0), new ModelMessageStopEvent(StopReasons.EndTurn)],
            "missing_stop" => Text("incomplete")[..^2],
            "invalid_tool_json" => Call(("bad", "work", "{")),
            "duplicate_tool_ids" => Call(("same", "work", "{}"), ("same", "work", "{}")),
            "tool_stop_mismatch" => [.. Call(("bad", "work", "{}"))[..^1], new ModelMessageStopEvent(StopReasons.EndTurn)],
            "after_stop" => [.. Text("stopped"), new ModelContentBlockStartEvent(1)],
            "negative_usage" => [new ModelMessageStartEvent(), new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(new(-1, 0))],
            "negative_reported_total" => [new ModelMessageStartEvent(), new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(new(1, 1, ReportedTotalTokens: -1))],
            "overflowing_total" => [new ModelMessageStartEvent(), new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(new(long.MaxValue, 1))],
            "overflowing_cumulative_usage" => [new ModelMessageStartEvent(), new ModelMessageStopEvent(StopReasons.EndTurn), new ModelMetadataEvent(new(long.MaxValue)), new ModelMetadataEvent(new(1))],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var model = new ScriptedModel(malformed, Text("recovered"));
        var agent = new Agent(new() { Model = model });

        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("malformed").WaitAsync(Deadline));

        Assert.Equal(Role.User, Assert.Single(agent.Messages).Role);
        Assert.Equal("recovered", (await agent.InvokeAsync(null).WaitAsync(Deadline)).ToString());
        Assert.Equal(2, model.Requests.Count);
    }

    [Fact]
    public async Task ToolInputOutlivesTheProviderJsonDocument()
    {
        string? received = null;
        var tool = Tool("echo", (input, _, _) =>
        {
            received = input.GetProperty("value").GetString();
            return ValueTask.FromResult(received!);
        });
        var model = new DocumentOwnedInputModel();
        var agent = new Agent(new() { Model = model, Tools = [tool] });

        List<IAgentStreamEvent> events = [];
        using var deadline = new CancellationTokenSource(Deadline);
        await foreach (var item in agent.StreamAsync("echo", cancellationToken: deadline.Token)) events.Add(item);
        Assert.Equal("done", Assert.Single(events.OfType<AgentResultEvent>()).Result.ToString());

        Assert.Equal("provider-owned", received);
        Assert.Equal("provider-owned", agent.Messages[1].Content.OfType<ToolUseBlock>().Single().Input.GetProperty("value").GetString());
        var streamed = events.OfType<ModelStreamUpdateEvent>().Select(item => item.Event).OfType<ModelContentBlockStartEvent>().Single(item => item.ToolUse is not null);
        Assert.Equal("provider-owned", streamed.ToolUse!.Input.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(ToolExecutionStrategy.Sequential)]
    [InlineData(ToolExecutionStrategy.Concurrent)]
    public async Task AfterToolHookFailureRetainsTheCompletedToolResult(ToolExecutionStrategy strategy)
    {
        var writes = 0;
        var hooks = new HookRegistry();
        hooks.Register<AfterToolCallEvent>((_, _) => ValueTask.FromException(new InvalidOperationException("after hook failed")));
        var model = new ScriptedModel(Call(("written", "save", "{}")), Text("recovered"));
        var tool = Tool("save", (_, _, _) => { writes++; return ValueTask.FromResult("saved successfully"); });
        var agent = new Agent(new() { Model = model, Tools = [tool], Hooks = hooks, ToolExecution = strategy });

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("save").WaitAsync(Deadline));

        Assert.Equal(1, writes);
        var retained = Assert.IsType<ToolResultBlock>(Assert.Single(agent.Messages[^1].Content));
        Assert.Equal("written", retained.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, retained.Status);
        Assert.Equal("saved successfully", retained.Content);
        Assert.Equal("recovered", (await agent.InvokeAsync(null).WaitAsync(Deadline)).ToString());
    }

    private static FunctionTool Tool(string name, Func<JsonElement, ToolContext, CancellationToken, ValueTask<string>> body) =>
        new(new(name, "A deterministic test capability", JsonSerializer.SerializeToElement(new { type = "object" })), body);

    private static ModelStreamEvent[] IndexedCalls() => Call(("call-0", "work", "{\"index\":0}"), ("call-1", "work", "{\"index\":1}"), ("call-2", "work", "{\"index\":2}"));
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<bool>[] Gates(int count) => Enumerable.Range(0, count).Select(_ => Gate()).ToArray();
    private static void ReleaseAll(IEnumerable<TaskCompletionSource<bool>> gates)
    {
        foreach (var gate in gates) gate.TrySetResult(true);
    }

    private sealed class SnapshotStore : ISessionStore
    {
        public SessionSnapshot? Snapshot { get; private set; }
        public List<SessionSnapshot> Saves { get; } = [];
        public int Loads { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Loads++;
            return ValueTask.FromResult(Snapshot);
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshot = snapshot with { Messages = snapshot.Messages.ToArray() };
            Saves.Add(Snapshot);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PausedModel : IModel
    {
        private int _requests;
        public TaskCompletionSource<bool> Disposed { get; } = Gate();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) > 1)
            {
                foreach (var item in Text("next")) yield return item;
                yield break;
            }
            try
            {
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "partial");
                await Gate().Task.WaitAsync(cancellationToken);
            }
            finally { Disposed.TrySetResult(true); }
        }
    }

    private sealed class DocumentOwnedInputModel : IModel
    {
        private int _requests;
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (Interlocked.Increment(ref _requests) > 1)
            {
                foreach (var item in Text("done")) yield return item;
                yield break;
            }
            using var document = JsonDocument.Parse("""{"value":"provider-owned"}""");
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new("provider-id", "echo", document.RootElement));
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }
}
