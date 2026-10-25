using System.Text;
using System.Text.Json;
using Strands.Agents.A2A;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ATaskStoreTests
{
    [Theory]
    [InlineData(0, 1, 1, "maxTasks")]
    [InlineData(1, 0, 1, "maxTaskBytes")]
    [InlineData(1, 1, 0, "maxTotalBytes")]
    public void InvalidCapacityConfigurationNamesTheInvalidArgument(int tasks, int taskBytes, long totalBytes, string argument)
        => Assert.Equal(argument, Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryA2ATaskStore(tasks, taskBytes, totalBytes)).ParamName);
    [Fact]
    public async Task SavedTaskAndExtensionsOutliveTheCallersJsonDocument()
    {
        var store = new InMemoryA2ATaskStore();
        using (var document = JsonDocument.Parse("{\"kind\":\"task\",\"id\":\"task\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"},\"extension\":{\"answer\":42}}"))
            await store.SaveAsync(document.RootElement);

        var loaded = await store.LoadAsync("task");

        Assert.NotNull(loaded); Assert.Equal(42, loaded.Value.GetProperty("extension").GetProperty("answer").GetInt32());
        Assert.Null(await store.LoadAsync("missing"));
    }
    [Fact]
    public async Task CapacityFailurePreservesExistingTasksAndAllowsReplacingTheSameRecord()
    {
        var store = new InMemoryA2ATaskStore(maxTasks: 1); await store.SaveAsync(TaskJson("first", "old"));

        await Assert.ThrowsAsync<A2AProtocolException>(async () => { await store.SaveAsync(TaskJson("second", "other")); });

        Assert.Null(await store.LoadAsync("second")); Assert.Equal("old", (await store.LoadAsync("first"))!.Value.GetProperty("detail").GetString());
        await store.SaveAsync(TaskJson("first", "new"));
        Assert.Equal("new", (await store.LoadAsync("first"))!.Value.GetProperty("detail").GetString());
    }
    [Fact]
    public async Task PerTaskBytesAreMeasuredInUtf8AndOversizedReplacementPreservesTheOldRecord()
    {
        var original = TaskJson("first", "old");
        var store = new InMemoryA2ATaskStore(maxTaskBytes: Encoding.UTF8.GetByteCount(original.GetRawText())); await store.SaveAsync(original);

        await Assert.ThrowsAsync<A2AProtocolException>(async () => { await store.SaveAsync(TaskJson("first", new string('x', 100))); });

        Assert.Equal("old", (await store.LoadAsync("first"))!.Value.GetProperty("detail").GetString());
    }
    [Fact]
    public async Task TotalBytesCannotGrowPastTheLimitAndSmallerReplacementReleasesCapacity()
    {
        var first = TaskJson("first", "12345678"); var second = TaskJson("other", "12345678");
        var size = Encoding.UTF8.GetByteCount(first.GetRawText()); var store = new InMemoryA2ATaskStore(maxTotalBytes: size * 2 - 1); await store.SaveAsync(first);

        await Assert.ThrowsAsync<A2AProtocolException>(async () => { await store.SaveAsync(second); });

        Assert.Null(await store.LoadAsync("other"));
        await store.SaveAsync(TaskJson("first", "")); await store.SaveAsync(second);
        Assert.NotNull(await store.LoadAsync("other"));
    }
    [Theory]
    [InlineData("{\"id\":\"first\"}")]
    [InlineData("{\"kind\":\"message\",\"id\":\"first\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"}}")]
    [InlineData("{\"kind\":\"task\",\"id\":\"first\",\"contextId\":\"\",\"status\":{\"state\":\"completed\"}}")]
    [InlineData("{\"kind\":\"task\",\"id\":\"first\",\"contextId\":\"context\",\"status\":{\"state\":\"invalid\"}}")]
    [InlineData("{\"kind\":\"task\",\"id\":\"first\",\"id\":\"first\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"}}")]
    [InlineData("{\"kind\":\"task\",\"id\":\"first\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"},\"history\":{}}")]
    [InlineData("{\"kind\":\"task\",\"id\":\"first\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"},\"artifacts\":[{}]}")]
    public async Task InvalidTaskRecordsCannotOverwriteAValidSnapshot(string json)
    {
        var store = new InMemoryA2ATaskStore(); await store.SaveAsync(TaskJson("first", "valid"));

        await Assert.ThrowsAsync<A2AProtocolException>(async () => { await store.SaveAsync(JsonSerializer.Deserialize<JsonElement>(json)); });

        Assert.Equal("valid", (await store.LoadAsync("first"))!.Value.GetProperty("detail").GetString());
    }
    [Fact]
    public async Task StoredTaskSurvivesRecreatingBothAdaptersAndTheLocalFileBackend()
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-a2a-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new StorageA2ATaskStore(new LocalFileStorage(directory));
            await first.SaveAsync(TaskJson("../task/雪", "persisted"));

            var reopened = new StorageA2ATaskStore(new LocalFileStorage(directory));
            var loaded = await reopened.LoadAsync("../task/雪");

            Assert.NotNull(loaded); Assert.Equal("persisted", loaded.Value.GetProperty("detail").GetString());
            Assert.Null(await reopened.LoadAsync("other"));
            Assert.Single(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ActualUtf8ByteLimitIncludesMultibyteText()
    {
        var task = JsonSerializer.Deserialize<JsonElement>("{\"kind\":\"task\",\"id\":\"first\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"},\"detail\":\"雪\"}");
        var store = new InMemoryA2ATaskStore(maxTaskBytes: task.GetRawText().Length);
        await Assert.ThrowsAsync<A2AProtocolException>(async () => { await store.SaveAsync(task); });
        Assert.Null(await store.LoadAsync("first"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledSaveAndLoadPreserveTheCallerTokenAndExistingData(bool storage)
    {
        IA2ATaskStore store = storage ? new StorageA2ATaskStore(new InMemoryStorage()) : new InMemoryA2ATaskStore();
        await store.SaveAsync(TaskJson("first", "old")); using var caller = new CancellationTokenSource(); caller.Cancel();
        var save = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await store.SaveAsync(TaskJson("first", "new"), caller.Token); });
        var load = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await store.LoadAsync("first", caller.Token); });
        Assert.Equal(caller.Token, save.CancellationToken); Assert.Equal(caller.Token, load.CancellationToken);
        Assert.Equal("old", (await store.LoadAsync("first"))!.Value.GetProperty("detail").GetString());
    }
    [Fact]
    public async Task BackendFailuresRetainOriginalIdentityAndDoNotChangeExistingData()
    {
        var backend = new ControlledStorage(); var store = new StorageA2ATaskStore(backend); await store.SaveAsync(TaskJson("first", "old"));
        var writeFailure = new IOException("write"); backend.WriteFailure = writeFailure;
        Assert.Same(writeFailure, await Assert.ThrowsAsync<IOException>(async () => { await store.SaveAsync(TaskJson("first", "new")); }));
        Assert.Equal("old", (await store.LoadAsync("first"))!.Value.GetProperty("detail").GetString());
        var readFailure = new IOException("read"); backend.ReadFailure = readFailure;
        Assert.Same(readFailure, await Assert.ThrowsAsync<IOException>(async () => { await store.LoadAsync("first"); }));
    }
    [Theory]
    [InlineData("identifier")]
    [InlineData("size")]
    [InlineData("json")]
    [InlineData("utf8")]
    [InlineData("duplicates")]
    public async Task CorruptedBackendRecordsAreRejectedBeforeTheyReachTaskConsumers(string corruption)
    {
        var backend = new ControlledStorage(); var store = new StorageA2ATaskStore(backend, maxTaskBytes: 1024);
        await store.SaveAsync(TaskJson("first", "old"));
        backend.Value = corruption switch
        {
            "identifier" => Encoding.UTF8.GetBytes(TaskJson("other", "wrong").GetRawText()),
            "size" => new byte[1025],
            "json" => Encoding.UTF8.GetBytes("{"),
            "utf8" => [0xff],
            _ => Encoding.UTF8.GetBytes("{\"kind\":\"task\",\"id\":\"first\",\"id\":\"other\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"}}")
        };
        await Assert.ThrowsAsync<A2AProtocolException>(async () => { await store.LoadAsync("first"); });
    }
    [Fact]
    public async Task BackendReadThatIgnoresCancellationCannotReturnASuccessfulRecordAfterCancellation()
    {
        using var caller = new CancellationTokenSource(); var backend = new ControlledStorage(); var store = new StorageA2ATaskStore(backend);
        await store.SaveAsync(TaskJson("first", "old")); backend.ReadAction = caller.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await store.LoadAsync("first", caller.Token); });
        Assert.Equal(caller.Token, error.CancellationToken);
    }
    [Fact]
    public async Task ExplicitNamespacesDoNotCollideAndBackendBuffersRemainIndependent()
    {
        var backend = new InMemoryStorage(); var first = new StorageA2ATaskStore(backend, "one"); var second = new StorageA2ATaskStore(backend, "two");
        await first.SaveAsync(TaskJson("same", "one")); await second.SaveAsync(TaskJson("same", "two"));
        Assert.Equal("one", (await first.LoadAsync("same"))!.Value.GetProperty("detail").GetString());
        Assert.Equal("two", (await second.LoadAsync("same"))!.Value.GetProperty("detail").GetString());
        Assert.Equal(2, (await backend.ListAsync()).Count);
    }
    private sealed class ControlledStorage : IStorage
    {
        internal byte[]? Value { get; set; }
        internal Exception? ReadFailure { get; set; }
        internal Exception? WriteFailure { get; set; }
        internal Action? ReadAction { get; set; }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (WriteFailure is { } failure) throw failure;
            Value = (byte[])data.Clone(); return default;
        }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { if (ReadFailure is { } failure) throw failure; ReadAction?.Invoke(); return new(Value is { } bytes ? (byte[])bytes.Clone() : null); }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Value = null; return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => new(Array.Empty<string>());
    }
    internal static JsonElement TaskJson(string id, string detail) => JsonSerializer.SerializeToElement(new { kind = "task", id, contextId = "context", status = new { state = "completed" }, detail });
}
