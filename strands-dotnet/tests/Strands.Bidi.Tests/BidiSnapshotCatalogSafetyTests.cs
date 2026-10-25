using System.Text;
using System.Text.Json;
using Strands.Agents;
using Strands.Agents.Bidi;
using Strands.Agents.Storage;
using static Strands.Bidi.Tests.BidiAgentTests;
using static Strands.Bidi.Tests.BidiRestartTests;
using static Strands.Bidi.Tests.WebSocketServer;

namespace Strands.Bidi.Tests;

public sealed class BidiSnapshotCatalogSafetyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    [InlineData("a:b")]
    public async Task IdentifiersAndCursorsRejectUnsafeValuesBeforeStorageEffects(string invalid)
    {
        var storage = new ProbeStorage();
        Assert.Throws<ArgumentException>(() => new BidiSnapshotSessionManager(storage, invalid));
        var manager = new BidiSnapshotSessionManager(storage);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        Assert.Throws<ArgumentException>(() => new BidiAgent(new() { Model = model, ToolAgent = host, AgentId = invalid }));
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.ListSnapshotIdsAsync(agent, startAfter: invalid));
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.RestoreSnapshotAsync(agent, invalid));
        Assert.Equal(0, storage.Reads); Assert.Equal(0, storage.Lists); Assert.Empty(storage.Writes);
    }

    [Fact]
    public async Task TriggerOnlyPolicyCannotBeConfiguredWithoutACatalogManager()
    {
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        Assert.Throws<ArgumentException>(() => new BidiAgent(new() { Model = model, ToolAgent = host, SessionSaveStrategy = BidiSessionSaveStrategy.Trigger }));
    }

    [Fact]
    public async Task TriggerFailureStillSavesLatestAndAFalseTriggerLeavesNoCheckpoint()
    {
        foreach (var fails in new[] { false, true })
        {
            var storage = new ProbeStorage();
            var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Trigger, snapshotTrigger: _ => fails ? throw new IOException("trigger") : false);
            await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
            await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager });
            await agent.StartAsync(); await agent.StopAsync();
            Assert.Equal(fails ? 1 : 0, storage.Writes.Count); Assert.Empty(await manager.ListSnapshotIdsAsync(agent));
            if (fails) Assert.EndsWith("/snapshot_latest.json", Assert.Single(storage.Writes).Key);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWriteFailurePreservesTheOriginalErrorAndOnlyTheSuccessfullyWrittenImmutableEntry(bool latestFails)
    {
        var storage = new ProbeStorage(); var failure = new IOException("backend write failed");
        storage.BeforeWrite = (key, _) => key.Contains(latestFails ? "snapshot_latest" : "immutable_history") ? throw failure : default;
        var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Trigger, snapshotTrigger: _ => true);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager, Messages = [Message.User("retained")] });
        await agent.StartAsync(); Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => agent.StopAsync()));
        var ids = await manager.ListSnapshotIdsAsync(agent); Assert.Equal(latestFails ? 1 : 0, ids.Count);
        Assert.Null(await storage.Inner.ReadAsync("session/default-session/scopes/agent/default/snapshots/snapshot_latest.json"));
        if (latestFails)
        {
            await using var restored = new BidiAgent(new() { Model = model, ToolAgent = host });
            Assert.True(await manager.RestoreSnapshotAsync(restored, ids[0])); Assert.Equal("retained", Assert.Single(restored.Messages).Text);
        }
    }

    [Fact]
    public async Task OneCaptureAndIndependentBuffersProtectLatestFromConcurrentStateChangesAndBackendMutation()
    {
        var storage = new ProbeStorage();
        var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Trigger, snapshotTrigger: _ => true);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager });
        agent.State.Set("version", 1);
        storage.AfterWrite = (key, bytes) => { if (key.Contains("immutable_history")) { agent.State.Set("version", 2); Array.Clear(bytes, 0, bytes.Length); } };
        await agent.StartAsync(); await agent.StopAsync();
        var ids = await manager.ListSnapshotIdsAsync(agent);
        await using var target = new BidiAgent(new() { Model = model, ToolAgent = host });
        Assert.True(await manager.RestoreSnapshotAsync(target)); Assert.Equal(1, target.State.Get<int>("version"));
        Assert.True(await manager.RestoreSnapshotAsync(target, Assert.Single(ids))); Assert.Equal(1, target.State.Get<int>("version"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"scope\":\"agent\",\"schemaVersion\":\"9\",\"data\":{}}")]
    [InlineData("{\"scope\":\"agent\",\"schemaVersion\":\"1.0\",\"data\":{\"systemPrompt\":42}}")]
    public async Task MalformedCatalogDataIsRejectedBeforeInitializationOrMutation(string json)
    {
        var storage = new ProbeStorage(); var manager = new BidiSnapshotSessionManager(storage);
        await storage.Inner.WriteAsync("session/default-session/scopes/agent/default/snapshots/snapshot_latest.json", Encoding.UTF8.GetBytes(json));
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, Messages = [Message.User("original")] });
        var initialized = 0; agent.Hooks.Register<BidiInitializedEvent>((_, _) => { initialized++; return default; });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await manager.RestoreSnapshotAsync(agent));
        Assert.Equal(0, initialized); Assert.Equal("original", Assert.Single(agent.Messages).Text); Assert.Equal(0, model.Starts);
    }

    [Fact]
    public async Task ActiveRestoreAndPrecancelledOperationsDoNotReadOrWriteStorage()
    {
        var storage = new ProbeStorage(); var manager = new BidiSnapshotSessionManager(storage);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host }); await agent.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.RestoreSnapshotAsync(agent));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.SaveSnapshotAsync(agent, false, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.ListSnapshotIdsAsync(agent, cancellationToken: cancellation.Token));
        Assert.Equal(0, storage.Reads); Assert.Equal(0, storage.Lists); Assert.Empty(storage.Writes); await agent.StopAsync();
    }

    [Fact]
    public async Task AgentNamespacesAndDeletionStayInsideTheSelectedStorageViewAndSession()
    {
        var root = new InMemoryStorage(); var storage = root.Namespace("tenant"); var manager = new BidiSnapshotSessionManager(storage, "first");
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using var first = new BidiAgent(new() { Model = model, ToolAgent = host, AgentId = "one", Messages = [Message.User("one")] });
        await using var second = new BidiAgent(new() { Model = model, ToolAgent = host, AgentId = "two", Messages = [Message.User("two")] });
        var one = Assert.IsType<string>(await manager.SaveSnapshotAsync(first, false)); var two = Assert.IsType<string>(await manager.SaveSnapshotAsync(second, false));
        Assert.Equal(new[] { one }, await manager.ListSnapshotIdsAsync(first)); Assert.Equal(new[] { two }, await manager.ListSnapshotIdsAsync(second));
        await root.WriteAsync("tenant/second/keep", [1]); await root.WriteAsync("outside/keep", [2]);
        await manager.DeleteSessionAsync();
        Assert.Equal(new[] { "outside/keep", "tenant/second/keep" }, await root.ListAsync());
    }

    [Fact]
    public async Task BeforeRestartSavesUserHookChangesBeforeSwappingAndResponseStopDoesNotTrigger()
    {
        var storage = new ProbeStorage(); var triggers = 0;
        var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Trigger, snapshotTrigger: _ => { triggers++; return true; });
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager });
        agent.Hooks.Register<BidiBeforeConnectionRestartEvent>((_, _) => { agent.State.Set("before", true); return default; });
        storage.BeforeWrite = (_, _) => { Assert.Equal(0, model.Restarts); Assert.True(agent.State.Get<bool>("before")); return default; };
        await agent.StartAsync(); model.Current.Emit(new BidiResponseStopEvent("completed"));
        await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await Within(Next())); Assert.IsType<BidiResponseStopEvent>(reader.Current); Assert.Equal(0, triggers);
        model.Current.Emit(new BidiConnectionTimeoutException("restart")); Assert.True(await Within(Next()));
        await Within(model.Reconnected.Task); Assert.Equal(1, triggers); Assert.Equal(2, storage.Writes.Count);
        storage.BeforeWrite = null; await agent.StopAsync(); Assert.Equal(2, triggers);
        async Task<bool> Next() => await reader.MoveNextAsync();
    }

    [Fact]
    public async Task ExplicitCatalogWritesDrainBeforeDisposalAndReentrantSavesFailInsteadOfDeadlocking()
    {
        var storage = new ProbeStorage(); var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new BidiSnapshotSessionManager(storage);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager });
        storage.BeforeWrite = async (_, _) => { entered.TrySetResult(true); await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.SaveSnapshotAsync(agent, false)); await release.Task; };
        var save = manager.SaveSnapshotAsync(agent, false).AsTask(); await Within(entered.Task);
        var dispose = agent.DisposeAsync().AsTask(); Assert.False(dispose.IsCompleted); release.TrySetResult(true);
        Assert.NotNull(await Within(save)); await dispose; Assert.Single(storage.Writes); Assert.Equal(0, model.Disposals);
    }

    [Fact]
    public void SnapshotHandlesRemainCanonicalUniqueAndMonotonicAcrossBursts()
    {
        var ids = Enumerable.Range(0, 5000).Select(_ => BidiSnapshotIds.New()).ToArray();
        Assert.All(ids, id => Assert.True(BidiSnapshotIds.IsValid(id))); Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
        Assert.False(BidiSnapshotIds.IsValid(ids[0].ToUpperInvariant())); Assert.False(BidiSnapshotIds.IsValid(ids[0] + "\n"));
    }

    [Fact]
    public async Task PrecancelledDeleteDoesNotEnterTheBorrowedBackend()
    {
        var storage = new ProbeStorage(); var manager = new BidiSnapshotSessionManager(storage);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.DeleteSessionAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken); Assert.Equal(0, storage.Lists);
    }

    [Fact]
    public async Task CancellationObservedAfterAWriteIsNotReturnedAsTypedSuccess()
    {
        using var cancellation = new CancellationTokenSource(); var storage = new ProbeStorage();
        storage.AfterWrite = (_, _) => cancellation.Cancel(); var manager = new BidiSnapshotSessionManager(storage);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel(); await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await manager.SaveSnapshotAsync(agent, false, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.Single(storage.Writes); Assert.Single(await manager.ListSnapshotIdsAsync(agent));
    }

    [Fact]
    public async Task StopKeepsItsOriginalModelFailureWhenTheCatalogAlsoFails()
    {
        var storage = new ProbeStorage(); var cleanup = new IOException("catalog save"); storage.BeforeWrite = (_, _) => throw cleanup;
        var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Stop);
        var primary = new IOException("model stop");
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel { StopError = primary };
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager }); await agent.StartAsync();
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.StopAsync()));
        Assert.Contains(cleanup, Assert.IsType<List<Exception>>(primary.Data["Strands.CleanupErrors"]));
    }

    [Fact]
    public async Task ABeforeRestartWriteFailurePreventsTheSwapAndUnblocksWaitingSendsWithTheSameError()
    {
        var storage = new ProbeStorage(); var primary = new IOException("before restart save"); storage.BeforeWrite = (_, _) => throw primary;
        var manager = new BidiSnapshotSessionManager(storage, saveLatestOn: BidiSessionSaveStrategy.Stop);
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new RestartModel();
        await using var agent = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager }); await agent.StartAsync();
        model.Current.Emit(new BidiConnectionTimeoutException("deadline")); await using var reader = agent.ReceiveAsync().GetAsyncEnumerator();
        Assert.True(await Within(Next())); Assert.IsType<BidiConnectionRestartEvent>(reader.Current);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(Next)); Assert.Equal(0, model.Restarts);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => agent.SendAsync(new BidiMessage([new TextBlock("waiting")]))));
        storage.BeforeWrite = null; await agent.StopAsync();
        async Task<bool> Next() => await reader.MoveNextAsync();
    }

    [Fact]
    public async Task LatestRestoreRetainsRepeatedHistoricalCallsAndRepairsOnlyThePendingTail()
    {
        var storage = new InMemoryStorage(); var manager = new BidiSnapshotSessionManager(storage);
        var prior = new ToolUseBlock("repeat", "work", JsonSerializer.SerializeToElement(new { }));
        var history = new Message[] { new(Role.Assistant, [prior]), new(Role.User, [new ToolResultBlock("repeat", "one")]), new(Role.Assistant, [prior]), new(Role.User, [new ToolResultBlock("repeat", "two")]), new(Role.Assistant, [prior with { ToolUseId = "pending" }]) };
        await using var host = new Agent(new() { Model = new NoCallsModel() }); await using var model = new ScriptedModel();
        await using (var source = new BidiAgent(new() { Model = model, ToolAgent = host, Messages = history })) { await manager.SaveSnapshotAsync(source, true); }
        await using var fresh = new BidiAgent(new() { Model = model, ToolAgent = host, SessionManager = manager }); await fresh.StartAsync();
        Assert.Equal(6, fresh.Messages.Count); Assert.Equal(new[] { "repeat", "repeat", "pending" }, fresh.Messages.SelectMany(message => message.Content).OfType<ToolUseBlock>().Select(call => call.ToolUseId));
        var repaired = Assert.Single(fresh.Messages.Last().Content.OfType<ToolResultBlock>()); Assert.Equal(ToolResultStatus.Error, repaired.Status); Assert.Contains("unknown", repaired.Content);
        await fresh.StopAsync();
    }

    private sealed class ProbeStorage : IStorage
    {
        internal InMemoryStorage Inner { get; } = new();
        internal int Reads; internal int Lists;
        internal List<(string Key, byte[] Data)> Writes { get; } = [];
        internal Func<string, byte[], ValueTask>? BeforeWrite;
        internal Action<string, byte[]>? AfterWrite;
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeWrite is { } before) await before(key, data);
            await Inner.WriteAsync(key, data, cancellationToken); Writes.Add((key, (byte[])data.Clone())); AfterWrite?.Invoke(key, data);
        }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { Reads++; return Inner.ReadAsync(key, cancellationToken); }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) { Lists++; return Inner.ListAsync(prefix, cancellationToken); }
    }
}
