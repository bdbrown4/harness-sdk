using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Strands.Agents.A2A;

namespace Strands.Agents.Tests.A2A;

public sealed class A2AServerTaskStoreTests
{
    [Fact]
    public async Task ANewServerRetrievesThePersistedCompletedTaskWithoutCreatingAnotherAgent()
    {
        var store = new InMemoryA2ATaskStore(); var factories = 0; JsonElement completed;
        await using (var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { factories++; return new(new Agent(new() { Model = new A2AServerTests.EchoModel() })); } }))
        await using (var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store }))
        using (var http = new HttpClient(new A2AServerTests.Handler(server)))
            completed = await new A2AClient(http, new(new Uri("http://local/"))).CallAsync("message/send", A2AServerTests.Send("persisted"));
        await using var reopenedExecutor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not run") });
        await using var reopenedServer = new A2AServer(reopenedExecutor, A2AServerTests.Options() with { TaskStore = store });
        using var reopenedHttp = new HttpClient(new A2AServerTests.Handler(reopenedServer));

        var loaded = await new A2AClient(reopenedHttp, new(new Uri("http://local/"))).GetTaskAsync(completed.GetProperty("id").GetString()!);

        Assert.Equal(completed.GetRawText(), loaded.GetRawText()); Assert.Equal(1, factories);
    }
    [Fact]
    public async Task ServerDisposalCancelsAndDrainsAStoreReadAlreadyAcceptedByTheHandler()
    {
        var store = new BlockingReadStore(); using var caller = new CancellationTokenSource();
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not run") });
        var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        var read = new A2AClient(http, new(new Uri("http://local/"))).GetTaskAsync("stored", cancellationToken: caller.Token);
        try
        {
            await A2AWireTests.WithDeadline(store.Started.Task);

            await server.DisposeAsync();

            Assert.True(store.Closed); Assert.True(store.ObservedCancellation);
        }
        finally
        {
            caller.Cancel(); try { await read; } catch (Exception) { }
            await server.DisposeAsync();
        }
    }
    [Fact]
    public async Task CompletedStatusIsNeitherVisibleNorStreamedUntilItsDurableSaveCompletes()
    {
        var store = new TerminalGateStore(); var seen = new ConcurrentQueue<string>();
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel() })) });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var streaming = CollectAsync();
        try
        {
            await A2AWireTests.WithDeadline(store.TerminalSaveStarted.Task);

            Assert.Equal("working", (await client.GetTaskAsync(store.TaskId!)).GetProperty("status").GetProperty("state").GetString());
            Assert.Equal("working", (await store.Inner.LoadAsync(store.TaskId!))!.Value.GetProperty("status").GetProperty("state").GetString());
            Assert.DoesNotContain("completed", seen);

            store.Release.TrySetResult(true); await A2AWireTests.WithDeadline(streaming);
            Assert.Contains("completed", seen);
            Assert.Equal("completed", (await store.Inner.LoadAsync(store.TaskId!))!.Value.GetProperty("status").GetProperty("state").GetString());
        }
        finally { store.Release.TrySetResult(true); await streaming; }
        async Task<bool> CollectAsync()
        {
            await foreach (var update in client.StreamCallAsync("message/stream", A2AServerTests.Send("persist first")))
                if (update.TryGetProperty("status", out var status)) seen.Enqueue(status.GetProperty("state").GetString()!);
            return true;
        }
    }
    [Fact]
    public async Task FailedTerminalSaveDoesNotAdvertiseCompletionOrReplayTheExecutedAgent()
    {
        var failure = new IOException("terminal persistence"); var store = new TerminalGateStore { Failure = failure }; var calls = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { calls++; return new(new Agent(new() { Model = new A2AServerTests.EchoModel() })); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));

        var task = await client.CallAsync("message/send", A2AServerTests.Send("once"));

        Assert.Equal("failed", task.GetProperty("status").GetProperty("state").GetString()); Assert.Equal(1, calls);
        Assert.Equal("failed", (await store.Inner.LoadAsync(task.GetProperty("id").GetString()!))!.Value.GetProperty("status").GetProperty("state").GetString());
        Assert.DoesNotContain("completed", store.SavedStates);
    }
    [Fact]
    public async Task RestoredTasksRetainExtensionsArtifactsAndSelectedHistoryWithoutStartingAnExecution()
    {
        var store = new InMemoryA2ATaskStore();
        var raw = JsonSerializer.Deserialize<JsonElement>("{\"kind\":\"task\",\"id\":\"stored\",\"contextId\":\"context\",\"status\":{\"state\":\"completed\"},\"metadata\":{\"answer\":42},\"artifacts\":[{\"artifactId\":\"a\",\"name\":\"file\",\"metadata\":{\"custom\":true},\"parts\":[{\"kind\":\"text\",\"text\":\"data\"}]}],\"history\":[{\"kind\":\"message\",\"role\":\"user\",\"messageId\":\"one\",\"parts\":[{\"kind\":\"text\",\"text\":\"first\"}]},{\"kind\":\"message\",\"role\":\"agent\",\"messageId\":\"two\",\"parts\":[{\"kind\":\"text\",\"text\":\"second\"}]}]}");
        await store.SaveAsync(raw);
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not run") });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));

        var task = await client.GetTaskAsync("stored", historyLength: 1);

        Assert.Equal(42, task.GetProperty("metadata").GetProperty("answer").GetInt32());
        Assert.Equal("file", task.GetProperty("artifacts")[0].GetProperty("name").GetString()); Assert.True(task.GetProperty("artifacts")[0].GetProperty("metadata").GetProperty("custom").GetBoolean());
        Assert.Equal("two", Assert.Single(task.GetProperty("history").EnumerateArray()).GetProperty("messageId").GetString());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidBackendRecordsAndBackendArgumentFailuresAreSanitizedInternalErrors(bool throws)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not run") });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = new InvalidReadStore(throws) });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => client.GetTaskAsync("stored"));

        Assert.Equal(-32603, error.Code); Assert.DoesNotContain("backend-secret", error.Message);
    }
    [Fact]
    public async Task StoreReadCallbacksCannotDisposeTheirServerOrRecursivelyReadTheSameStore()
    {
        var store = new CallbackReadStore();
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not run") });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        store.Callback = async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => { await server.DisposeAsync(); });
            var recursive = await Assert.ThrowsAsync<A2ARpcException>(() => client.GetTaskAsync("nested")); Assert.Equal(-32603, recursive.Code);
        };

        var result = await A2AWireTests.WithDeadline(client.GetTaskAsync("outer"));

        Assert.Equal("outer", result.GetProperty("id").GetString()); Assert.Equal(1, store.Reads);
    }
    [Fact]
    public async Task CallerCancellationOfAStoreReadRetainsItsExactToken()
    {
        var store = new BlockingReadStore(); using var caller = new CancellationTokenSource();
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => throw new InvalidOperationException("must not run") });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var read = client.GetTaskAsync("stored", cancellationToken: caller.Token); await A2AWireTests.WithDeadline(store.Started.Task);

        caller.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => A2AWireTests.WithDeadline(read));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.True(store.Closed);
    }
    [Fact]
    public async Task SuppliedTaskStoreIsBorrowedAndRemainsUsableAfterServerDisposal()
    {
        var store = new BorrowedStore();
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = new A2AServerTests.EchoModel() })) });
        var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        try
        {
            var task = await client.CallAsync("message/send", A2AServerTests.Send("retained"));

            await server.DisposeAsync();

            Assert.False(store.Disposed); Assert.NotNull(await store.LoadAsync(task.GetProperty("id").GetString()!));
        }
        finally { await server.DisposeAsync(); await store.DisposeAsync(); }
    }
    [Fact]
    public async Task PersistedTaskJsonAloneCannotRestartAnExecutionOrAdoptAnUnrelatedNativeContext()
    {
        var store = new InMemoryA2ATaskStore();
        await store.SaveAsync(JsonSerializer.Deserialize<JsonElement>("{\"kind\":\"task\",\"id\":\"stored\",\"contextId\":\"context\",\"status\":{\"state\":\"input-required\"}}"));
        var model = new A2ARecoveryTests.CountingModel();
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(new Agent(new() { Model = model })) });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = store });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));

        await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync("message/send", A2AServerTests.Send("restart", "stored", "context")));

        Assert.Equal(0, model.Calls); Assert.Equal("input-required", (await client.GetTaskAsync("stored")).GetProperty("status").GetProperty("state").GetString());
    }
    private sealed class BorrowedStore : IA2ATaskStore, IAsyncDisposable
    {
        private readonly InMemoryA2ATaskStore _inner = new();
        internal bool Disposed { get; private set; }
        public ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default) => _inner.SaveAsync(task, cancellationToken);
        public ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default) => _inner.LoadAsync(taskId, cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
    private sealed class CallbackReadStore : IA2ATaskStore
    {
        internal Func<Task>? Callback { get; set; }
        internal int Reads { get; private set; }
        public ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default) => default;
        public async ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default)
        {
            Reads++; if (Callback is { } callback) await callback(); return A2ATaskStoreTests.TaskJson(taskId, "stored");
        }
    }
    private sealed class InvalidReadStore(bool throws) : IA2ATaskStore
    {
        public ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default) => default;
        public ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default)
        {
            if (throws) throw new ArgumentException("backend-secret");
            return new(JsonSerializer.Deserialize<JsonElement>("{\"id\":\"stored\",\"backend-secret\":true}"));
        }
    }
    private sealed class TerminalGateStore : IA2ATaskStore
    {
        internal InMemoryA2ATaskStore Inner { get; } = new();
        internal TaskCompletionSource<bool> TerminalSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> SavedStates { get; } = [];
        internal Exception? Failure { get; init; }
        internal string? TaskId { get; private set; }
        public async ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default)
        {
            var state = task.GetProperty("status").GetProperty("state").GetString()!; TaskId = task.GetProperty("id").GetString();
            if (state == "completed") { TerminalSaveStarted.TrySetResult(true); if (Failure is { } failure) throw failure; await Release.Task; }
            await Inner.SaveAsync(task, cancellationToken); SavedStates.Add(state);
        }
        public ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default) => Inner.LoadAsync(taskId, cancellationToken);
    }
    private sealed class BlockingReadStore : IA2ATaskStore
    {
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed { get; private set; }
        internal bool ObservedCancellation { get; private set; }
        public ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default) => default;
        public async ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return null; }
            finally { ObservedCancellation = cancellationToken.IsCancellationRequested; Closed = true; }
        }
    }
}
