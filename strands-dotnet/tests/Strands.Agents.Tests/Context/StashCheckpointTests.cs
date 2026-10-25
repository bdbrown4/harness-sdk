using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.Context;

[CollectionDefinition("Stash checkpoint diagnostics", DisableParallelization = true)]
public sealed class StashCheckpointDiagnosticCollection;

[Collection("Stash checkpoint diagnostics")]
public sealed class StashCheckpointTests
{
    [Fact]
    public async Task DurableSessionSavesAnExternalTypeMarkerWithoutEmbeddingEntries()
    {
        var session = new SnapshotStore();
        await using var agent = new Agent(new()
        {
            Model = new TextModel(),
            SessionStore = session,
            SessionId = "session",
            ContextManager = new(new() { Strategies = [], StashStorage = new DurableStorage() })
        });

        await agent.InvokeAsync("task");

        Assert.Equal(nameof(DurableStorage), session.Snapshot!.ContextStashStorageType);
        Assert.Null(session.Snapshot.ContextStash);
    }

    [Fact]
    public async Task RestoringAnExternalMarkerWarnsWhenTheBackendTypeChanged()
    {
        var session = new SnapshotStore { Snapshot = new(1, []) { ContextStashStorageType = "previous-backend" } };
        var messages = new List<string>();
        using var listener = new CallbackListener(value => messages.Add(value));
        Trace.Listeners.Add(listener);
        try
        {
            await using var agent = Create(session, new DurableStorage());
            await agent.InitializeAsync();
            Assert.Contains(messages, value => value.Contains("previous-backend") && value.Contains(nameof(DurableStorage)));
        }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task AWholeInlineBatchIsValidatedBeforeTheFirstBackendWrite()
    {
        var storage = new DurableStorage();
        var stash = new Stash(storage, "session", "agent");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await stash.LoadSnapshotAsync(new Dictionary<string, JsonElement>
        { ["first"] = Entry("valid"), ["later"] = JsonSerializer.SerializeToElement<object?>(null) }));

        Assert.Empty(storage.Writes);
    }

    [Fact]
    public async Task PythonRestorePolicyKeepsTheConversationWhenOriginalStorageFails()
    {
        var failure = new IOException("stash restore");
        var storage = new DurableStorage { WriteFailure = failure };
        var session = new SnapshotStore { Snapshot = new(1, [Message.User("restored task")]) { ContextStash = new Dictionary<string, JsonElement> { ["original"] = Entry("original text") } } };
        await using var agent = Create(session, storage, StashRestoreFailurePolicy.Continue);

        await agent.InitializeAsync();

        Assert.Equal("restored task", Assert.Single(agent.Messages).Text);
        Assert.Contains(storage.Writes, value => value.EndsWith("/original", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConflictingExternalAndInlineStateIsRejectedBeforeHistoryOrStorageMutation()
    {
        var storage = new DurableStorage();
        var session = new SnapshotStore
        {
            Snapshot = new(1, [Message.User("replacement")])
            { ContextStash = new Dictionary<string, JsonElement> { ["original"] = Entry("value") }, ContextStashStorageType = "external" }
        };
        await using var agent = Create(session, storage, messages: [Message.User("configured")]);

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());

        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Empty(storage.Writes);
    }

    [Theory]
    [InlineData(StashRestoreFailurePolicy.Throw, false)]
    [InlineData(StashRestoreFailurePolicy.Continue, false)]
    [InlineData(StashRestoreFailurePolicy.Throw, true)]
    [InlineData(StashRestoreFailurePolicy.Continue, true)]
    public async Task RequestedCancellationAlwaysEscapesWithTheExactCallerToken(StashRestoreFailurePolicy policy, bool nonCancellationFailure)
    {
        using var cancellation = new CancellationTokenSource();
        Exception cause = nonCancellationFailure ? new IOException("canceled backend") : new OperationCanceledException("backend token", CancellationToken.None);
        var storage = new DurableStorage { OnWrite = (_, _, _) => { cancellation.Cancel(); throw cause; } };
        var session = new SnapshotStore { Snapshot = new(1, []) { ContextStash = new Dictionary<string, JsonElement> { ["original"] = Entry("text") } } };
        await using var agent = Create(session, storage, policy);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InitializeAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectStashCancellationNormalizationRetainsTheBackendCause(bool nonCancellationFailure)
    {
        using var cancellation = new CancellationTokenSource();
        Exception cause = nonCancellationFailure ? new IOException("canceled backend") : new OperationCanceledException("backend token", CancellationToken.None);
        var stash = new Stash(new DurableStorage { OnWrite = (_, _, _) => { cancellation.Cancel(); throw cause; } }, "session", "agent");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stash.LoadSnapshotAsync(new Dictionary<string, JsonElement> { ["original"] = Entry("text") }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public async Task TheDefaultPolicyRetainsTheOriginalBackendFailure()
    {
        var failure = new IOException("original backend");
        var session = new SnapshotStore { Snapshot = new(1, []) { ContextStash = new Dictionary<string, JsonElement> { ["original"] = Entry("text") } } };
        await using var agent = Create(session, new DurableStorage { WriteFailure = failure });
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InitializeAsync()));
    }

    [Fact]
    public async Task InlineJsonAndItsCollectionAreDetachedBeforeTheFirstStorageAwait()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var storage = new DurableStorage { OnWrite = async (_, _, _) => { if (first) { first = false; entered.TrySetResult(true); await release.Task; } } };
        var stash = new Stash(storage, "session", "agent");
        using var document = JsonDocument.Parse(Entry("original").GetRawText());
        var entries = new Dictionary<string, JsonElement> { ["first"] = document.RootElement, ["second"] = document.RootElement };
        var restoring = stash.LoadSnapshotAsync(entries).AsTask();
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(8000)));
            entries.Clear(); document.Dispose();
        }
        finally { release.TrySetResult(true); await restoring; }
        Assert.Equal("original", Assert.IsType<TextBlock>(await stash.RetrieveAsync("first")).Text);
        Assert.Equal("original", Assert.IsType<TextBlock>(await stash.RetrieveAsync("second")).Text);
    }

    [Fact]
    public async Task BackendFailuresRetainTheDocumentedPartialMergeWithoutDeletingUnrelatedEntries()
    {
        var failure = new IOException("second write");
        var storage = new DurableStorage { OnWrite = (key, _, _) => key.EndsWith("/second", StringComparison.Ordinal) ? throw failure : default };
        var stash = new Stash(storage, "session", "agent");
        await stash.LoadSnapshotAsync(new Dictionary<string, JsonElement> { ["unrelated"] = Entry("keep") });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await stash.LoadSnapshotAsync(new Dictionary<string, JsonElement>
        { ["first"] = Entry("accepted"), ["second"] = Entry("failed") })));

        Assert.Equal("accepted", Assert.IsType<TextBlock>(await stash.RetrieveAsync("first")).Text);
        Assert.Equal("keep", Assert.IsType<TextBlock>(await stash.RetrieveAsync("unrelated")).Text);
        Assert.Null(await stash.RetrieveAsync("second"));
    }

    [Fact]
    public async Task ThrowingDiagnosticListenersCannotUndoPythonContinueOrBackfill()
    {
        var failure = new IOException("diagnostic backend");
        var session = new SnapshotStore { Snapshot = new(1, [Message.User("kept")]) { ContextStash = new Dictionary<string, JsonElement> { ["original"] = Entry("text") } } };
        await using var agent = Create(session, new DurableStorage { WriteFailure = failure }, StashRestoreFailurePolicy.Continue);
        using var listener = new CallbackListener(_ => throw new InvalidOperationException("diagnostic listener"));
        Trace.Listeners.Add(listener);
        try { await agent.InitializeAsync(); Assert.Equal("kept", Assert.Single(agent.Messages).Text); }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExternalMetadataNeverReadsOrWritesAReplacementStash(bool sameBackend)
    {
        var storage = new DurableStorage();
        var session = new SnapshotStore { Snapshot = new(1, []) { ContextStashStorageType = sameBackend ? nameof(DurableStorage) : "previous" } };
        await using var agent = Create(session, storage);
        await agent.InitializeAsync();
        Assert.Equal(0, storage.Reads);
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public async Task FreshFileSessionRestoresLegacyInlineEntriesAndTheSavedAgentNamespace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-stash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = new SessionSnapshot(1, [Message.User("restored")])
            { AgentId = "saved-agent", ContextStash = new Dictionary<string, JsonElement> { ["original"] = Entry("original text") } };
            var (version, messages) = snapshot;
            Assert.Equal(1, version); Assert.Same(snapshot.Messages, messages);
            await new FileSessionStore(directory).SaveAsync("session", snapshot);
            var file = Assert.Single(Directory.GetFiles(directory));
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            legacy.Remove("contextStashStorageType");
            File.WriteAllText(file, legacy.ToJsonString());
            var storage = new DurableStorage();
            await using var agent = new Agent(new()
            {
                Model = new TextModel(),
                SessionId = "session",
                SessionStore = new FileSessionStore(directory),
                ContextManager = new(new() { Strategies = [], StashStorage = storage })
            });
            await agent.InitializeAsync();
            Assert.Equal("saved-agent", agent.Id);
            Assert.Equal("original text", Assert.IsType<TextBlock>(await agent.ContextManager!.Stash!.RetrieveAsync("original")).Text);
            Assert.Contains("context/session/scopes/agent/saved-agent/original", storage.Writes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    public async Task FileStoreRejectsInvalidExternalMetadataBeforeCreatingTheTarget(string marker)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-stash-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () => await new FileSessionStore(directory).SaveAsync("session", new(1, []) { ContextStashStorageType = marker }));
            Assert.False(Directory.Exists(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SavingADurableMarkerToARealFilePreservesTheAdditiveProperty()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-stash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(directory);
            await store.SaveAsync("session", new(1, []) { ContextStashStorageType = "external-backend" });
            var restored = await new FileSessionStore(directory).LoadAsync("session");
            Assert.Equal("external-backend", restored!.ContextStashStorageType);
            Assert.Null(restored.ContextStash);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PreCancelledLoadDoesNotInspectEntriesOrWriteToStorage()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var storage = new DurableStorage();
        var stash = new Stash(storage, "session", "agent");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stash.LoadSnapshotAsync(new Dictionary<string, JsonElement> { ["invalid"] = default }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public void UnknownRestorePoliciesAreRejectedAtConstruction()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ContextManager(new() { StashRestoreFailurePolicy = (StashRestoreFailurePolicy)999 }));

    [Theory]
    [InlineData("../escape")]
    [InlineData("not//canonical")]
    [InlineData("")]
    public async Task InvalidLaterKeysAreRejectedBeforeWritingEarlierEntries(string key)
    {
        var storage = new DurableStorage();
        var stash = new Stash(storage, "session", "agent");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await stash.LoadSnapshotAsync(new Dictionary<string, JsonElement> { ["first"] = Entry("valid"), [key] = Entry("invalid") }));
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public async Task MalformedInlineValuesCannotReplaceConfiguredHistoryEvenUnderContinue()
    {
        var session = new SnapshotStore { Snapshot = new(1, [Message.User("replacement")]) { ContextStash = new Dictionary<string, JsonElement> { ["original"] = JsonSerializer.SerializeToElement<object?>(null) } } };
        var storage = new DurableStorage();
        await using var agent = Create(session, storage, StashRestoreFailurePolicy.Continue, [Message.User("configured")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public async Task ExternalTypeDiagnosticsCannotFailInitializationWhenTheListenerThrows()
    {
        var session = new SnapshotStore { Snapshot = new(1, []) { ContextStashStorageType = "previous-backend" } };
        await using var agent = Create(session, new DurableStorage());
        using var listener = new CallbackListener(_ => throw new IOException("diagnostic listener"));
        Trace.Listeners.Add(listener);
        try { await agent.InitializeAsync(); }
        finally { Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task AnEphemeralCaptureFailureNeverPersistsAConversationWithMissingOriginals()
    {
        var failure = new IOException("snapshot read");
        var storage = new DurableStorage();
        var ephemeral = new EphemeralStorage(storage);
        await new Stash(ephemeral, "session", "saved-agent").StoreMessageAsync(Message.Assistant("original"));
        storage.ReadFailure = failure;
        var session = new SnapshotStore();
        await using var agent = new Agent(new()
        {
            Id = "saved-agent",
            Model = new TextModel(),
            SessionStore = session,
            SessionId = "session",
            ContextManager = new(new() { Strategies = [], StashStorage = ephemeral })
        });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("not safely captured")));

        Assert.Equal(0, session.Saves);
        Assert.Null(session.Snapshot);
    }

    [Fact]
    public async Task SessionAndStashDependenciesRemainBorrowedThroughAgentDisposal()
    {
        var session = new SnapshotStore();
        var storage = new DurableStorage();
        var agent = Create(session, storage);
        await agent.InitializeAsync();
        await agent.DisposeAsync();
        Assert.Equal(0, session.Disposals);
        Assert.Equal(0, storage.Disposals);
    }

    internal static JsonElement Entry(string text) => JsonSerializer.SerializeToElement<ContentBlock>(new TextBlock(text), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static Agent Create(SnapshotStore session, IStorage storage, StashRestoreFailurePolicy policy = StashRestoreFailurePolicy.Throw, IReadOnlyList<Message>? messages = null) => new(new()
    {
        Model = new TextModel(),
        SessionStore = session,
        SessionId = "session",
        Messages = messages ?? [],
        ContextManager = new(new() { Strategies = [], StashStorage = storage, StashRestoreFailurePolicy = policy })
    });
    private sealed class SnapshotStore : ISessionStore, IDisposable
    {
        internal SessionSnapshot? Snapshot;
        internal int Saves { get; private set; }
        internal int Disposals { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => new(Snapshot);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) { Snapshot = snapshot; Saves++; return default; }
        public void Dispose() => Disposals++;
    }
    private sealed class DurableStorage : IStorage, IDisposable
    {
        private readonly InMemoryStorage _inner = new();
        internal List<string> Writes { get; } = [];
        internal int Reads { get; private set; }
        internal Exception? WriteFailure { get; init; }
        internal Exception? ReadFailure { get; set; }
        internal int Disposals { get; private set; }
        internal Func<string, byte[], CancellationToken, ValueTask>? OnWrite { get; init; }
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        { Writes.Add(key); if (WriteFailure is { } failure) throw failure; if (OnWrite is { } callback) await callback(key, data, cancellationToken); await _inner.WriteAsync(key, data, cancellationToken); }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { Reads++; if (ReadFailure is { } failure) throw failure; return _inner.ReadAsync(key, cancellationToken); }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);
        public void Dispose() => Disposals++;
    }
    private sealed class EphemeralStorage(DurableStorage inner) : IStorage, IStorageCharacteristics
    {
        public bool IsEphemeral => true;
        public bool IsNamespaced => false;
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(key, data, cancellationToken);
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);
    }
    private sealed class TextModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent(); yield return new ModelContentBlockStartEvent(0);
            yield return new ModelContentBlockDeltaEvent(0, Text: "done"); yield return new ModelContentBlockStopEvent(0);
            yield return new ModelMessageStopEvent(StopReasons.EndTurn); await Task.Yield();
        }
    }
    private sealed class CallbackListener(Action<string> callback) : TraceListener
    { public override void Write(string? message) => callback(message ?? ""); public override void WriteLine(string? message) => callback(message ?? ""); }
}
