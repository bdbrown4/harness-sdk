using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Models;

namespace Strands.Agents.Tests;

public sealed class AgentStatefulModelTests
{
    [Fact]
    public async Task CommitsProviderStateSeparatelyAndClearsAcknowledgedHistoryBeforeAfterInvocation()
    {
        var model = new StatefulModel(Turn("one", "first"), Turn("two", "second"));
        var hooks = new HookRegistry();
        hooks.Register<AfterInvocationEvent>((value, _) => { Assert.Empty(value.Agent.Messages); return default; });
        var invocation = new Dictionary<string, object?> { ["caller"] = 42 };
        var agent = new Agent(new() { Model = model, Hooks = hooks });
        Assert.Equal("first", (await agent.InvokeAsync("go", new() { InvocationState = invocation })).ToString());
        Assert.Equal("one", agent.ModelState.Get<string>("responseId"));
        Assert.Empty(agent.State.GetAll());
        Assert.Single(invocation);
        await agent.InvokeAsync("next");
        Assert.Equal("one", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Equal("next", Assert.Single(model.Requests[1].Messages).Text);
        Assert.Equal("two", agent.ModelState.Get<string>("responseId"));
    }

    [Fact]
    public async Task ToolLoopSendsOnlyOutputsNotAlreadyAcknowledgedByServer()
    {
        var model = new StatefulModel(Call("one"), Turn("two", "finished"));
        var agent = new Agent(new() { Model = model, Tools = [Tool((_, _) => new ValueTask<string>("saved"))] });
        await agent.InvokeAsync("go");
        var pending = Assert.Single(model.Requests[1].Messages);
        var result = Assert.IsType<ToolResultBlock>(Assert.Single(pending.Content));
        Assert.Equal("call", result.ToolUseId);
        Assert.Equal("saved", result.Content);
        Assert.Equal("one", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task CancelledToolInvocationPersistsUnsentOutputsAndResumesWithoutRepeatingSideEffects()
    {
        var store = new SnapshotStore();
        using var cancellation = new CancellationTokenSource();
        var executions = 0;
        var tool = Tool((_, _) => { executions++; cancellation.Cancel(); return new ValueTask<string>("saved"); });
        var first = new Agent(new() { Model = new StatefulModel(Call("one")), Tools = [tool], SessionStore = store, SessionId = "conversation" });
        var result = await first.InvokeAsync("save", cancellationToken: cancellation.Token);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Equal(3, first.Messages.Count);
        Assert.Equal("one", first.ModelState.Get<string>("responseId"));
        Assert.Equal(2, store.Snapshot!.ModelHistoryCount);
        var secondModel = new StatefulModel(Turn("two", "acknowledged"));
        var second = new Agent(new() { Model = secondModel, Tools = [tool], SessionStore = store, SessionId = "conversation" });
        await second.InvokeAsync(null);
        Assert.Equal(1, executions);
        Assert.Equal("saved", Assert.IsType<ToolResultBlock>(Assert.Single(Assert.Single(secondModel.Requests).Messages).Content[0]).Content);
        Assert.Equal("one", secondModel.Requests[0].ModelState!["responseId"].GetString());
        Assert.Empty(second.Messages);
        Assert.Empty(store.Snapshot!.Messages);
        Assert.Equal(0, store.Snapshot.ModelHistoryCount);
    }

    [Fact]
    public async Task HookRejectedModelTurnCannotAdvanceStateOrAcknowledgedCursor()
    {
        var hooks = new HookRegistry();
        hooks.Register<AfterModelCallEvent>((value, _) => { value.Retry = value.Message?.Text == "discard"; return default; });
        var model = new StatefulModel(Turn("bad", "discard"), Turn("good", "accepted"));
        var agent = new Agent(new() { Model = model, Hooks = hooks, ModelState = State("prior") });
        await agent.InvokeAsync("go");
        Assert.Equal("prior", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Equal("go", Assert.Single(model.Requests[1].Messages).Text);
        Assert.Equal("good", agent.ModelState.Get<string>("responseId"));
    }

    [Fact]
    public async Task FailureKeepsPriorStateAndPendingUserInputForExplicitContinuation()
    {
        var failure = new IOException("stream failed");
        var model = new StatefulModel(Turn("one", "first"), new(AgentTests.Text("partial"), failure), Turn("three", "resumed"));
        var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("first input");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("second input")));
        Assert.Equal("one", agent.ModelState.Get<string>("responseId"));
        Assert.Equal("second input", Assert.Single(agent.Messages).Text);
        await agent.InvokeAsync(null);
        Assert.Equal("second input", Assert.Single(model.Requests[2].Messages).Text);
        Assert.Equal("one", model.Requests[2].ModelState!["responseId"].GetString());
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task RestoresProviderStateButDiscardsStaleMessagesFromEarlierSnapshot()
    {
        var store = new SnapshotStore { Snapshot = new(1, [Message.User("stale")]) { ModelState = State("restored") } };
        var model = new StatefulModel(Turn("new", "reply"));
        var agent = new Agent(new() { Model = model, SessionStore = store, SessionId = "conversation" });
        await agent.InitializeAsync();
        Assert.Empty(agent.Messages);
        await agent.InvokeAsync("fresh");
        Assert.Equal("restored", model.Requests[0].ModelState!["responseId"].GetString());
        Assert.Equal("fresh", Assert.Single(model.Requests[0].Messages).Text);
    }

    [Fact]
    public async Task DefaultChildStartsWithFreshProviderState()
    {
        var model = new StatefulModel(Turn("parent", "reply"));
        var parent = new Agent(new() { Model = model, ModelState = State("initial") });
        await parent.InvokeAsync("go");
        await using var child = parent.CreateChild();
        Assert.Empty(child.ModelState.GetAll());
        Assert.Empty(child.Messages);
    }

    [Fact]
    public void StatefulModelsRejectContextReductionAndRoutingIncludingRetryWrappers()
    {
        var model = new StatefulModel(Turn("one", "reply"));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = model, ConversationManager = new SlidingWindowConversationManager(5) }));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = model, ContextManager = new ContextManager() }));
        Assert.Throws<ArgumentException>(() => new Agent(new() { Model = model, Plugins = [new ContextManager()] }));
        Assert.True(Assert.IsAssignableFrom<IStatefulModel>(new RetryModel(model)).Stateful);
        Assert.Throws<ArgumentException>(() => new RoutingModel([new(new RetryModel(model))]));
    }

    [Fact]
    public async Task StopStateIsDetachedBeforeProducerMutationAndDisposal()
    {
        using var document = JsonDocument.Parse("\"original\"");
        var state = new Dictionary<string, JsonElement> { ["responseId"] = document.RootElement };
        var events = AgentTests.Text("reply");
        events[4] = new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = state };
        var model = new StatefulModel(new Script(events, AfterEvents: () => { state["responseId"] = JsonSerializer.SerializeToElement("changed"); document.Dispose(); }));
        var agent = new Agent(new() { Model = model });
        await agent.InvokeAsync("go");
        Assert.Equal("original", agent.ModelState.Get<string>("responseId"));
    }

    [Fact]
    public async Task DisposingAfterStopButBeforeAcceptedCompletionDoesNotCommitState()
    {
        var model = new StatefulModel(Turn("unaccepted", "partial"), Turn("accepted", "reply"));
        var agent = new Agent(new() { Model = model, ModelState = State("prior") });
        await using (var stream = agent.StreamAsync("go").GetAsyncEnumerator())
            while (await stream.MoveNextAsync())
                if (stream.Current is ModelStreamUpdateEvent { Event: ModelMessageStopEvent }) break;
        Assert.Equal("prior", agent.ModelState.Get<string>("responseId"));
        Assert.Equal("go", Assert.Single(agent.Messages).Text);
        await agent.InvokeAsync(null);
        Assert.Equal("prior", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Equal("go", Assert.Single(model.Requests[1].Messages).Text);
    }

    [Fact]
    public async Task TurnLimitKeepsUnsentToolOutputsForExplicitContinuation()
    {
        var executions = 0;
        var model = new StatefulModel(Call("one"), Turn("two", "reply"));
        var agent = new Agent(new() { Model = model, MaxTurns = 1, Tools = [Tool((_, _) => { executions++; return new ValueTask<string>("saved"); })] });
        Assert.Equal(StopReasons.MaxTurns, (await agent.InvokeAsync("go")).StopReason);
        Assert.Equal(3, agent.Messages.Count);
        await agent.InvokeAsync(null);
        Assert.Equal(1, executions);
        Assert.IsType<ToolResultBlock>(Assert.Single(model.Requests[1].Messages).Content[0]);
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task RequestTransformMayAppendMemoryButCannotReplaceProviderState()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) =>
        {
            var original = Assert.Single(value.Request.Messages);
            value.Request = value.Request with
            {
                Messages = [new(original.Role, original.Content.Concat(new[] { new TextBlock("memory") }).ToArray(), original.TrackingId)],
                ModelState = State("hook replacement")
            };
            return default;
        });
        var model = new StatefulModel(Turn("accepted", "reply"));
        await new Agent(new() { Model = model, Hooks = hooks, ModelState = State("prior") }).InvokeAsync("go");
        Assert.Equal(2, model.Requests[0].Messages[0].Content.Count);
        Assert.Equal("prior", model.Requests[0].ModelState!["responseId"].GetString());
    }

    [Fact]
    public async Task RequestTransformCannotDiscardUnacknowledgedInput()
    {
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((value, _) => { value.Request = value.Request with { Messages = [] }; return default; });
        var model = new StatefulModel(Turn("unused", "reply"));
        var agent = new Agent(new() { Model = model, Hooks = hooks });
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.InvokeAsync("must remain"));
        Assert.Empty(model.Requests);
        Assert.Equal("must remain", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task InvalidRestoredCursorCannotOverwriteUnreadSessionOrInitialState()
    {
        var snapshot = new SessionSnapshot(1, [Message.User("stored")]) { ModelHistoryCount = 2, ModelState = State("stored") };
        var store = new SnapshotStore { Snapshot = snapshot };
        var agent = new Agent(new() { Model = new StatefulModel(Turn("unused", "reply")), ModelState = State("initial"), SessionStore = store, SessionId = "conversation" });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InvokeAsync("go"));
        Assert.Same(snapshot, store.Snapshot);
        Assert.Equal("initial", agent.ModelState.Get<string>("responseId"));
        Assert.Empty(agent.Messages);
    }

    [Fact]
    public async Task InvalidCompletedProviderStateCannotReplacePriorState()
    {
        var events = AgentTests.Text("reply");
        events[4] = new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = new Dictionary<string, JsonElement> { ["invalid"] = default } };
        var model = new StatefulModel(new Script(events));
        var agent = new Agent(new() { Model = model, ModelState = State("prior") });
        await Assert.ThrowsAsync<ModelException>(() => agent.InvokeAsync("go"));
        Assert.Equal("prior", agent.ModelState.Get<string>("responseId"));
        Assert.Single(agent.Messages);
    }

    private static Script Turn(string id, string text)
    {
        var events = AgentTests.Text(text);
        events[4] = new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = State(id) };
        return new(events);
    }
    private static Script Call(string id)
    {
        var events = AgentTests.Call(("call", "save", "{}"));
        events[^1] = new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = State(id) };
        return new(events);
    }
    private static IReadOnlyDictionary<string, JsonElement> State(string id) => new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement(id) };
    private static FunctionTool Tool(Func<ToolContext, CancellationToken, ValueTask<string>> callback) => new(new("save", "Save", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, token) => callback(context, token));
    private sealed record Script(ModelStreamEvent[] Events, Exception? Failure = null, Action? AfterEvents = null);
    private sealed class StatefulModel(params Script[] turns) : IStatefulModel
    {
        public bool Stateful => true;
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var script = turns[Requests.Count - 1];
            foreach (var value in script.Events) { cancellationToken.ThrowIfCancellationRequested(); yield return value; await Task.Yield(); }
            script.AfterEvents?.Invoke();
            if (script.Failure is not null) throw script.Failure;
        }
    }
    private sealed class SnapshotStore : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshot = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot));
            return default;
        }
    }
}
