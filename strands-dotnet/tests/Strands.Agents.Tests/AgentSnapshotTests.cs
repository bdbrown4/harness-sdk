using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class AgentSnapshotTests
{
    [Fact]
    public async Task SessionCheckpointRestoresMessagesStatePromptAndModelStateWithoutApplyingAppData()
    {
        await using var agent = new Agent(new() { Model = new EchoModel(), SystemPrompt = "original" });
        agent.State.Set("value", 1);
        agent.ModelState.Set("cursor", "old");
        await agent.InvokeAsync("first");
        var checkpoint = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session, AppData = new Dictionary<string, JsonElement> { ["app"] = JsonSerializer.SerializeToElement("opaque") } });
        agent.State.Set("value", 2);
        agent.ModelState.Set("cursor", "new");
        await agent.InvokeAsync("second");

        await agent.LoadSnapshotAsync(checkpoint);

        Assert.Equal(2, agent.Messages.Count);
        Assert.Equal("first", agent.Messages[0].Text);
        Assert.Equal(1, agent.State.Get<int>("value"));
        Assert.Equal("old", agent.ModelState.Get<string>("cursor"));
        Assert.Equal("original", agent.SystemPrompt);
        Assert.Null(agent.State.Get("app"));
        Assert.Equal("opaque", checkpoint.AppData["app"].GetString());
    }

    [Fact]
    public async Task FieldSelectionAppliesExclusionsLastAndAbsentFieldsRemainUntouched()
    {
        await using var source = new Agent(new() { Model = new EchoModel(), SystemPrompt = "source", Messages = [Message.User("source")] });
        source.State.Set("selected", 3);
        var checkpoint = await source.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session, Include = SnapshotFields.State, Exclude = SnapshotFields.Messages | SnapshotFields.SystemPrompt | SnapshotFields.ModelState | SnapshotFields.InterruptState });
        await using var target = new Agent(new() { Model = new EchoModel(), SystemPrompt = "target", Messages = [Message.User("target")] });
        target.ModelState.Set("keep", true);

        await target.LoadSnapshotAsync(checkpoint);

        Assert.Equal(new[] { "state" }, checkpoint.Data.Keys);
        Assert.Equal("target", target.SystemPrompt);
        Assert.Equal("target", Assert.Single(target.Messages).Text);
        Assert.True(target.ModelState.Get<bool>("keep"));
        Assert.Equal(3, target.State.Get<int>("selected"));
    }

    [Fact]
    public async Task ExplicitNullPromptClearsThePromptAndChildrenInheritTheRestoredValue()
    {
        await using var source = new Agent(new() { Model = new EchoModel() });
        var checkpoint = await source.TakeSnapshotAsync(new() { Include = SnapshotFields.SystemPrompt });
        await using var target = new Agent(new() { Model = new EchoModel(), SystemPrompt = "clear" });

        await target.LoadSnapshotAsync(checkpoint);
        await using var child = target.CreateChild();

        Assert.Null(target.SystemPrompt);
        Assert.Null(child.SystemPrompt);
    }

    [Fact]
    public async Task SnapshotJsonRoundTripOwnsAllValuesAfterProducerDisposal()
    {
        AgentSnapshot checkpoint;
        using (var document = JsonDocument.Parse("{\"nested\":{\"value\":1}}"))
        {
            await using var agent = new Agent(new() { Model = new EchoModel(), State = new Dictionary<string, JsonElement> { ["owned"] = document.RootElement } });
            checkpoint = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State, AppData = new Dictionary<string, JsonElement> { ["owned"] = document.RootElement } });
        }
        var json = JsonSerializer.Serialize(checkpoint);
        var restored = JsonSerializer.Deserialize<AgentSnapshot>(json)!;
        await using var target = new Agent(new() { Model = new EchoModel() });

        await target.LoadSnapshotAsync(restored);

        Assert.Equal(1, target.State.Get("owned")!.Value.GetProperty("nested").GetProperty("value").GetInt32());
        Assert.Equal(1, restored.AppData["owned"].GetProperty("nested").GetProperty("value").GetInt32());
        Assert.Equal(checkpoint.CreatedAt, restored.CreatedAt);
    }

    [Fact]
    public async Task MalformedHistoryCannotPartiallyReplaceStateOrCauseSessionReads()
    {
        var store = new CountingStore();
        await using var agent = new Agent(new() { Model = new EchoModel(), SessionStore = store, SessionId = "stored" });
        agent.State.Set("keep", 1);
        var invalid = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["state"] = JsonSerializer.SerializeToElement(new { keep = 2 }),
            ["messages"] = JsonSerializer.SerializeToElement(new[] { new Message(Role.User, [new ToolResultBlock("orphan", "invalid")]) })
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(invalid).AsTask());

        Assert.Equal(1, agent.State.Get<int>("keep"));
        Assert.Empty(agent.Messages);
        Assert.Equal(0, store.Loads);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task InitializationPrecedesPartialRestorationAndLoadingDoesNotWriteTheSession()
    {
        var store = new CountingStore { Snapshot = new(1, [Message.User("stored")]) { State = new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(1) } } };
        await using var agent = new Agent(new() { Model = new EchoModel(), SessionStore = store, SessionId = "stored" });

        await agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["state"] = JsonSerializer.SerializeToElement(new { value = 2 }) }));

        Assert.Equal(1, store.Loads);
        Assert.Equal(0, store.Saves);
        Assert.Equal("stored", Assert.Single(agent.Messages).Text);
        Assert.Equal(2, agent.State.Get<int>("value"));
    }

    [Theory]
    [InlineData("multiAgent", "1.0")]
    [InlineData("agent", "2.0")]
    public async Task InvalidEnvelopesAreRejectedBeforeInitialization(string scope, string version)
    {
        var store = new CountingStore();
        await using var agent = new Agent(new() { Model = new EchoModel(), SessionStore = store, SessionId = "stored" });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>(), scope: scope, schemaVersion: version)).AsTask());
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task EmptyOrUnknownFieldSelectionsAreRejectedBeforeInitialization()
    {
        var store = new CountingStore();
        await using var agent = new Agent(new() { Model = new EchoModel(), SessionStore = store, SessionId = "stored" });
        await Assert.ThrowsAsync<ArgumentException>(() => agent.TakeSnapshotAsync(new()).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => agent.TakeSnapshotAsync(new() { Include = (SnapshotFields)128 }).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => agent.TakeSnapshotAsync(new() { Preset = (SnapshotPreset)128 }).AsTask());
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task ActiveInvocationsRejectCaptureAndRestorationWithoutChangingState()
    {
        var model = new WaitingModel();
        await using var agent = new Agent(new() { Model = model });
        using var cancellation = new CancellationTokenSource();
        var invocation = agent.InvokeAsync("wait", cancellationToken: cancellation.Token);
        await model.Started.Task;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["state"] = JsonSerializer.SerializeToElement(new { changed = true }) })).AsTask());
            Assert.Null(agent.State.Get("changed"));
        }
        finally { cancellation.Cancel(); await invocation; }
        Assert.Single((await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.Messages })).Data);
    }

    [Fact]
    public async Task RunningInitializationRejectsReentrantOrConcurrentSnapshotOperations()
    {
        var plugin = new WaitingPlugin();
        await using var agent = new Agent(new() { Model = new EchoModel(), Plugins = [plugin] });
        var initialization = agent.InitializeAsync();
        await plugin.Started.Task;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>())).AsTask());
        }
        finally { plugin.Release.TrySetResult(true); await initialization; }
        Assert.NotNull(await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }));
    }

    [Fact]
    public async Task TerminalShutdownRejectsCheckpointOperations()
    {
        await using var agent = new Agent(new() { Model = new EchoModel() });
        await agent.ShutdownAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>())).AsTask());
    }

    [Fact]
    public async Task CancellationAndMalformedCursorNeverStartInitializationOrMutateState()
    {
        var store = new CountingStore();
        await using var agent = new Agent(new() { Model = new EchoModel(), SessionStore = store, SessionId = "stored" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session }, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>()), cancellation.Token).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { Message.User("unacknowledged") }),
            ["modelHistoryCount"] = JsonSerializer.SerializeToElement(1),
            ["state"] = JsonSerializer.SerializeToElement(new { changed = true })
        })).AsTask());
        Assert.Equal(0, store.Loads);
        Assert.Null(agent.State.Get("changed"));
    }

    [Fact]
    public async Task AcknowledgedHistoryRequiresEffectiveProviderStateBeforeAnyMutation()
    {
        await using var agent = new Agent(new() { Model = new StatefulEchoModel() });
        var data = StatefulCheckpointData();
        data["modelState"] = JsonSerializer.SerializeToElement(new { });
        data["state"] = JsonSerializer.SerializeToElement(new { changed = true });

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(new(data)).AsTask());

        Assert.Empty(agent.Messages);
        Assert.Null(agent.State.Get("changed"));
        Assert.Empty(agent.ModelState.GetAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderStateCannotChangeIndependentlyOfAnAcknowledgedHistory(bool clear)
    {
        await using var agent = new Agent(new() { Model = new StatefulEchoModel() });
        await agent.LoadSnapshotAsync(new(StatefulCheckpointData()));
        var replacement = clear ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(new { responseId = "other" });

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(new(new Dictionary<string, JsonElement> { ["modelState"] = replacement })).AsTask());

        Assert.Equal("accepted", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(3, agent.Messages.Count);
    }

    [Fact]
    public async Task StatefulCheckpointsSendOnlyPendingOutputsWithTheirOriginalServerState()
    {
        var model = new StatefulEchoModel();
        await using var agent = new Agent(new() { Model = model });
        await agent.LoadSnapshotAsync(new(StatefulCheckpointData()));
        var checkpoint = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        await using var restored = new Agent(new() { Model = model });
        await restored.LoadSnapshotAsync(checkpoint);

        await restored.InvokeAsync(null);

        var request = Assert.Single(model.Requests);
        Assert.NotNull(request.ModelState);
        Assert.Equal("accepted", request.ModelState["responseId"].GetString());
        Assert.Equal("saved", Assert.Single(Assert.Single(request.Messages).Content.OfType<ToolResultBlock>()).Content);
        Assert.Empty(restored.Messages);
        Assert.Equal("next", restored.ModelState.Get<string>("responseId"));
    }

    [Fact]
    public async Task InvalidApplicationMetadataIsRejectedBeforeInitialization()
    {
        var store = new CountingStore();
        await using var agent = new Agent(new() { Model = new EchoModel(), SessionStore = store, SessionId = "stored" });

        await Assert.ThrowsAsync<ArgumentException>(() => agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session, AppData = new Dictionary<string, JsonElement> { ["invalid"] = default } }).AsTask());

        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public void CheckpointEnvelopesRejectUndefinedJson()
    {
        Assert.Throws<ArgumentException>(() => new AgentSnapshot(new Dictionary<string, JsonElement> { ["invalid"] = default }));
        Assert.Throws<ArgumentException>(() => new AgentSnapshot(new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement> { ["invalid"] = default }));
    }

    [Fact]
    public async Task ApplicationMetadataIsDetachedBeforeAwaitingInitialization()
    {
        var plugin = new WaitingPlugin();
        await using var agent = new Agent(new() { Model = new EchoModel(), Plugins = [plugin] });
        using var document = JsonDocument.Parse("{\"value\":1}");
        var metadata = new Dictionary<string, JsonElement> { ["owned"] = document.RootElement };
        var capture = agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session, AppData = metadata }).AsTask();
        await plugin.Started.Task;
        document.Dispose();
        metadata["owned"] = JsonSerializer.SerializeToElement(new { value = 2 });
        plugin.Release.TrySetResult(true);

        var checkpoint = await capture;

        Assert.Equal(1, checkpoint.AppData["owned"].GetProperty("value").GetInt32());
    }

    private static Dictionary<string, JsonElement> StatefulCheckpointData() => new()
    {
        ["messages"] = JsonSerializer.SerializeToElement(new Message[]
        {
            Message.User("save"),
            new(Role.Assistant, [new ToolUseBlock("call", "save", JsonSerializer.SerializeToElement(new { }))]),
            new(Role.User, [new ToolResultBlock("call", "saved")])
        }),
        ["modelHistoryCount"] = JsonSerializer.SerializeToElement(2),
        ["modelState"] = JsonSerializer.SerializeToElement(new { responseId = "accepted" })
    };

    private sealed class CountingStore : ISessionStore
    {
        internal int Loads, Saves;
        internal SessionSnapshot? Snapshot;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) { Loads++; return new(Snapshot); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Saves++; return default; }
    }

    private sealed class WaitingModel : IModel
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            yield return new ModelMessageStartEvent();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class StatefulEchoModel : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "reply");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn) { ModelState = new Dictionary<string, JsonElement> { ["responseId"] = JsonSerializer.SerializeToElement("next") } };
        }
    }

    private sealed class WaitingPlugin : IPlugin
    {
        public string Name => "test:waiting";
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Started.TrySetResult(true); await Release.Task; }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class EchoModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "reply");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }
}
