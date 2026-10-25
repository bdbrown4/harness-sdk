using System.Text.Json;
using System.Text.Json.Serialization;

namespace Strands.Agents.Tests.EventParity;

public sealed class BeforeInvocationInputTests
{
    [Fact]
    public async Task ReplacementAloneReachesModelHistoryObserversAndSession()
    {
        var hooks = new HookRegistry(); var store = new IncomingInputStore();
        var model = new EventTestModel(EventTestModel.Text("answer"));
        var state = new Dictionary<string, object?> { ["request"] = new object() };
        var replacement = Message.User("REDACTED"); var observed = new List<Message>();
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            Assert.Same(state, value.InvocationState);
            Assert.Empty(value.Agent.Messages);
            value.Messages = new[] { replacement };
            return default;
        });
        hooks.Register<MessageAddedEvent>((value, _) => { observed.Add(value.Message); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, SessionStore = store, SessionId = "incoming" });

        var result = await agent.InvokeAsync("SECRET", new() { InvocationState = state });

        var input = Assert.Single(Assert.Single(model.Requests).Messages);
        Assert.Equal("REDACTED", input.Text);
        Assert.Equal(replacement.TrackingId, input.TrackingId);
        Assert.Equal(new[] { "REDACTED", "answer" }, agent.Messages.Select(message => message.Text));
        Assert.Equal(agent.Messages, observed);
        Assert.Equal(agent.Messages, store.Snapshot!.Messages);
        Assert.DoesNotContain(store.Saves.SelectMany(snapshot => snapshot.Messages), message => message.Text.IndexOf("SECRET", StringComparison.Ordinal) >= 0);
        Assert.Same(state, result.InvocationState);
    }

    [Fact]
    public async Task AnExplicitEmptyBatchSuppressesTheIncomingPrompt()
    {
        var hooks = new HookRegistry(); var model = new EventTestModel(EventTestModel.Text("answer"));
        hooks.Register<BeforeInvocationEvent>((value, _) => { value.Messages = Array.Empty<Message>(); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        await agent.InvokeAsync("do not append");

        Assert.Empty(Assert.Single(model.Requests).Messages);
        Assert.Equal("answer", Assert.Single(agent.Messages).Text);
    }

    [Fact]
    public async Task TheInitialEventContainsTheDetachedOriginalTypedBatch()
    {
        var hooks = new HookRegistry(); var model = new EventTestModel(EventTestModel.Text("answer"));
        IReadOnlyList<Message>? observed = null;
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            observed = value.Messages;
            var message = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Message>>(value.Messages));
            Assert.Equal("original", message.Text);
            Assert.Equal(7, Assert.IsType<JsonBlock>(message.Content[1]).Json.GetProperty("value").GetInt32());
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });
        var json = JsonDocument.Parse("{\"value\":7}");
        ContentBlock[] content = [new TextBlock("original"), new JsonBlock(json.RootElement)];
        var stream = agent.StreamContentAsync(content);
        content[0] = new TextBlock("changed"); json.Dispose();

        await foreach (var _ in stream) { }

        Assert.NotNull(observed);
        Assert.Equal("original", Assert.Single(Assert.Single(model.Requests).Messages).Text);
    }

    [Fact]
    public async Task SuppressingManualResumeInputKeepsProtectedContinuationAndItsAppendReceipt()
    {
        var hooks = new HookRegistry(); var beforeCalls = 0; var appended = 0; var abandoned = 0;
        var model = new EventTestModel(EventTestModel.Text("first answer"), EventTestModel.Text("second answer"));
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            if (++beforeCalls == 2)
            {
                Assert.Equal("manual resume", Assert.Single(value.Messages!).Text);
                value.Messages = Array.Empty<Message>();
            }
            return default;
        });
        hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (beforeCalls == 1)
            {
                value.Agent.AddContinuationInput(value, new[] { Message.User("protected result") },
                    () => { appended++; return default; }, _ => { abandoned++; return default; });
                value.Resume = new InvocationContinuation("manual resume");
            }
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        await agent.InvokeAsync("first input");

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(new[] { "first input", "first answer", "protected result" }, model.Requests[1].Messages.Select(message => message.Text));
        Assert.DoesNotContain(agent.Messages, message => message.Text == "manual resume");
        Assert.Equal(1, appended); Assert.Equal(0, abandoned);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("reset")]
    [InlineData("empty")]
    [InlineData("replace")]
    public async Task EachManualResumeCycleSupportsNullResetEmptyAndReplacement(string mode)
    {
        var before = 0; var hooks = new HookRegistry(); var state = new Dictionary<string, object?>();
        var model = new EventTestModel(EventTestModel.Text("first answer"), EventTestModel.Text("second answer"));
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            Assert.Same(state, value.InvocationState);
            if (++before != 2) return default;
            Assert.Equal("manual", Assert.Single(value.Messages!).Text);
            if (mode != "null") value.Messages = new[] { Message.User("temporary") };
            return default;
        }, -1);
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            if (before == 2) value.Messages = mode switch
            {
                "null" or "reset" => null,
                "empty" => Array.Empty<Message>(),
                _ => new[] { Message.Assistant("replacement context"), Message.User("replacement input") }
            };
            return default;
        });
        hooks.Register<AfterInvocationEvent>((value, _) =>
        {
            if (before == 1) value.Resume = new InvocationContinuation("manual");
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        await agent.InvokeAsync("initial", new() { InvocationState = state });

        var expected = new List<string> { "initial", "first answer" };
        if (mode is "null" or "reset") expected.Add("manual");
        if (mode == "replace") expected.AddRange(new[] { "replacement context", "replacement input" });
        Assert.Equal(expected, model.Requests[1].Messages.Select(message => message.Text));
        Assert.DoesNotContain(agent.Messages, message => message.Text == "temporary");
        Assert.Equal(2, before); Assert.Empty(state);
    }

    [Theory]
    [InlineData("none", 0, 0)]
    [InlineData("text", 1, 1)]
    [InlineData("content", 1, 0)]
    public async Task NoInputEmptyTextAndEmptyContentRetainTheirDifferentOriginalBatches(string form, int count, int blocks)
    {
        var hooks = new HookRegistry(); var model = new EventTestModel(EventTestModel.Text("answer"));
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            Assert.Equal(count, value.Messages!.Count);
            if (count != 0) { Assert.Empty(value.Messages[0].Text); Assert.Equal(blocks, value.Messages[0].Content.Count); }
            value.Messages = null;
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        if (form == "content") await agent.InvokeContentAsync(Array.Empty<ContentBlock>());
        else await agent.InvokeAsync(form == "none" ? null : "");

        var messages = Assert.Single(model.Requests).Messages;
        Assert.Equal(count, messages.Count);
        if (count != 0) Assert.Equal(blocks, messages[0].Content.Count);
    }

    [Fact]
    public async Task TheWholeReplacementIsDetachedBeforeAnyMessageObserverCanMutateItsProducerList()
    {
        using var json = JsonDocument.Parse("{\"value\":7}");
        var first = new Message(Role.User, new ContentBlock[] { new JsonBlock(json.RootElement) }, "first", json.RootElement);
        var second = new Message(Role.User, new ContentBlock[] { new TextBlock("second") }, "second");
        var replacement = new List<Message> { first, second }; var hooks = new HookRegistry();
        var model = new EventTestModel(EventTestModel.Text("answer"));
        hooks.Register<BeforeInvocationEvent>((value, _) => { value.Messages = replacement; json.Dispose(); return default; });
        hooks.Register<MessageAddedEvent>((value, _) =>
        {
            if (value.Message.TrackingId == "first") replacement[1] = Message.User("producer changed");
            return default;
        });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks });

        await agent.InvokeAsync("original");

        var input = Assert.Single(model.Requests).Messages;
        Assert.Equal(2, input.Count); Assert.Equal("first", input[0].TrackingId); Assert.Equal("second", input[1].TrackingId);
        Assert.Equal(7, input[0].Metadata!.Value.GetProperty("value").GetInt32());
        Assert.Equal(7, Assert.IsType<JsonBlock>(Assert.Single(input[0].Content)).Json.GetProperty("value").GetInt32());
        Assert.Equal("second", input[1].Text); Assert.Equal("producer changed", replacement[1].Text);
    }

    [Fact]
    public async Task RestorePluginsAndInitializedObserversPrecedeTheReplacementWithoutErasingPluginState()
    {
        var order = new List<string>(); var store = new IncomingInputStore
        {
            Snapshot = new(1, new[] { Message.User("old"), Message.Assistant("saved") })
            { State = new Dictionary<string, JsonElement> { ["restored"] = JsonSerializer.SerializeToElement(42) } },
            OnLoad = () => order.Add("load")
        };
        var hooks = new HookRegistry(); var model = new EventTestModel(EventTestModel.Text("answer"));
        var plugin = new IncomingInputPlugin(agent =>
        {
            order.Add("plugin"); Assert.Equal(42, agent.State.Get<int>("restored"));
            agent.State.Set("plugin", "initialized"); Assert.Equal(2, agent.Messages.Count);
        });
        hooks.Register<InitializedEvent>((value, _) =>
        { order.Add("initialized"); Assert.Equal("initialized", value.Agent.State.Get<string>("plugin")); return default; });
        hooks.Register<BeforeInvocationEvent>((value, _) =>
        {
            order.Add("before"); Assert.Equal(2, value.Agent.Messages.Count);
            Assert.Equal("input", Assert.Single(value.Messages!).Text); value.Messages = new[] { Message.User("replacement") }; return default;
        });
        hooks.Register<BeforeModelCallEvent>((_, _) => { order.Add("model"); return default; });
        await using var agent = new Agent(new() { Model = model, Hooks = hooks, Plugins = [plugin], SessionStore = store, SessionId = "restored" });

        await agent.InvokeAsync("input");

        Assert.Equal(new[] { "load", "plugin", "initialized", "before", "model" }, order);
        Assert.Equal("replacement", model.Requests[0].Messages[2].Text);
        Assert.Equal("initialized", store.Snapshot!.State!["plugin"].GetString());
        Assert.Equal(42, store.Snapshot.State["restored"].GetInt32());
    }

    [Fact]
    public async Task TheOldTwoArgumentConstructorDeconstructionAndRecordCloneRemainCompatible()
    {
        await using var agent = new Agent(new() { Model = new EventTestModel() });
        var state = new Dictionary<string, object?>();
        var first = new BeforeInvocationEvent(agent, state); var second = new BeforeInvocationEvent(agent, state);
        first.Deconstruct(out var originalAgent, out var originalState);
        Assert.Same(agent, originalAgent); Assert.Same(state, originalState); Assert.Null(first.Messages);
        Assert.Equal(first, second); Assert.Equal(first.GetHashCode(), second.GetHashCode()); Assert.Equal(second, first with { });
    }

    [Fact]
    public async Task AnOldManuallyConstructedEventOmitsTheNewNullJsonMember()
    {
        await using var agent = new Agent(new() { Model = new EventTestModel() });
        var options = new JsonSerializerOptions(); options.Converters.Add(new IncomingAgentConverter());
        var value = new BeforeInvocationEvent(agent, new Dictionary<string, object?>());

        var json = JsonSerializer.SerializeToElement(value, options);

        Assert.False(json.TryGetProperty("Messages", out _));
        Assert.Equal(new[] { "Agent", "CancelReason", "InvocationState" }, json.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("agent", json.GetProperty("Agent").GetString());
    }

    private sealed class IncomingAgentConverter : JsonConverter<Agent>
    {
        public override Agent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, Agent value, JsonSerializerOptions options) => writer.WriteStringValue("agent");
    }

    private sealed class IncomingInputPlugin(Action<Agent> initialize) : IPlugin
    {
        public string Name => "incoming-input-test";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { initialize(agent); return default; }
        public IReadOnlyList<ITool> GetTools() => Array.Empty<ITool>();
    }
}

internal sealed class IncomingInputStore : ISessionStore
{
    internal SessionSnapshot? Snapshot { get; set; }
    internal List<SessionSnapshot> Saves { get; } = [];
    internal List<SessionSnapshot> Attempts { get; } = [];
    internal Action? OnLoad { get; set; }
    internal Func<SessionSnapshot, Exception?>? SaveFailure { get; set; }
    public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    { OnLoad?.Invoke(); return new(Snapshot); }
    public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Attempts.Add(snapshot);
        if (SaveFailure?.Invoke(snapshot) is { } error) throw error;
        Snapshot = snapshot; Saves.Add(snapshot); return default;
    }
}
