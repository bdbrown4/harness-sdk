using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strands.Agents.Interventions;
using Strands.Agents.Middleware;

namespace Strands.Agents.Tests.EventParity;

public sealed class MessageAddedStateTests
{
    [Fact]
    public async Task AcceptedInputAndAssistantHooksAndStreamCarryTheExactOriginalBag()
    {
        var state = new Dictionary<string, object?> { ["request"] = new object() };
        var hooks = new HookRegistry(); var observed = new List<MessageAddedEvent>(); var streamed = new List<MessageAddedEvent>();
        hooks.Register<MessageAddedEvent>((value, _) => { observed.Add(value); return default; });
        var model = new EventTestModel(EventTestModel.Text("answer"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        await foreach (var value in agent.StreamAsync("question", new() { InvocationState = state }))
            if (value is MessageAddedEvent added) streamed.Add(added);

        Assert.Single(model.Requests); Assert.Equal(2, observed.Count); Assert.Equal(2, streamed.Count);
        Assert.Equal(agent.Messages.Select(message => message.TrackingId), observed.Select(value => value.Message.TrackingId));
        Assert.Equal(observed.Select(value => value.Message.TrackingId), streamed.Select(value => value.Message.TrackingId));
        Assert.All(observed.Concat(streamed), value => { Assert.Same(agent, value.Agent); Assert.Same(state, value.InvocationState); });
        Assert.Single(state);
    }

    [Fact]
    public async Task ARealToolResultCommitAndItsStreamObservationKeepTheOriginalBag()
    {
        var state = new Dictionary<string, object?> { ["request"] = new object() };
        var hooks = new HookRegistry(); var observed = new List<MessageAddedEvent>(); var streamed = new List<MessageAddedEvent>();
        hooks.Register<MessageAddedEvent>((value, _) => { observed.Add(value); return default; });
        var tool = new StateProbeTool();
        ModelStreamEvent[] calls = [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("call", "probe", JsonSerializer.SerializeToElement(new { }))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
        var model = new EventTestModel(calls, EventTestModel.Text("done"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Tools = new[] { tool } });

        await foreach (var value in agent.StreamAsync("question", new() { InvocationState = state }))
            if (value is MessageAddedEvent added) streamed.Add(added);

        Assert.Equal(2, model.Requests.Count); Assert.Equal(1, tool.Calls); Assert.Same(state, tool.State);
        Assert.Equal(4, observed.Count); Assert.Equal(4, streamed.Count);
        Assert.Equal(new[] { Role.User, Role.Assistant, Role.User, Role.Assistant }, observed.Select(value => value.Message.Role));
        Assert.Single(observed[2].Message.Content.OfType<ToolResultBlock>());
        Assert.Equal(observed.Select(value => value.Message.TrackingId), streamed.Select(value => value.Message.TrackingId));
        Assert.All(observed.Concat(streamed), value => { Assert.Same(agent, value.Agent); Assert.Same(state, value.InvocationState); });
        Assert.Single(state);
    }

    private sealed class StateProbeTool : ITool
    {
        internal int Calls;
        internal IReadOnlyDictionary<string, object?>? State;
        public ToolSpec Spec { get; } = new("probe", "State probe", JsonSerializer.SerializeToElement(new { type = "object" }));
        public ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
        { Calls++; State = context.InvocationState; return new("tool fact"); }
    }
}

public sealed class MessageAddedStateBoundaryTests
{
    [Fact]
    public async Task OldConstructionEqualityDeconstructionAndTheSoleConstructorRemainCompatible()
    {
        await using var agent = new Agent(new() { Model = new EventTestModel() }); var message = Message.User("old");
        var first = new MessageAddedEvent(agent, message); var second = new MessageAddedEvent(agent, message);
        first.Deconstruct(out var actualAgent, out var actualMessage);
        Assert.Same(agent, actualAgent); Assert.Same(message, actualMessage);
        Assert.Equal(first, second); Assert.Equal(first.GetHashCode(), second.GetHashCode()); Assert.Equal(second, first with { });
        Assert.Equal(2, Assert.Single(typeof(MessageAddedEvent).GetConstructors()).GetParameters().Length);
        Assert.Empty(first.InvocationState); Assert.Same(first.InvocationState, second.InvocationState);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)first.InvocationState).Add("not writable", 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvocationStateIsExcludedWithoutChangingTheOldJsonShape(bool bound)
    {
        await using var agent = new Agent(new() { Model = new EventTestModel() });
        var value = new MessageAddedEvent(agent, Message.User("old"));
        if (bound) value = value with { InvocationState = new Dictionary<string, object?> { ["delegate"] = (Action)(() => { }), ["agent"] = agent } };
        var options = new JsonSerializerOptions(); options.Converters.Add(new AgentConverter(agent));
        var text = JsonSerializer.Serialize(value, options); using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "Agent", "Message" }, json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(p => p, StringComparer.Ordinal));
        var restored = JsonSerializer.Deserialize<MessageAddedEvent>(text, options)!;
        Assert.Same(agent, restored.Agent); Assert.Equal("old", restored.Message.Text); Assert.Empty(restored.InvocationState);
    }

    [Theory]
    [InlineData("invocation")]
    [InlineData("model")]
    [InlineData("cache")]
    public async Task DeniedAndCachedMessagesUseTheOriginalInvocationState(string mode)
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        var model = new EventTestModel(); var middleware = new MiddlewareRegistry();
        if (mode == "invocation") hooks.Register<BeforeInvocationEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        else if (mode == "model") hooks.Register<BeforeModelCallEvent>((value, _) => { value.CancelReason = "denied"; return default; });
        else middleware.RegisterWrap(MiddlewareStages.InvokeModel, Cache);
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Middleware = middleware });
        Assert.Equal(mode == "cache" ? "cached" : "denied", (await agent.InvokeAsync("question", new() { InvocationState = state })).ToString());
        Assert.Empty(model.Requests); Assert.Equal(mode == "invocation" ? 1 : 2, seen.Count);
        MessageAddedStateHelpers.AssertState(seen, agent, state);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("protected")]
    [InlineData("both")]
    public async Task ManualAndProtectedContinuationsKeepTheSamePublicInvocationBag(string mode)
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen); var passes = 0; var appended = 0; var abandoned = 0;
        hooks.Register<BeforeInvocationEvent>((value, _) => { passes++; if (passes == 2 && mode == "protected") value.Messages = Array.Empty<Message>(); return default; });
        hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (passes == 1)
            {
                if (mode != "manual") value.Agent.AddContinuationInput(value, [Message.User("automatic")], () => { appended++; return default; }, _ => { abandoned++; return default; });
                value.Resume = new InvocationContinuation("manual");
            }
            return default;
        });
        var model = new EventTestModel(EventTestModel.Text("first"), EventTestModel.Text("second"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        await agent.InvokeAsync("initial", new() { InvocationState = state });
        Assert.Equal(2, passes); Assert.Equal(2, model.Requests.Count); Assert.Equal(mode == "manual" ? 0 : 1, appended); Assert.Equal(0, abandoned);
        Assert.Contains(seen, value => value.Message.Text == (mode == "manual" ? "manual" : "automatic"));
        if (mode == "protected") Assert.DoesNotContain(seen, value => value.Message.Text == "manual");
        if (mode == "both") Assert.Contains(seen, value => value.Message.Text == "manual");
        MessageAddedStateHelpers.AssertState(seen, agent, state);
    }

    [Fact]
    public async Task BeforeModelAdditionalMessagesAndInputReplacementUseTheOriginalBag()
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen); var appended = 0;
        hooks.Register<BeforeInvocationEvent>((value, _) => { value.Messages = [Message.User("replacement")]; return default; });
        hooks.Register<BeforeModelCallEvent>((value, _) =>
        { value.Agent.AddContinuationInput(value, [Message.User("additional")], () => { appended++; return default; }); return default; });
        var model = new EventTestModel(EventTestModel.Text("answer"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        await agent.InvokeAsync("secret", new() { InvocationState = state });
        Assert.Equal(new[] { "replacement", "additional" }, Assert.Single(model.Requests).Messages.Select(message => message.Text));
        Assert.Equal(1, appended); Assert.Equal(3, seen.Count); MessageAddedStateHelpers.AssertState(seen, agent, state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeforeAndAfterModelInterventionGuidanceKeepsExplicitEventState(bool after)
    {
        var state = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = MessageAddedStateHelpers.Capture(seen);
        var model = after ? new EventTestModel(EventTestModel.Text("rejected"), EventTestModel.Text("accepted")) : new EventTestModel(EventTestModel.Text("accepted"));
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Interventions = [new Guidance(after)] });
        Assert.Equal("accepted", (await agent.InvokeAsync("question", new() { InvocationState = state })).ToString());
        Assert.Equal(after ? 2 : 1, model.Requests.Count); Assert.Equal(3, seen.Count);
        Assert.Contains(seen, value => value.Message.Text == "[state-guide] feedback");
        Assert.DoesNotContain(seen, value => value.Message.Text == "rejected"); MessageAddedStateHelpers.AssertState(seen, agent, state);
    }

    [Fact]
    public async Task ASharedHookKeepsSimultaneousAgentsAndTheirBagsIsolated()
    {
        var stateA = MessageAddedStateHelpers.State(); var stateB = MessageAddedStateHelpers.State(); var seen = new List<MessageAddedEvent>(); var hooks = new HookRegistry();
        hooks.Register<MessageAddedEvent>((value, _) => { lock (seen) seen.Add(value); return default; });
        var enteredA = MessageAddedStateHelpers.Signal(); var enteredB = MessageAddedStateHelpers.Signal(); var release = MessageAddedStateHelpers.Signal();
        await using var agentA = new Agent(new() { Model = new GateModel(enteredA, release), Hooks = hooks });
        await using var agentB = new Agent(new() { Model = new GateModel(enteredB, release), Hooks = hooks });
        var callA = agentA.InvokeAsync("a", new() { InvocationState = stateA }); var callB = agentB.InvokeAsync("b", new() { InvocationState = stateB });
        try { await MessageAddedStateHelpers.Within(Task.WhenAll(enteredA.Task, enteredB.Task)); Assert.False(callA.IsCompleted); Assert.False(callB.IsCompleted); }
        finally { release.TrySetResult(true); await MessageAddedStateHelpers.Within(Task.WhenAll(callA, callB)); }
        Assert.Equal(4, seen.Count);
        MessageAddedStateHelpers.AssertState(seen.Where(value => ReferenceEquals(value.Agent, agentA)), agentA, stateA);
        MessageAddedStateHelpers.AssertState(seen.Where(value => ReferenceEquals(value.Agent, agentB)), agentB, stateB);
    }

    [Fact]
    public async Task AnExpiredApplicationCallbackCannotSupplyAmbientStateToAnotherAgent()
    {
        var stateA = MessageAddedStateHelpers.State(); var stateB = MessageAddedStateHelpers.State(); var seenA = new List<MessageAddedEvent>(); var seenB = new List<MessageAddedEvent>();
        using var callback = new CancellationTokenSource(); var registered = false; CancellationTokenRegistration registration = default; Task<AgentResult>? second = null;
        var hooksA = MessageAddedStateHelpers.Capture(seenA); var hooksB = MessageAddedStateHelpers.Capture(seenB);
        await using var agentB = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("b")), Hooks = hooksB });
        hooksA.Register<MessageAddedEvent>((_, _) => { if (!registered) { registered = true; registration = callback.Token.Register(() => second = agentB.InvokeAsync("b", new() { InvocationState = stateB })); } return default; });
        await using var agentA = new Agent(new() { Model = new EventTestModel(EventTestModel.Text("a")), Hooks = hooksA });
        try { await agentA.InvokeAsync("a", new() { InvocationState = stateA }); callback.Cancel(); await MessageAddedStateHelpers.Within(second!); }
        finally { registration.Dispose(); }
        Assert.Equal(2, seenA.Count); Assert.Equal(2, seenB.Count); MessageAddedStateHelpers.AssertState(seenA, agentA, stateA); MessageAddedStateHelpers.AssertState(seenB, agentB, stateB);
    }

    private static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Cache(InvokeModelContext context, MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    { await Task.CompletedTask; yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("cached"), StopReasons.EndTurn, new())); }

    private sealed class AgentConverter(Agent agent) : JsonConverter<Agent>
    {
        public override Agent Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) { reader.GetString(); return agent; }
        public override void Write(Utf8JsonWriter writer, Agent value, JsonSerializerOptions options) => writer.WriteStringValue("agent");
    }
    private sealed class Guidance(bool after) : InterventionHandler
    {
        public override string Name => "state-guide";
        public override ValueTask<IInterventionAction> BeforeModelCallAsync(BeforeModelCallEvent value, CancellationToken cancellationToken = default) => new(after ? new Proceed() : new Guide("feedback"));
        public override ValueTask<IInterventionAction> AfterModelCallAsync(AfterModelCallEvent value, CancellationToken cancellationToken = default) => new(after && value.Message?.Text == "rejected" ? new Guide("feedback") : new Proceed());
    }
    private sealed class GateModel(TaskCompletionSource<bool> entered, TaskCompletionSource<bool> release) : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { entered.TrySetResult(true); await release.Task.ConfigureAwait(false); foreach (var value in EventTestModel.Text("answer")) { cancellationToken.ThrowIfCancellationRequested(); yield return value; } }
    }
}

internal static class MessageAddedStateHelpers
{
    internal static Dictionary<string, object?> State() => new() { ["request"] = new object() };
    internal static HookRegistry Capture(List<MessageAddedEvent> seen)
    { var hooks = new HookRegistry(); hooks.Register<MessageAddedEvent>((value, _) => { seen.Add(value); return default; }); return hooks; }
    internal static void AssertState(IEnumerable<MessageAddedEvent> events, Agent agent, IReadOnlyDictionary<string, object?> state)
    { var values = events.ToArray(); Assert.NotEmpty(values); Assert.All(values, value => { Assert.Same(agent, value.Agent); Assert.Same(state, value.InvocationState); }); Assert.Single(state); }
    internal static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static async Task Within(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task.ConfigureAwait(false); }
    internal static ModelStreamEvent[] Call(string name = "probe") => [new ModelMessageStartEvent(), new ModelContentBlockStartEvent(0, new ToolUseBlock("call", name, JsonSerializer.SerializeToElement(new { }))), new ModelContentBlockStopEvent(0), new ModelMessageStopEvent(StopReasons.ToolUse)];
    internal static FunctionTool Tool(Func<ToolContext, string> callback, string name = "probe") => new(new(name, "State probe", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) => new ValueTask<string>(callback(context)));
}
