using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Strands.Agents.Memory;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.Memory;

public sealed class TestMemoryStoreSafetyTests
{
    [Fact]
    public async Task ASharedUtf8FileMayStartWithABomWithoutLosingItsRecords()
    {
        var json = Encoding.UTF8.GetBytes("[{\"id\":\"shared\",\"content\":\"shared fact\",\"createdAt\":\"2026-01-01T00:00:00.000Z\"}]");
        var backend = new ControlledStorage { Bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray() };
        var store = Store(backend);
        Assert.Equal("shared fact", Assert.Single(await store.SearchAsync("shared")).Content);
        await store.AddAsync("new fact");
        Assert.Equal(2, (await store.SearchAsync("fact")).Count);
    }

    [Fact]
    public async Task SupplementaryUnicodeLettersAndNumbersRemainWholeTokens()
    {
        var store = new TestMemoryStore(new() { Name = "unicode", Persist = false });
        await store.AddAsync("café 日本 naïve 𐐀𐐁 𝟙𝟚");
        foreach (var query in new[] { "CAFÉ", "日本", "naïve", "𐐀𐐁", "𝟙𝟚" })
            Assert.Single(await store.SearchAsync(query));
    }

    [Fact]
    public async Task AnUnrequestedBackendFailureKeepsItsOriginalStackAndIdentity()
    {
        var primary = new IOException("read failure");
        var backend = new ControlledStorage { OnRead = _ => ThrowFromBackend(primary) };
        var store = Store(backend);
        var error = await Assert.ThrowsAsync<IOException>(async () => await store.SearchAsync("fact"));
        Assert.Same(primary, error);
        Assert.Contains(nameof(ThrowFromBackend), error.StackTrace!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameInstanceSerializesConcurrentAddsWithoutLosingUniqueRecords(bool persistent)
    {
        var root = Root();
        var path = Path.Combine(root, "records.json");
        try
        {
            var store = new TestMemoryStore(new() { Name = "notes", Persist = persistent, Path = path });
            var ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(async value => await store.AddAsync("fact " + value)));
            Assert.Equal(20, ids.Select(result => result.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(20, (await store.SearchAsync("fact", 30)).Count);
            if (persistent)
            {
                using var records = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal(20, records.RootElement.GetArrayLength());
            }
            else Assert.False(File.Exists(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentTrimmedDuplicatesReturnOneStableRecord()
    {
        var store = new TestMemoryStore(new() { Name = "notes", Persist = false });
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i => await store.AddAsync(i % 2 == 0 ? " fact " : "fact")));
        Assert.Single(results.Select(value => value.Id).Distinct(StringComparer.Ordinal));
        Assert.Single(await store.SearchAsync("fact"));
    }

    [Fact]
    public async Task MetadataIsDetachedBeforeWaitingAndTheReservedScoreDoesNotOverwriteStoredData()
    {
        var entered = Gate(); var release = Gate();
        var reads = 0;
        var backend = new ControlledStorage
        {
            OnRead = async token =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    entered.TrySetResult(true);
                    await release.Task;
                }
                token.ThrowIfCancellationRequested();
            },
        };
        var store = Store(backend);
        var first = store.AddAsync("first fact").AsTask();
        Task<TestMemoryAddResult>? second = null;
        try
        {
            await Wait(entered.Task);
            using (var metadata = JsonDocument.Parse("{\"nested\":{\"keep\":[1,true,null]},\"_relevanceScore\":999}"))
                second = store.AddAsync("second fact", metadata.RootElement).AsTask();
        }
        finally { release.TrySetResult(true); }
        await first;
        await second!;
        var result = Assert.Single(await store.SearchAsync("second"));
        Assert.Equal(1, result.Metadata!.Value.GetProperty("_relevanceScore").GetInt32());
        Assert.True(result.Metadata.Value.GetProperty("nested").GetProperty("keep")[1].GetBoolean());
        using var raw = JsonDocument.Parse(backend.Bytes!);
        Assert.Equal(999, raw.RootElement[1].GetProperty("metadata").GetProperty("_relevanceScore").GetInt32());
    }

    [Fact]
    public async Task FailedWritesLeaveThePreviousFileAndDoNotWedgeLaterAdds()
    {
        var backend = new ControlledStorage();
        var store = Store(backend);
        await store.AddAsync("first fact");
        var original = (byte[])backend.Bytes!.Clone();
        var failure = new IOException("write failure");
        backend.OnWrite = _ => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () => await store.AddAsync("failed fact")));
        Assert.Equal(original, backend.Bytes);
        backend.OnWrite = null;
        await store.AddAsync("later fact");
        Assert.Equal(new[] { "first fact", "later fact" }.OrderBy(value => value, StringComparer.Ordinal),
            (await store.SearchAsync("fact")).Select(value => value.Content).OrderBy(value => value, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":1,\"content\":\"fact\",\"createdAt\":\"date\"}]")]
    [InlineData("[{\"id\":\"id\",\"content\":null,\"createdAt\":\"date\"}]")]
    [InlineData("[{\"id\":\"id\",\"content\":\"fact\",\"createdAt\":null}]")]
    [InlineData("[{\"id\":\"id\",\"content\":\"fact\",\"createdAt\":\"date\",\"metadata\":[]}]")]
    public async Task MalformedRecordsNeverGetSilentlyReplaced(string raw)
    {
        var original = Encoding.UTF8.GetBytes(raw);
        var backend = new ControlledStorage { Bytes = original };
        var store = Store(backend);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SearchAsync("fact"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.AddAsync("replacement"));
        Assert.Equal(0, backend.Writes);
        Assert.Equal(original, backend.Bytes);
    }

    [Fact]
    public async Task WholeRecordValidationPreventsWritingAfterAnEarlierValidRow()
    {
        var raw = "[{\"id\":\"ok\",\"content\":\"existing fact\",\"createdAt\":\"date\"},{\"id\":true}]";
        var backend = new ControlledStorage { Bytes = Encoding.UTF8.GetBytes(raw) };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Store(backend).AddAsync("existing fact"));
        Assert.Equal(0, backend.Writes);
        Assert.Equal(raw, Encoding.UTF8.GetString(backend.Bytes!));
    }

    [Fact]
    public async Task TokenlessQueriesDoNotReadEvenAMalformedOrUnavailableBackend()
    {
        var backend = new ControlledStorage { OnRead = _ => throw new IOException("Must not be read.") };
        var store = Store(backend);
        Assert.Empty(await store.SearchAsync(" ...  "));
        Assert.Equal(0, backend.Reads);
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.SearchAsync("", caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
    }

    [Fact]
    public async Task ACancelledWriterWaitingForTheInstanceGateCannotConsumeAnotherWritersAdmission()
    {
        var entered = Gate(); var release = Gate();
        var reads = 0;
        var backend = new ControlledStorage
        {
            OnRead = async _ =>
            {
                if (Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(true); await release.Task; }
            },
        };
        var store = Store(backend);
        var first = store.AddAsync("accepted first fact").AsTask();
        using var caller = new CancellationTokenSource();
        try
        {
            await Wait(entered.Task);
            var second = store.AddAsync("cancelled waiting fact", cancellationToken: caller.Token).AsTask();
            caller.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal(1, backend.Reads);
            Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        await first;
        await store.AddAsync("later fact");
        Assert.Equal(2, backend.Writes);
        Assert.DoesNotContain(await store.SearchAsync("fact"), entry => entry.Content.IndexOf("cancelled", StringComparison.Ordinal) >= 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedCancellationJoinsPhysicalReadsAndWritesAndReportsCommittedWriteUncertainty(bool write)
    {
        using var caller = new CancellationTokenSource();
        var entered = Gate(); var release = Gate();
        var backend = new ControlledStorage { IgnoreCancellation = true };
        Func<CancellationToken, Task> hold = async _ => { entered.TrySetResult(true); await release.Task; };
        if (write) backend.OnWrite = hold; else backend.OnRead = hold;
        var store = Store(backend);
        Task operation = write ? store.AddAsync("accepted fact", cancellationToken: caller.Token).AsTask() : store.SearchAsync("fact", caller.Token).AsTask();
        try
        {
            await Wait(entered.Task);
            caller.Cancel();
            Assert.False(operation.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(caller.Token, error.CancellationToken);
        backend.OnRead = null; backend.OnWrite = null;
        if (write)
        {
            using var committed = JsonDocument.Parse(backend.Bytes!);
            var id = committed.RootElement[0].GetProperty("id").GetString();
            Assert.Equal(id, (await store.AddAsync("accepted fact")).Id);
            Assert.Equal(1, backend.Writes);
        }
        else Assert.Null(backend.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationNormalizesAnIndependentFailureWithoutLosingItsCause(bool tokenless)
    {
        using var caller = new CancellationTokenSource();
        var primary = tokenless ? (Exception)new OperationCanceledException("backend cancellation") : new IOException("backend failed while caller cancelled");
        var backend = new ControlledStorage { OnRead = _ => { caller.Cancel(); throw primary; } };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store(backend).SearchAsync("fact", caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Same(primary, error.InnerException);
        Assert.False(ReferenceEquals(primary, error));
    }

    [Fact]
    public async Task MatchingCallerCancellationAndIndependentCleanupErrorsKeepTheirOriginalIdentity()
    {
        using var caller = new CancellationTokenSource();
        var matching = new OperationCanceledException(caller.Token);
        var backend = new ControlledStorage { OnRead = _ => { caller.Cancel(); throw matching; } };
        Assert.Same(matching, await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store(backend).SearchAsync("fact", caller.Token)));

        using var secondCaller = new CancellationTokenSource();
        var primary = new IOException("primary backend failure");
        var cleanup = new IOException("independent cleanup failure");
        primary.Data["Strands.CleanupErrors"] = new List<Exception> { cleanup };
        backend = new ControlledStorage { OnRead = _ => { secondCaller.Cancel(); throw primary; } };
        var error = await Assert.ThrowsAsync<IOException>(async () => await Store(backend).AddAsync("fact", cancellationToken: secondCaller.Token));
        Assert.Same(primary, error);
        Assert.Same(cleanup, Assert.Single(Assert.IsType<List<Exception>>(error.Data["Strands.CleanupErrors"])));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task ThrowFromBackend(Exception error) => throw error;
    private static TestMemoryStore Store(ControlledStorage backend) => new(new() { Name = "notes" }, backend, "notes.json");
    private static string Root() => Path.Combine(Path.GetTempPath(), "test-memory-" + Guid.NewGuid().ToString("N"));
    private static TaskCompletionSource<bool> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }

    private sealed class ControlledStorage : IStorage
    {
        internal byte[]? Bytes;
        internal int Reads, Writes;
        internal bool IgnoreCancellation;
        internal Func<CancellationToken, Task>? OnRead, OnWrite;
        public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            if (OnRead is { } action) await action(cancellationToken);
            if (!IgnoreCancellation) cancellationToken.ThrowIfCancellationRequested();
            return Bytes is { } bytes ? (byte[])bytes.Clone() : null;
        }
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            if (OnWrite is { } action) await action(cancellationToken);
            if (!IgnoreCancellation) cancellationToken.ThrowIfCancellationRequested();
            Bytes = (byte[])data.Clone();
        }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
