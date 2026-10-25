using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class AgentSystemPromptPersistenceTests
{
    private const string Projection = "persisted 雪\nlast\nline";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EmptyTypedSnapshotRoundTripKeepsAnExplicitArray()
    {
        await using var source = new Agent(new() { Model = new RecordingModel(), SystemPromptContent = [] });
        var snapshot = await source.TakeSnapshotAsync(new() { Include = SnapshotFields.SystemPrompt });
        Assert.Single(snapshot.Data);
        Assert.Equal(JsonValueKind.Array, snapshot.Data["systemPrompt"].ValueKind);
        Assert.Equal(0, snapshot.Data["systemPrompt"].GetArrayLength());
        var model = new RecordingModel();
        await using var target = new Agent(new() { Model = model, SystemPrompt = "fallback" });
        await target.LoadSnapshotAsync(snapshot);
        await target.InvokeAsync("question");
        Assert.Null(target.SystemPrompt);
        Assert.NotNull(target.SystemPromptContent);
        Assert.Empty(target.SystemPromptContent);
        Assert.Empty(Assert.Single(model.Requests).SystemPromptContent!);
        Assert.Null(model.Requests[0].SystemPrompt);
    }

    [Fact]
    public async Task CacheOnlySnapshotRoundTripKeepsMarkersAndANullProjection()
    {
        await using var source = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = [new CachePointBlock(), new CachePointBlock("")]
        });
        var snapshot = await source.TakeSnapshotAsync(new() { Include = SnapshotFields.SystemPrompt });
        var model = new RecordingModel();
        await using var target = new Agent(new() { Model = model, SystemPrompt = "fallback" });
        await target.LoadSnapshotAsync(snapshot);
        await target.InvokeAsync("question");
        Assert.Equal(JsonValueKind.Array, snapshot.Data["systemPrompt"].ValueKind);
        Assert.Null(target.SystemPrompt);
        Assert.Collection(Assert.Single(model.Requests).SystemPromptContent!,
            block => Assert.Null(Assert.IsType<CachePointBlock>(block).Ttl),
            block => Assert.Equal("", Assert.IsType<CachePointBlock>(block).Ttl));
        Assert.Null(model.Requests[0].SystemPrompt);
    }

    [Fact]
    public async Task MissingPromptSnapshotPreservesTheCurrentTypedRepresentation()
    {
        await using var agent = new Agent(new() { Model = new RecordingModel(), SystemPromptContent = Prompt() });
        await agent.LoadSnapshotAsync(Snapshot(null));
        AssertTyped(agent);
        Assert.Equal("replacement", agent.State.Get<string>("label"));
    }

    [Fact]
    public async Task LegacyEmptyStringSnapshotReplacesTheTypedRepresentation()
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        await agent.LoadSnapshotAsync(Snapshot(JsonSerializer.SerializeToElement("")));
        await agent.InvokeAsync("question");
        Assert.Equal("", agent.SystemPrompt);
        Assert.Null(agent.SystemPromptContent);
        Assert.Equal("", Assert.Single(model.Requests).SystemPrompt);
        Assert.Null(model.Requests[0].SystemPromptContent);
    }

    [Fact]
    public async Task ExplicitNullSnapshotClearsBothPromptRepresentations()
    {
        var model = new RecordingModel();
        await using var agent = new Agent(new() { Model = model, SystemPromptContent = Prompt() });
        await agent.LoadSnapshotAsync(Snapshot(JsonSerializer.SerializeToElement<string?>(null)));
        await agent.InvokeAsync("question");
        Assert.Null(agent.SystemPrompt);
        Assert.Null(agent.SystemPromptContent);
        Assert.Null(Assert.Single(model.Requests).SystemPrompt);
        Assert.Null(model.Requests[0].SystemPromptContent);
    }

    [Fact]
    public async Task InvalidTypedArraysRejectBeforeInitializationOrStateReplacement()
    {
        string[] invalidArrays =
        [
            "[null]",
            "[{\"type\":\"text\",\"text\":null}]",
            "[{\"type\":\"json\",\"json\":{}}]",
            "[{\"type\":\"unknown-system-block\"}]"
        ];
        foreach (var invalid in invalidArrays)
        {
            var probe = new InitializationProbe();
            var model = new RecordingModel();
            await using var agent = new Agent(new()
            {
                Model = model,
                SystemPromptContent = Prompt(),
                Plugins = [probe],
                State = State("configured")
            });
            using var document = JsonDocument.Parse(invalid);
            await Assert.ThrowsAsync<InvalidDataException>(() => agent.LoadSnapshotAsync(Snapshot(document.RootElement)).AsTask());
            Assert.Equal(0, probe.Initializations);
            Assert.Empty(model.Requests);
            Assert.Empty(agent.Messages);
            AssertTyped(agent);
            Assert.Equal("configured", agent.State.Get<string>("label"));
        }
    }

    [Fact]
    public async Task PreCanceledSnapshotRestoreDoesNotInitializeOrApplyPromptOrState()
    {
        var probe = new InitializationProbe();
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            Plugins = [probe],
            State = State("configured")
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.LoadSnapshotAsync(
            Snapshot(JsonSerializer.SerializeToElement("replacement")), cancellation.Token).AsTask());
        Assert.Equal(0, probe.Initializations);
        AssertTyped(agent);
        Assert.Equal("configured", agent.State.Get<string>("label"));
    }

    [Fact]
    public async Task CanceledSnapshotRestoreDoesNotApplyItsPreparedPromptWhileInitializationIsJoined()
    {
        var plugin = new GatedPlugin();
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            Plugins = [plugin],
            State = State("configured")
        });
        using var cancellation = new CancellationTokenSource();
        var restore = agent.LoadSnapshotAsync(Snapshot(JsonSerializer.SerializeToElement("replacement")), cancellation.Token).AsTask();
        var initialization = agent.InitializeAsync();
        try
        {
            await Within(plugin.Gate.Entered.Task);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
            Assert.False(initialization.IsCompleted);
            AssertTyped(agent);
            Assert.Equal("configured", agent.State.Get<string>("label"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => agent.DisposeAsync().AsTask());
        }
        finally
        {
            plugin.Gate.Release.TrySetResult(true);
            await ObserveCompletion(restore);
            await ObserveCompletion(initialization);
        }
        Assert.True(plugin.Gate.Exited.Task.IsCompleted);
        AssertTyped(agent);
        Assert.Equal("configured", agent.State.Get<string>("label"));
    }

    [Fact]
    public async Task CustomStoreRestoreDetachesTheFullTypedListBeforeAPluginAwait()
    {
        var supplied = Prompt();
        var store = new SessionStore { Snapshot = TypedSession(supplied) };
        var plugin = new GatedPlugin();
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPrompt = "configured",
            SessionId = "typed",
            SessionStore = store,
            Plugins = [plugin]
        });
        var initialization = agent.InitializeAsync();
        try
        {
            await Within(plugin.Gate.Entered.Task);
            supplied.Clear();
            supplied.Add(new TextBlock("mutated after restore"));
            AssertTyped(agent);
            AssertTyped(plugin.ObservedPrompt);
        }
        finally
        {
            plugin.Gate.Release.TrySetResult(true);
            await ObserveCompletion(initialization);
        }
        await initialization;
        Assert.True(plugin.Gate.Exited.Task.IsCompleted);
        AssertTyped(agent);
        Assert.NotSame(supplied, agent.SystemPromptContent);
        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public async Task CustomStoreMismatchedProjectionRejectsBeforePluginOrStateReplacement()
    {
        var store = new SessionStore { Snapshot = TypedSession(Prompt()) with { SystemPrompt = "mismatch", State = State("stored") } };
        var probe = new InitializationProbe();
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            SessionStore = store,
            SessionId = "typed",
            Plugins = [probe],
            State = State("configured")
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Equal(0, probe.Initializations);
        AssertTyped(agent);
        Assert.Equal("configured", agent.State.Get<string>("label"));
    }

    [Fact]
    public async Task LegacySessionWithoutAPromptPreservesTypedConfiguration()
    {
        var store = new SessionStore { Snapshot = new(1, []) { HasSystemPrompt = false, SystemPrompt = "ignored legacy field" } };
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            SessionStore = store,
            SessionId = "typed"
        });
        await agent.InitializeAsync();
        AssertTyped(agent);
    }

    [Fact]
    public async Task LegacySessionStringClearsConfiguredTypedContent()
    {
        var store = new SessionStore { Snapshot = new(1, []) { HasSystemPrompt = true, SystemPrompt = "legacy restored" } };
        var model = new RecordingModel();
        await using var agent = new Agent(new()
        {
            Model = model,
            SystemPromptContent = Prompt(),
            SessionStore = store,
            SessionId = "typed"
        });
        await agent.InvokeAsync("question");
        Assert.Equal("legacy restored", agent.SystemPrompt);
        Assert.Null(agent.SystemPromptContent);
        Assert.Equal("legacy restored", Assert.Single(model.Requests).SystemPrompt);
        Assert.Null(model.Requests[0].SystemPromptContent);
    }

    [Fact]
    public async Task ExplicitNullSessionClearsConfiguredTypedContent()
    {
        var store = new SessionStore { Snapshot = new(1, []) { HasSystemPrompt = true, SystemPrompt = null } };
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            SessionStore = store,
            SessionId = "typed"
        });
        await agent.InitializeAsync();
        Assert.Null(agent.SystemPrompt);
        Assert.Null(agent.SystemPromptContent);
    }

    [Fact]
    public async Task FreshFileSessionAgentKeepsTypedOrderAndCacheTtl()
    {
        using var directory = new TemporaryDirectory();
        await using (var source = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            SessionId = "typed",
            SessionStore = new FileSessionStore(directory.Path)
        }))
            await source.InvokeAsync("saved question");
        var saved = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory.Path).LoadAsync("typed"));
        Assert.True(saved.HasSystemPrompt);
        Assert.Equal(Projection, saved.SystemPrompt);
        AssertTyped(saved.SystemPromptContent);
        Assert.Equal(2, saved.Messages.Count);
        var model = new RecordingModel();
        await using var target = new Agent(new()
        {
            Model = model,
            SystemPrompt = "fallback",
            SessionId = "typed",
            SessionStore = new FileSessionStore(directory.Path)
        });
        await target.InitializeAsync();
        AssertTyped(target);
        Assert.Empty(model.Requests);
        await target.InvokeAsync("next question");
        AssertTyped(Assert.Single(model.Requests).SystemPromptContent);
        Assert.Equal(Projection, model.Requests[0].SystemPrompt);
    }

    [Fact]
    public async Task FileSessionStoreRejectsAMismatchWithoutReplacingThePriorRecord()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileSessionStore(directory.Path);
        var valid = TypedSession(Prompt());
        await store.SaveAsync("typed", valid);
        var path = Assert.Single(Directory.GetFiles(directory.Path, "*.json"));
        var before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("typed", valid with { SystemPrompt = "mismatch" }).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path));
        AssertTyped(Assert.IsType<SessionSnapshot>(await store.LoadAsync("typed")).SystemPromptContent);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task FileSessionStorePreservesAnExplicitEmptyTypedPrompt()
    {
        using var directory = new TemporaryDirectory();
        await new FileSessionStore(directory.Path).SaveAsync("empty", new(1, [])
        {
            HasSystemPrompt = true,
            SystemPromptContent = []
        });
        var saved = Assert.IsType<SessionSnapshot>(await new FileSessionStore(directory.Path).LoadAsync("empty"));
        Assert.True(saved.HasSystemPrompt);
        Assert.Null(saved.SystemPrompt);
        Assert.NotNull(saved.SystemPromptContent);
        Assert.Empty(saved.SystemPromptContent);
        await using var target = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            SessionId = "empty",
            SessionStore = new FileSessionStore(directory.Path)
        });
        await target.InitializeAsync();
        Assert.Null(target.SystemPrompt);
        Assert.NotNull(target.SystemPromptContent);
        Assert.Empty(target.SystemPromptContent);
    }

    [Fact]
    public async Task MemoryStorageCatalogLatestRestoresTypedContentIntoAFreshAgent()
    {
        var storage = new InMemoryStorage();
        var writer = new SnapshotSessionManager(storage, "typed-catalog");
        await using (var source = new Agent(new() { Id = "stable", Model = new RecordingModel(), SystemPromptContent = Prompt(), Plugins = [writer] }))
            Assert.Null(await writer.SaveSnapshotAsync(source, true));
        var reader = new SnapshotSessionManager(storage, "typed-catalog");
        var model = new RecordingModel();
        await using var target = new Agent(new() { Id = "stable", Model = model, SystemPrompt = "fallback", Plugins = [reader] });
        Assert.True(await reader.RestoreSnapshotAsync(target));
        AssertTyped(target);
        Assert.Empty(model.Requests);
        await target.InvokeAsync("question");
        AssertTyped(Assert.Single(model.Requests).SystemPromptContent);
    }

    [Fact]
    public async Task CatalogImmutableAndLatestShareTypedPayloadAndImmutableRestoreReplacesAString()
    {
        var storage = new ByteStorage();
        var triggers = 0;
        var manager = new SnapshotSessionManager(storage, "typed-catalog", SnapshotSessionSaveStrategy.Trigger, _ => { triggers++; return true; });
        await using var agent = new Agent(new() { Id = "stable", Model = new RecordingModel(), SystemPromptContent = Prompt(), Plugins = [manager] });
        await agent.InvokeAsync("question");
        Assert.Equal(1, triggers);
        Assert.Equal(2, storage.Attempts.Count);
        Assert.Contains("immutable_history/", storage.Attempts[0].Key);
        Assert.EndsWith("snapshot_latest.json", storage.Attempts[1].Key);
        var immutable = ReadCatalogSnapshot(storage.Attempts[0].Bytes);
        var latest = ReadCatalogSnapshot(storage.Attempts[1].Bytes);
        AssertTyped(immutable.SystemPromptContent);
        AssertTyped(latest.SystemPromptContent);
        Assert.Equal(Projection, immutable.SystemPrompt);
        Assert.Equal(Projection, latest.SystemPrompt);
        Assert.Equal(JsonSerializer.Serialize(immutable, Json), JsonSerializer.Serialize(latest, Json));
        var id = Assert.Single(await manager.ListSnapshotIdsAsync(agent));
        await agent.LoadSnapshotAsync(Snapshot(JsonSerializer.SerializeToElement("temporary string")));
        Assert.Null(agent.SystemPromptContent);
        Assert.True(await manager.RestoreSnapshotAsync(agent, id));
        AssertTyped(agent);
        Assert.Equal(2, storage.Attempts.Count);
    }

    [Fact]
    public async Task IndexedRepositoryFreshAgentRoundTripRetainsTypedMetadata()
    {
        var storage = new InMemoryStorage();
        var repository = new StorageSessionRepository(storage);
        var writer = new RepositorySessionManager("typed-repository", repository);
        await using (var source = new Agent(new() { Id = "stable", Model = new RecordingModel(), SystemPromptContent = Prompt(), Plugins = [writer] }))
            await source.InvokeAsync("saved question");
        var record = Assert.IsType<SessionAgentRecord>(await repository.ReadAgentAsync("typed-repository", "stable"));
        var metadata = record.InternalState.GetProperty("snapshot").Deserialize<SessionSnapshot>(Json)!;
        Assert.True(metadata.HasSystemPrompt);
        Assert.Equal(Projection, metadata.SystemPrompt);
        AssertTyped(metadata.SystemPromptContent);
        var reader = new RepositorySessionManager("typed-repository", new StorageSessionRepository(storage));
        var model = new RecordingModel();
        await using var target = new Agent(new() { Id = "stable", Model = model, SystemPrompt = "fallback", Plugins = [reader] });
        await target.InitializeAsync();
        AssertTyped(target);
        Assert.Equal(2, target.Messages.Count);
        Assert.Empty(model.Requests);
        await target.InvokeAsync("next question");
        AssertTyped(Assert.Single(model.Requests).SystemPromptContent);
        Assert.Equal(Projection, model.Requests[0].SystemPrompt);
    }

    [Fact]
    public async Task CanceledCatalogRestoreJoinsANoncooperativeReadWithoutApplyingThePrompt()
    {
        var storage = new ByteStorage();
        await SaveCatalogLatest(storage);
        var gate = new Gate();
        storage.BeforeRead = gate.PauseAsync;
        var manager = new SnapshotSessionManager(storage, "typed-catalog");
        var probe = new InitializationProbe();
        await using var target = new Agent(new()
        {
            Id = "stable",
            Model = new RecordingModel(),
            SystemPrompt = "configured",
            Plugins = [manager, probe],
            State = State("configured")
        });
        using var cancellation = new CancellationTokenSource();
        var restore = manager.RestoreSnapshotAsync(target, cancellationToken: cancellation.Token).AsTask();
        try
        {
            await Within(gate.Entered.Task);
            cancellation.Cancel();
            Assert.False(restore.IsCompleted);
            Assert.Equal("configured", target.SystemPrompt);
            Assert.Null(target.SystemPromptContent);
            Assert.Equal(0, probe.Initializations);
        }
        finally
        {
            gate.Release.TrySetResult(true);
            await ObserveCompletion(restore);
            storage.BeforeRead = null;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        Assert.True(gate.Exited.Task.IsCompleted);
        Assert.Equal("configured", target.SystemPrompt);
        Assert.Null(target.SystemPromptContent);
        Assert.Equal("configured", target.State.Get<string>("label"));
        Assert.Equal(0, probe.Initializations);
    }

    [Fact]
    public async Task TypedCatalogCleanupSaveRetainsTheInvocationPrimaryAndItsTypedPayload()
    {
        var primary = new IOException("model hook primary");
        var cleanup = new IOException("completion save cleanup");
        var storage = new ByteStorage { WriteFailure = cleanup };
        var manager = new SnapshotSessionManager(storage, "typed-catalog");
        var hooks = new HookRegistry();
        hooks.Register<BeforeModelCallEvent>((_, _) => throw primary);
        var model = new RecordingModel();
        await using var agent = new Agent(new()
        {
            Id = "stable",
            Model = model,
            SystemPromptContent = Prompt(),
            Plugins = [manager],
            Hooks = hooks
        });
        var error = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("accepted input"));
        Assert.Same(primary, error);
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"]));
        var attempted = ReadCatalogSnapshot(Assert.Single(storage.Attempts).Bytes);
        Assert.True(attempted.HasSystemPrompt);
        AssertTyped(attempted.SystemPromptContent);
        Assert.Equal(Projection, attempted.SystemPrompt);
        AssertTyped(agent);
        Assert.Empty(model.Requests);
        Assert.Empty(await storage.ListAsync());
    }

    [Fact]
    public async Task SessionLoadFailureRetainsConfiguredTypedStateAndDoesNotInitializePlugins()
    {
        var primary = new IOException("load primary");
        var store = new SessionStore { LoadFailure = primary };
        var probe = new InitializationProbe();
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPromptContent = Prompt(),
            SessionId = "typed",
            SessionStore = store,
            Plugins = [probe],
            State = State("configured")
        });
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
        Assert.Equal(1, store.Loads);
        Assert.Equal(0, probe.Initializations);
        AssertTyped(agent);
        Assert.Equal("configured", agent.State.Get<string>("label"));
    }

    [Fact]
    public async Task CatalogProjectionMismatchRejectsBeforeFreshAgentInitialization()
    {
        var storage = new ByteStorage();
        await SaveCatalogLatest(storage);
        var key = Assert.Single(await storage.ListAsync());
        var envelope = JsonNode.Parse((await storage.ReadAsync(key))!)!.AsObject();
        envelope["snapshot"]!.AsObject()["systemPrompt"] = "mismatch";
        await storage.WriteAsync(key, JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
        var manager = new SnapshotSessionManager(storage, "typed-catalog");
        var probe = new InitializationProbe();
        await using var target = new Agent(new()
        {
            Id = "stable",
            Model = new RecordingModel(),
            SystemPrompt = "configured",
            Plugins = [manager, probe],
            State = State("configured")
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.RestoreSnapshotAsync(target).AsTask());
        Assert.Equal(0, probe.Initializations);
        Assert.Equal("configured", target.SystemPrompt);
        Assert.Null(target.SystemPromptContent);
        Assert.Equal("configured", target.State.Get<string>("label"));
    }

    [Fact]
    public async Task IndexedRepositoryProjectionMismatchRejectsBeforePluginInitialization()
    {
        var repository = new StorageSessionRepository(new InMemoryStorage());
        var writer = new RepositorySessionManager("typed-repository", repository);
        await using (var source = new Agent(new() { Id = "stable", Model = new RecordingModel(), SystemPromptContent = Prompt(), Plugins = [writer] }))
            await source.InitializeAsync();
        var stored = Assert.IsType<SessionAgentRecord>(await repository.ReadAgentAsync("typed-repository", "stable"));
        var metadata = JsonNode.Parse(stored.InternalState.GetRawText())!.AsObject();
        metadata["snapshot"]!.AsObject()["systemPrompt"] = "mismatch";
        await repository.UpdateAgentAsync("typed-repository", new(stored.AgentId, stored.State, stored.ConversationManagerState,
            JsonSerializer.SerializeToElement(metadata, Json), stored.CreatedAt, stored.UpdatedAt));
        var reader = new RepositorySessionManager("typed-repository", repository);
        var probe = new InitializationProbe();
        await using var target = new Agent(new()
        {
            Id = "stable",
            Model = new RecordingModel(),
            SystemPrompt = "configured",
            Plugins = [reader, probe],
            State = State("configured")
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => target.InitializeAsync());
        Assert.Equal(0, probe.Initializations);
        Assert.Equal("configured", target.SystemPrompt);
        Assert.Null(target.SystemPromptContent);
        Assert.Equal("configured", target.State.Get<string>("label"));
    }

    [Fact]
    public async Task LateCustomStoreCancellationKeepsTheExistingAcceptedRestoreContract()
    {
        var gate = new Gate();
        var store = new SessionStore { Snapshot = TypedSession(Prompt()) with { State = State("stored") }, LoadGate = gate };
        var probe = new InitializationProbe();
        await using var agent = new Agent(new()
        {
            Model = new RecordingModel(),
            SystemPrompt = "configured",
            SessionId = "typed",
            SessionStore = store,
            Plugins = [probe],
            State = State("configured")
        });
        using var cancellation = new CancellationTokenSource();
        var initialization = agent.InitializeAsync(cancellation.Token);
        try
        {
            await Within(gate.Entered.Task);
            cancellation.Cancel();
            Assert.False(initialization.IsCompleted);
            Assert.Equal("configured", agent.SystemPrompt);
        }
        finally
        {
            gate.Release.TrySetResult(true);
            await ObserveCompletion(initialization);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
        Assert.True(gate.Exited.Task.IsCompleted);
        Assert.Equal(0, probe.Initializations);
        // Ordinary custom stores accept the returned snapshot before the existing
        // sandbox-initialization cancellation check. Initialization then cancels.
        AssertTyped(agent);
        Assert.Equal("stored", agent.State.Get<string>("label"));
    }

    private static List<ContentBlock> Prompt() => [new TextBlock("persisted 雪"), new CachePointBlock("caller-ttl"), new TextBlock("last\nline")];

    private static Dictionary<string, JsonElement> State(string label) => new() { ["label"] = JsonSerializer.SerializeToElement(label) };

    private static AgentSnapshot Snapshot(JsonElement? prompt)
    {
        var data = new Dictionary<string, JsonElement> { ["state"] = JsonSerializer.SerializeToElement(State("replacement")) };
        if (prompt is { } value) data["systemPrompt"] = value;
        return new(data);
    }

    private static SessionSnapshot TypedSession(IReadOnlyList<ContentBlock> content) => new(1, [])
    {
        HasSystemPrompt = true,
        SystemPrompt = Projection,
        SystemPromptContent = content
    };

    private static void AssertTyped(Agent agent)
    {
        Assert.Equal(Projection, agent.SystemPrompt);
        AssertTyped(agent.SystemPromptContent);
    }

    private static void AssertTyped(IReadOnlyList<ContentBlock>? content)
    {
        Assert.NotNull(content);
        Assert.Collection(content,
            block => Assert.Equal("persisted 雪", Assert.IsType<TextBlock>(block).Text),
            block => Assert.Equal("caller-ttl", Assert.IsType<CachePointBlock>(block).Ttl),
            block => Assert.Equal("last\nline", Assert.IsType<TextBlock>(block).Text));
    }

    private static async Task SaveCatalogLatest(IStorage storage)
    {
        var manager = new SnapshotSessionManager(storage, "typed-catalog");
        await using var source = new Agent(new() { Id = "stable", Model = new RecordingModel(), SystemPromptContent = Prompt(), Plugins = [manager] });
        Assert.Null(await manager.SaveSnapshotAsync(source, true));
    }

    private static SessionSnapshot ReadCatalogSnapshot(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("snapshot").Deserialize<SessionSnapshot>(Json)!;
    }

    private static async Task Within(Task task)
    {
        if (!ReferenceEquals(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))))
            throw new TimeoutException("The persistence test barrier did not settle within ten seconds.");
        await task;
    }

    private static async Task ObserveCompletion(Task task)
    {
        // The caller releases its gate first. Observe faults here so cleanup cannot replace
        // an assertion's primary failure; the normal path checks the operation separately.
        try { await task; }
        catch (Exception) { }
    }

    private sealed class Gate
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task PauseAsync()
        {
            Entered.TrySetResult(true);
            try { await Within(Release.Task); }
            finally { Exited.TrySetResult(true); }
        }
    }

    private sealed class GatedPlugin : IPlugin
    {
        internal Gate Gate { get; } = new();
        internal IReadOnlyList<ContentBlock>? ObservedPrompt { get; private set; }
        public string Name => "test:typed-prompt-gated-initialization";
        public async ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            ObservedPrompt = agent.SystemPromptContent;
            await Gate.PauseAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class InitializationProbe : IPlugin
    {
        internal int Initializations { get; private set; }
        public string Name => "test:typed-prompt-initialization-probe";
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            Initializations++;
            return default;
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class RecordingModel : IModel
    {
        internal List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "answer");
            yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn);
        }
    }

    private sealed class SessionStore : ISessionStore
    {
        internal SessionSnapshot? Snapshot { get; set; }
        internal Exception? LoadFailure { get; init; }
        internal Gate? LoadGate { get; init; }
        internal int Loads { get; private set; }
        public async ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Loads++;
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadGate is { } gate) await gate.PauseAsync();
            if (LoadFailure is { } failure) throw failure;
            return Snapshot;
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            return default;
        }
    }

    private sealed class ByteStorage : IStorage, IStorageCharacteristics
    {
        private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
        internal List<(string Key, byte[] Bytes)> Attempts { get; } = [];
        internal Func<Task>? BeforeRead { get; set; }
        internal Exception? WriteFailure { get; init; }
        public bool IsEphemeral => true;
        public bool IsNamespaced => true;
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = (byte[])data.Clone();
            Attempts.Add((key, copy));
            if (WriteFailure is { } failure) throw failure;
            _data[key] = copy;
            return default;
        }
        public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeRead is { } before) await before();
            return _data.TryGetValue(key, out var bytes) ? (byte[])bytes.Clone() : null;
        }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _data.Remove(key);
            return default;
        }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(_data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray());
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        internal string Path { get; }
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(_temporaryRoot, "strands-typed-prompt-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test directory is outside its selected temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
