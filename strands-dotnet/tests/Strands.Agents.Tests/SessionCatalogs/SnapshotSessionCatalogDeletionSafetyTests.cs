using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Strands.Agents.Context;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

[Collection("SDK logging")]
public sealed class SnapshotSessionCatalogDeletionSafetyTests
{
    [Theory]
    [InlineData("outside")]
    [InlineData("prefix")]
    [InlineData("double-slash")]
    [InlineData("backslash")]
    [InlineData("parent")]
    [InlineData("trailing")]
    [InlineData("case")]
    [InlineData("null")]
    public async Task EveryReturnedCatalogKeyValidatesBeforeAnyDelete(string corruption)
    {
        var storage = new Store
        {
            OnList = (prefix, _) => new(new[] { prefix + "valid", corruption switch
        {
            "outside" => "other/keep", "prefix" => prefix, "double-slash" => prefix + "a//b", "backslash" => prefix + "a\\b",
            "parent" => prefix + "../keep", "trailing" => prefix + "directory/", "case" => "Selected/keep", _ => null!
        } })
        };
        var manager = new SnapshotSessionManager(storage, "selected");
        await using var agent = Create(manager);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DeleteSessionAsync().AsTask());
        Assert.Empty(storage.Deleted); storage.OnList = null;
        await manager.SaveSnapshotAsync(agent, true);
        await using var next = Create(manager, "next");
    }

    [Fact]
    public async Task CatalogListingIsDetachedAndDuplicatesDeleteOnlyOnce()
    {
        var keys = new List<string> { "selected/first", "selected/second", "selected/second" };
        var storage = new Store { OnList = (_, _) => new(keys) };
        storage.OnDelete = (key, _) => { if (key.EndsWith("first", StringComparison.Ordinal)) keys[1] = "other/keep"; return default; };
        await new SnapshotSessionManager(storage, "selected").DeleteSessionAsync();
        Assert.Equal(new[] { "selected/first", "selected/second" }, storage.Deleted);
    }

    [Fact]
    public async Task CancellationDuringBorrowedListingDetachmentPreventsEveryDelete()
    {
        using var caller = new CancellationTokenSource();
        var storage = new Store { OnList = (_, _) => new(new CancellingList(caller)) };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SnapshotSessionManager(storage, "selected").DeleteSessionAsync(caller.Token).AsTask());
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task ListingFailureWinsRacingCancellationAndReleasesManagerAndAgentAdmissions()
    {
        using var caller = new CancellationTokenSource(); var primary = new IOException("listing cause");
        var storage = new Store { OnList = (_, _) => { caller.Cancel(); throw primary; } };
        var manager = new SnapshotSessionManager(storage, "selected"); await using var agent = Create(manager);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => manager.DeleteSessionAsync(caller.Token).AsTask()));
        Assert.Empty(storage.Deleted); storage.OnList = null;
        await manager.SaveSnapshotAsync(agent, true); await using var next = Create(manager, "next");
    }

    [Fact]
    public async Task DeleteFailureKeepsItsIdentityAndDoesNotStartLaterRows()
    {
        using var caller = new CancellationTokenSource(); var primary = new IOException("delete cause");
        var storage = new Store { OnList = (_, _) => new(new[] { "selected/first", "selected/second" }), OnDelete = (_, _) => { caller.Cancel(); throw primary; } };
        var manager = new SnapshotSessionManager(storage, "selected"); await using var agent = Create(manager);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => manager.DeleteSessionAsync(caller.Token).AsTask()));
        Assert.Equal(new[] { "selected/first" }, storage.Deleted); storage.OnDelete = null; storage.OnList = null;
        await manager.SaveSnapshotAsync(agent, true);
    }

    [Fact]
    public async Task NoncooperativeListingPhysicallyJoinsBeforeCancellationReleasesReservations()
    {
        using var caller = new CancellationTokenSource(); var entered = Signal(); var release = Signal();
        var storage = new Store { OnList = async (_, token) => { Assert.Equal(caller.Token, token); entered.TrySetResult(true); await release.Task; return new[] { "selected/first" }; } };
        var manager = new SnapshotSessionManager(storage, "selected"); await using var agent = Create(manager); await agent.InitializeAsync();
        var deletion = manager.DeleteSessionAsync(caller.Token).AsTask();
        try
        {
            await Within(entered.Task); caller.Cancel(); Assert.False(deletion.IsCompleted);
            await Assert.ThrowsAsync<ConcurrentInvocationException>(() => agent.InvokeAsync("blocked"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteSessionAsync().AsTask());
            Assert.Throws<InvalidOperationException>(() => Create(manager, "next"));
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Empty(storage.Deleted);
        storage.OnList = null; await manager.SaveSnapshotAsync(agent, true);
    }

    [Fact]
    public async Task CallerSelectedNamespaceAndOtherSessionsRemainByteExact()
    {
        var storage = new InMemoryStorage(); var selected = storage.Namespace("tenant");
        await storage.WriteAsync("other-root/selected/keep", [1]); await selected.WriteAsync("other/keep", [2]);
        var manager = new SnapshotSessionManager(selected, "selected"); await using var agent = Create(manager);
        await manager.SaveSnapshotAsync(agent, false); await manager.SaveSnapshotAsync(agent, true);
        await manager.DeleteSessionAsync();
        Assert.Equal(new[] { "other-root/selected/keep", "tenant/other/keep" }, await storage.ListAsync());
        Assert.Equal(new byte[] { 1 }, await storage.ReadAsync("other-root/selected/keep"));
        Assert.Equal(new byte[] { 2 }, await selected.ReadAsync("other/keep"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownStashFailureUsesPythonDiagnosticProfileEvenWithFailedLogger(bool throwingLogger)
    {
        var previous = SdkLogging.Logger; var logger = new ProbeLogger { Throwing = throwingLogger }; SdkLogging.ConfigureLogging(logger);
        try
        {
            var primary = new IOException("stash unavailable"); var storage = new Store(); var context = new ContextManager(new() { Strategies = [] });
            var manager = new SnapshotSessionManager(storage, "selected"); await using var agent = Create(manager, context: context, storage: storage);
            await agent.InitializeAsync(); await manager.SaveSnapshotAsync(agent, true);
            storage.OnList = (prefix, _) => prefix.StartsWith("context/", StringComparison.Ordinal) ? throw primary : new(storage.Keys(prefix));
            await manager.DeleteSessionAsync(); Assert.Same(primary, logger.Error);
            storage.OnList = null; Assert.Empty(await storage.ListAsync("selected/")); Assert.NotEmpty(await storage.ListAsync("context/selected/"));
            Assert.False(logger.Disposed);
        }
        finally { SdkLogging.ConfigureLogging(previous); }
    }

    [Fact]
    public async Task IndependentStashCancellationIsNotSwallowedByBestEffortCleanup()
    {
        var expected = new OperationCanceledException("independent stash cancellation"); var storage = new Store();
        var context = new ContextManager(new() { Strategies = [] }); var manager = new SnapshotSessionManager(storage, "selected");
        await using var agent = Create(manager, context: context, storage: storage); await agent.InitializeAsync();
        storage.OnList = (prefix, _) => prefix.StartsWith("context/", StringComparison.Ordinal) ? throw expected : new(storage.Keys(prefix));
        Assert.Same(expected, await Assert.ThrowsAsync<OperationCanceledException>(() => manager.DeleteSessionAsync().AsTask()));
        storage.OnList = null; await manager.SaveSnapshotAsync(agent, true);
    }

    [Fact]
    public async Task ProviderPublicationWaitsForDeletionReleaseAndConflictReportingRunsOutsideTheMonitor()
    {
        var storage = new Store(); var manager = new SnapshotSessionManager(storage, "selected"); var provider = new Provider(Tool("provider"));
        await using var agent = new Agent(new() { Id = "first", Model = new NeverModel(), Plugins = [manager], Tools = [Tool("application")], ToolProviders = [provider], EnableToolProviderUpdates = true });
        await agent.InitializeAsync(); var previous = SdkLogging.Logger; var calls = 0; var observedMonitor = true; Exception? lifecycle = null; Exception? bind = null;
        var gate = typeof(Agent).GetField("_initializationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent)!;
        var logger = new ProbeLogger { Callback = () => { calls++; observedMonitor = Monitor.IsEntered(gate); lifecycle = Record.Exception(() => { _ = agent.ShutdownAsync(); }); bind = Record.Exception(() => Create(manager, "next")); } };
        SdkLogging.ConfigureLogging(logger);
        try
        {
            storage.OnList = (prefix, _) => { provider.Emit(Tool("application")); Assert.Equal(new[] { "application", "provider" }, agent.Tools.Select(tool => tool.Spec.Name)); return new(storage.Keys(prefix)); };
            await manager.DeleteSessionAsync();
            Assert.Equal(1, calls); Assert.False(observedMonitor); Assert.IsType<InvalidOperationException>(lifecycle); Assert.IsType<InvalidOperationException>(bind);
            Assert.Equal(new[] { "application", "provider" }, agent.Tools.Select(tool => tool.Spec.Name));
        }
        finally { storage.OnList = null; SdkLogging.ConfigureLogging(previous); }
        await using var next = Create(manager, "next"); await manager.SaveSnapshotAsync(agent, true);
    }

    private static Agent Create(SnapshotSessionManager manager, string id = "first", ContextManager? context = null, IStorage? storage = null) => new(new()
    { Id = id, Model = new NeverModel(), Messages = [Message.User(id), Message.Assistant("response")], Plugins = context is null ? [manager] : [manager, context], Storage = storage });
    private static ITool Tool(string name) => new FunctionTool(new(name, name, JsonSerializer.SerializeToElement(new { type = "object" })), (_, _, _) => new(name));
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class NeverModel : IModel { public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No model or host process may run."); }
    private sealed class CancellingList(CancellationTokenSource caller) : IReadOnlyList<string>
    {
        public int Count => 1; public string this[int index] => "selected/first";
        public IEnumerator<string> GetEnumerator() { caller.Cancel(); yield return "selected/first"; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Store : IStorage, IStorageCharacteristics
    {
        private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
        public bool IsNamespaced => true; public bool IsEphemeral => false;
        public Func<string, CancellationToken, ValueTask<IReadOnlyList<string>>>? OnList { get; set; }
        public Func<string, CancellationToken, ValueTask>? OnDelete { get; set; }
        public List<string> Deleted { get; } = [];
        public string[] Keys(string prefix) => _data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { _data[key] = (byte[])data.Clone(); return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => new(_data.TryGetValue(key, out var bytes) ? (byte[])bytes.Clone() : null);
        public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Deleted.Add(key); if (OnDelete is { } callback) await callback(key, cancellationToken); _data.Remove(key); }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => OnList is { } callback ? callback(prefix, cancellationToken) : new(Keys(prefix));
    }
    private sealed class ProbeLogger : ILogger, IDisposable
    {
        public bool Throwing { get; init; }
        public bool Disposed { get; private set; }
        public Exception? Error { get; private set; }
        public Action? Callback { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { Error = exception; Callback?.Invoke(); if (Throwing) throw new IOException("logger cause"); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Provider(ITool initial) : IToolProviderUpdates
    {
        private IObserver<IReadOnlyList<ITool>>? _observer;
        internal void Emit(ITool tool) => _observer!.OnNext([tool]);
        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => default;
        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default) => new(new[] { initial });
        public ValueTask<IAsyncDisposable> WatchToolsAsync(ToolProviderContext context, IObserver<IReadOnlyList<ITool>> observer, CancellationToken cancellationToken = default) { _observer = observer; observer.OnNext([initial]); return new(new Lease()); }
    }
    private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => default; }
}
