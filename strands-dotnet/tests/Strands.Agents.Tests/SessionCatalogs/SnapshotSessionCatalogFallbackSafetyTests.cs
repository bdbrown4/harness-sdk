using Microsoft.Extensions.Logging;
using Strands.Agents.Context;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionCatalogs;

[Collection("SDK logging")]
public sealed class SnapshotSessionCatalogFallbackSafetyTests
{
    [Fact]
    public async Task KnownCallerScopedStashSuppressesTheRawBackendFallback()
    {
        var raw = new InMemoryStorage(); var context = new ContextManager(new() { Strategies = [], StashStorage = raw.Namespace("shared") });
        var manager = new SnapshotSessionManager(raw, "selected");
        await using var agent = new Agent(new() { Id = "first", Model = new NeverModel(), Messages = [Message.User("original")], Plugins = [manager, context] });
        await agent.InitializeAsync(); await manager.SaveSnapshotAsync(agent, true);
        var sharedKeys = await raw.ListAsync("shared/"); Assert.NotEmpty(sharedKeys);
        await raw.WriteAsync("context/selected/unclaimed", [7]);
        await manager.DeleteSessionAsync();
        Assert.Equal(sharedKeys, await raw.ListAsync("shared/")); Assert.Equal(new byte[] { 7 }, await raw.ReadAsync("context/selected/unclaimed"));
        Assert.Empty(await raw.ListAsync("session/selected/"));
    }

    [Fact]
    public async Task UninitializedBindingWithoutKnownStashDoesNotInitializeToDiscoverOne()
    {
        var raw = new Store(); var manager = new SnapshotSessionManager(raw, "selected"); var plugin = new ReadingPlugin();
        await raw.WriteAsync("context/selected/original", [1]);
        await using var agent = new Agent(new() { Id = "first", Model = new NeverModel(), Plugins = [manager, plugin] });
        await manager.DeleteSessionAsync();
        Assert.Empty(raw.Keys("context/selected/")); Assert.Equal(0, plugin.Initializations); Assert.Equal(0, raw.Reads);
    }

    [Fact]
    public async Task FallbackDoesNotInferAnUninitializedAgentsDifferentStorageBackend()
    {
        var raw = new Store(); var other = new Store(); var manager = new SnapshotSessionManager(raw, "selected");
        await raw.WriteAsync("context/selected/original", [1]); await other.WriteAsync("context/selected/original", [2]);
        await using var agent = new Agent(new() { Id = "first", Model = new NeverModel(), Storage = other, Plugins = [manager] });
        await manager.DeleteSessionAsync();
        Assert.Empty(raw.Keys("context/selected/")); Assert.Equal(new byte[] { 2 }, await other.ReadAsync("context/selected/original"));
        Assert.False(raw.Disposed); Assert.False(other.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawFallbackFailureIsBestEffortEvenIfTheBorrowedLoggerThrows(bool throwingLogger)
    {
        var previous = SdkLogging.Logger; var logger = new Logger(throwingLogger); SdkLogging.ConfigureLogging(logger);
        try
        {
            var raw = new Store(); var expected = new IOException("raw context unavailable");
            await raw.WriteAsync("selected/record", [1]); await raw.WriteAsync("context/selected/original", [2]);
            raw.OnList = (prefix, _) => prefix.StartsWith("context/", StringComparison.Ordinal) ? throw expected : new(raw.Keys(prefix));
            await new SnapshotSessionManager(raw, "selected").DeleteSessionAsync();
            Assert.Same(expected, logger.Error); Assert.False(logger.Disposed); Assert.False(raw.Disposed);
            Assert.Empty(raw.Keys("selected/")); Assert.Single(raw.Keys("context/selected/"));
        }
        finally { SdkLogging.ConfigureLogging(previous); }
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("noncanonical")]
    [InlineData("null")]
    public async Task LateInvalidFallbackListingNeverDeletesAnyContextKey(string kind)
    {
        var raw = new Store(); await raw.WriteAsync("selected/record", [1]);
        raw.OnList = (prefix, _) => prefix.StartsWith("context/", StringComparison.Ordinal)
            ? new(new[] { prefix + "valid", kind switch { "outside" => "context/other/keep", "noncanonical" => prefix + "a//b", _ => null! } })
            : new(raw.Keys(prefix));
        await new SnapshotSessionManager(raw, "selected").DeleteSessionAsync();
        Assert.Equal(new[] { "selected/record" }, raw.Deleted);
    }

    [Fact]
    public async Task HeldFallbackListingJoinsBeforeCancellationReleasesManagerAdmission()
    {
        using var caller = new CancellationTokenSource(); var entered = Signal(); var release = Signal(); var raw = new Store();
        raw.OnList = async (prefix, token) =>
        {
            if (!prefix.StartsWith("context/", StringComparison.Ordinal)) return raw.Keys(prefix);
            Assert.Equal(caller.Token, token); entered.TrySetResult(true); await release.Task; return new[] { prefix + "original" };
        };
        var manager = new SnapshotSessionManager(raw, "selected"); var deletion = manager.DeleteSessionAsync(caller.Token).AsTask();
        try
        {
            await Within(entered.Task); caller.Cancel(); Assert.False(deletion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => Create(manager));
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deletion);
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Empty(raw.Deleted); raw.OnList = null;
        await using var next = Create(manager);
    }

    [Fact]
    public async Task CancellationRacingTheLastAcceptedStashDeleteDoesNotPretendItWasUncommitted()
    {
        using var caller = new CancellationTokenSource(); var entered = Signal(); var release = Signal(); var raw = new Store();
        await raw.WriteAsync("context/selected/original", [1]);
        raw.OnDelete = async (_, token) => { Assert.Equal(caller.Token, token); entered.TrySetResult(true); await release.Task; };
        var manager = new SnapshotSessionManager(raw, "selected"); var deletion = manager.DeleteSessionAsync(caller.Token).AsTask();
        try
        {
            await Within(entered.Task); caller.Cancel(); Assert.False(deletion.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => Create(manager));
        }
        finally { release.TrySetResult(true); }
        await Within(deletion); Assert.Empty(raw.Keys("context/selected/"));
        await using var next = Create(manager);
    }

    [Fact]
    public async Task IndependentRawFallbackCancellationRetainsItsIdentity()
    {
        var expected = new OperationCanceledException("independent raw backend cancellation"); var raw = new Store();
        raw.OnList = (prefix, _) => prefix.StartsWith("context/", StringComparison.Ordinal) ? throw expected : new(raw.Keys(prefix));
        Assert.Same(expected, await Assert.ThrowsAsync<OperationCanceledException>(() => new SnapshotSessionManager(raw, "selected").DeleteSessionAsync().AsTask()));
    }

    private static Agent Create(SnapshotSessionManager manager) => new(new() { Id = "first", Model = new NeverModel(), Plugins = [manager] });
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Within(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class NeverModel : IModel { public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No models or host processes in fallback tests."); }
    private sealed class ReadingPlugin : IPlugin
    { public int Initializations { get; private set; } public string Name => "test:no-fallback-initialization"; public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Initializations++; return default; } public IReadOnlyList<ITool> GetTools() => []; }
    private sealed class Store : IStorage, IStorageCharacteristics, IDisposable
    {
        private readonly Dictionary<string, byte[]> _data = new(StringComparer.Ordinal);
        public bool IsNamespaced => true; public bool IsEphemeral => false; public bool Disposed { get; private set; }
        public int Reads { get; private set; }
        public List<string> Deleted { get; } = []; public Func<string, CancellationToken, ValueTask<IReadOnlyList<string>>>? OnList { get; set; }
        public Func<string, CancellationToken, ValueTask>? OnDelete { get; set; }
        public string[] Keys(string prefix) => _data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { _data[key] = (byte[])data.Clone(); return default; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { Reads++; return new(_data.TryGetValue(key, out var bytes) ? (byte[])bytes.Clone() : null); }
        public async ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Deleted.Add(key); if (OnDelete is { } callback) await callback(key, cancellationToken); _data.Remove(key); }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => OnList is { } callback ? callback(prefix, cancellationToken) : new(Keys(prefix));
        public void Dispose() => Disposed = true;
    }
    private sealed class Logger(bool throwing) : ILogger, IDisposable
    {
        public Exception? Error { get; private set; }
        public bool Disposed { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { Error = exception; if (throwing) throw new IOException("logger cause"); }
        public void Dispose() => Disposed = true;
    }
}
