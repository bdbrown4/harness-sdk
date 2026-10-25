using System.Text;
using System.Text.Json;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.SessionRepositories;

public sealed class SessionRepositorySafetyTests
{
    [Fact]
    public async Task BorrowedStorageCallbacksCannotWaitOnTheirOwnRepositoryGate()
    {
        var storage = new ProbeStorage();
        var repository = new StorageSessionRepository(storage);
        storage.BeforeRead = async () =>
        {
            using var timeout = new CancellationTokenSource();
            var nested = repository.ReadSessionAsync("nested", timeout.Token).AsTask();
            if (await Task.WhenAny(nested, Task.Delay(1000)) != nested)
            {
                timeout.Cancel();
                try { await nested; } catch (OperationCanceledException) { }
                throw new TimeoutException("The storage callback waited on its own gate.");
            }
            await nested;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await repository.ReadSessionAsync("session"));
        storage.BeforeRead = null;
        Assert.Null(await repository.ReadSessionAsync("session"));
    }

    [Fact]
    public async Task DuplicateJsonIsRejectedBeforeAnAcceptedRecordCanBeOverwritten()
    {
        var storage = new ProbeStorage();
        var repository = new StorageSessionRepository(storage);
        await repository.CreateAgentAsync("session", new("agent", SessionRepositoryTests.Object("old"), SessionRepositoryTests.Object("manager")));
        var writes = storage.Writes;
        using var duplicate = JsonDocument.Parse("{\"value\":1,\"value\":2}");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.CreateAgentAsync("session", new("agent", duplicate.RootElement, SessionRepositoryTests.Object("manager"))));
        Assert.Equal(writes, storage.Writes);
        Assert.Equal("old", (await repository.ReadAgentAsync("session", "agent"))!.State.GetProperty("value").GetString());
    }

    internal sealed class ProbeStorage : IStorage, IAsyncDisposable
    {
        internal readonly Dictionary<string, byte[]> Data = new(StringComparer.Ordinal);
        internal Func<ValueTask>? BeforeRead { get; set; }
        internal Func<CancellationToken, ValueTask>? BeforeWrite { get; set; }
        internal int Writes;
        internal int Reads;
        internal bool Disposed;
        internal bool IgnoreWriteCancellation { get; set; }
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        {
            if (BeforeWrite is { } before) await before(cancellationToken);
            if (!IgnoreWriteCancellation) cancellationToken.ThrowIfCancellationRequested();
            Data[key] = (byte[])data.Clone(); Writes++;
        }
        public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (BeforeRead is { } before) await before();
            cancellationToken.ThrowIfCancellationRequested();
            return Data.TryGetValue(key, out var value) ? (byte[])value.Clone() : null;
        }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Data.Remove(key); return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return new(Data.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray()); }
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
}
