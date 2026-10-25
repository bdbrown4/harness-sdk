using System.Text.Json;
using Classic = Strands.Agents.Conversation;

namespace Strands.Agents.Tests.Conversation;

public sealed class ClassicStateTests
{
    [Fact]
    public async Task PublicManagerRestoreCannotChangeStateDuringAnActiveInvocation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new Classic.SlidingWindowConversationManager(new() { PerTurn = 3 });
        var model = new ClassicConversationManagerTests.TextModel { BeforeStart = async () => { entered.TrySetResult(true); await release.Task; } };
        await using var agent = new Agent(new() { Model = model, ConversationManager = manager });
        var invocation = agent.InvokeAsync("active");
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            var state = manager.GetState();
            Assert.ThrowsAny<InvalidOperationException>(() => manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":99,"model_call_count":100}""")));
            Assert.True(JsonElement.DeepEquals(state, manager.GetState()));
        }
        finally { release.TrySetResult(true); await invocation; }
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":9,"model_call_count":4}"""));
        Assert.Equal(9, manager.RemovedMessageCount);
    }

    [Fact]
    public async Task NullTextSummaryIsRejectedBeforeInitializationAndOtherSnapshotMutation()
    {
        var manager = new Classic.SummarizingConversationManager();
        var model = new ClassicConversationManagerTests.TextModel();
        await using var agent = new Agent(new() { Model = model, ConversationManager = manager, SystemPrompt = "retained" });
        var state = JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SummarizingConversationManager","removed_message_count":99,"summary_message":{"role":"user","content":[{"type":"text","text":null}]}}""");
        var snapshot = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["conversationManagerState"] = state,
            ["systemPrompt"] = JsonSerializer.SerializeToElement("replacement")
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(snapshot));
        Assert.Equal("retained", agent.SystemPrompt);
        Assert.Equal(0, manager.RemovedMessageCount);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"__name__\":\"NullConversationManager\",\"removed_message_count\":1}")]
    [InlineData("{\"__name__\":\"SlidingWindowConversationManager\",\"removed_message_count\":-1}")]
    [InlineData("{\"__name__\":\"SlidingWindowConversationManager\",\"removed_message_count\":9223372036854775808}")]
    [InlineData("{\"__name__\":\"SlidingWindowConversationManager\",\"removed_message_count\":1.5}")]
    [InlineData("{\"__name__\":\"SlidingWindowConversationManager\",\"removed_message_count\":1,\"model_call_count\":\"2\"}")]
    public void MalformedManagerStateIsRejectedAtomically(string json)
    {
        var manager = new Classic.SlidingWindowConversationManager();
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":9,"model_call_count":4}"""));
        Assert.Throws<InvalidDataException>(() => manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal(9, manager.RemovedMessageCount);
        Assert.Equal(4, manager.ModelCallCount);
    }

    [Fact]
    public void LegacySlidingStateRestoresMissingCadenceAsZero()
    {
        var manager = new Classic.SlidingWindowConversationManager();
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":9,"model_call_count":4}"""));
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":10}"""));
        Assert.Equal(10, manager.RemovedMessageCount);
        Assert.Equal(0, manager.ModelCallCount);
    }

    [Fact]
    public void SummaryCheckpointAndRepositoryPrependOwnTheirJsonAndPreserveTrackingIdentity()
    {
        var original = Message.User("previous summary");
        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["__name__"] = "SummarizingConversationManager", ["removed_message_count"] = 12, ["summary_message"] = original }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manager = new Classic.SummarizingConversationManager();
        using (var producer = JsonDocument.Parse(json)) _ = manager.RestoreFromSession(producer.RootElement);
        var detached = manager.GetState();
        var restored = new Classic.SummarizingConversationManager();
        var prepend = Assert.Single(restored.RestoreFromSession(detached)!);
        Assert.Equal(original.TrackingId, prepend.TrackingId);
        Assert.Equal("previous summary", prepend.Text);
        Assert.Equal(12, restored.RemovedMessageCount);
    }

    [Fact]
    public void InvalidPreviousSummaryCannotPartiallyReplaceRemovalCount()
    {
        var manager = new Classic.SummarizingConversationManager();
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SummarizingConversationManager","removed_message_count":8,"summary_message":null}"""));
        var invalid = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["__name__"] = "SummarizingConversationManager", ["removed_message_count"] = 100, ["summary_message"] = Message.Assistant("invalid role") });
        Assert.Throws<InvalidDataException>(() => manager.RestoreFromSession(invalid));
        Assert.Equal(8, manager.RemovedMessageCount);
        Assert.Equal(JsonValueKind.Null, manager.GetState().GetProperty("summary_message").ValueKind);
    }

    [Fact]
    public async Task MissingAndNullSnapshotManagerFieldsPreserveTheConfiguredState()
    {
        var manager = new Classic.SlidingWindowConversationManager();
        manager.RestoreFromSession(JsonSerializer.Deserialize<JsonElement>("""{"__name__":"SlidingWindowConversationManager","removed_message_count":8,"model_call_count":2}"""));
        await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager });
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["state"] = JsonSerializer.SerializeToElement(new { app = 1 }) }));
        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["conversationManagerState"] = JsonSerializer.SerializeToElement<object?>(null) }));
        Assert.Equal(8, manager.RemovedMessageCount);
        Assert.Equal(2, manager.ModelCallCount);
    }

    [Fact]
    public async Task SnapshotSelectionAndOldProjectionRemainSeparateFromClassicState()
    {
        var manager = new Classic.NullConversationManager();
        await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager });
        var included = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.True(included.Data.ContainsKey("conversationManagerState"));
        var excluded = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session, Exclude = SnapshotFields.ConversationManagerState });
        Assert.False(excluded.Data.ContainsKey("conversationManagerState"));
        await using var old = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = new global::Strands.Agents.SlidingWindowConversationManager(2) });
        var projection = await old.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.False(projection.Data.ContainsKey("conversationManagerState"));
    }

    [Fact]
    public async Task AConfiguredManagerAndTheSameExplicitPluginRegisterOnlyOnce()
    {
        var manager = new Classic.SlidingWindowConversationManager(new() { PerTurn = 1 });
        await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager, Plugins = [manager] });
        await agent.InvokeAsync("parent");
        await using var child = agent.CreateChild();
        await child.InvokeAsync("child");
        Assert.Equal(1, manager.ModelCallCount);
        Assert.Equal(1, (await child.TakeSnapshotAsync(new() { Include = SnapshotFields.ConversationManagerState })).Data["conversationManagerState"].GetProperty("model_call_count").GetInt64());
    }

    [Fact]
    public async Task FirstClassContextManagerRetainsPrecedenceOverClassicManagement()
    {
        var manager = new Classic.SlidingWindowConversationManager(new() { WindowSize = 0, PerTurn = 1 });
        await using var agent = new Agent(new() { Model = new ClassicConversationManagerTests.TextModel(), ConversationManager = manager, Plugins = [manager], ContextManager = new(new() { Strategies = [], StashEnabled = false }) });
        await agent.InvokeAsync("retained");
        Assert.Equal(2, agent.Messages.Count);
        Assert.Equal(0, manager.ModelCallCount);
        Assert.False((await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session })).Data.ContainsKey("conversationManagerState"));
    }
}
