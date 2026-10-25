using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.A2A;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests.A2A;

public sealed class A2ARecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshServerAndExecutorRestoreTheMatchingNativeSessionWithoutRepeatingAcceptedEffects(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "strands-a2a-recovery-" + Guid.NewGuid().ToString("N"));
        var effects = 0; var approvals = 0; JsonElement paused;
        try
        {
            await using (var executor = new A2AExecutor(new()
            {
                AgentFactory = (id, _) => new(CreateAgent(id, new AbortInterruptTests.PausingModel(), new FileSessionStore(Path.Combine(directory, "sessions")), () => effects++, () => approvals++))
            }))
            await using (var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = new StorageA2ATaskStore(new LocalFileStorage(Path.Combine(directory, "tasks"))) }))
            using (var http = new HttpClient(new A2AServerTests.Handler(server)))
                paused = await new A2AClient(http, new(new Uri("http://local/"))).CallAsync("message/send", A2AServerTests.Send("run"));
            Assert.Equal(1, effects); Assert.Equal(0, approvals);
            await using var restoredExecutor = new A2AExecutor(new()
            {
                AgentFactory = (id, _) => new(CreateAgent(id, new A2AServerTests.EchoModel(), new FileSessionStore(Path.Combine(directory, "sessions")), () => effects++, () => approvals++))
            });
            await using var restoredServer = new A2AServer(restoredExecutor, A2AServerTests.Options() with { TaskStore = new StorageA2ATaskStore(new LocalFileStorage(Path.Combine(directory, "tasks"))) });
            using var restoredHttp = new HttpClient(new A2AServerTests.Handler(restoredServer));

            var client = new A2AClient(restoredHttp, new(new Uri("http://local/")));
            var completed = cancel ? await client.CancelTaskAsync(paused.GetProperty("id").GetString()!) : await client.CallAsync("message/send", Answer(paused));

            Assert.Equal(cancel ? "canceled" : "completed", completed.GetProperty("status").GetProperty("state").GetString());
            Assert.Equal(paused.GetProperty("id").GetString(), completed.GetProperty("id").GetString());
            Assert.Equal(paused.GetProperty("contextId").GetString(), completed.GetProperty("contextId").GetString());
            Assert.Equal(1, effects); Assert.Equal(cancel ? 0 : 1, approvals);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData("interruptId")]
    [InlineData("name")]
    [InlineData("reason")]
    [InlineData("missing")]
    public async Task StoredQuestionsMustMatchTheNativeLedgerBeforeWorkingOrAnyResumedEffect(string mutation)
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore(); var effects = 0; var approvals = 0;
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => effects++, () => approvals++);
        var altered = JsonNode.Parse(paused.GetRawText())!; var questions = altered["status"]!["message"]!["parts"]![1]!["data"]!["interrupts"]!.AsArray();
        if (mutation == "missing") questions.Clear(); else questions[0]![mutation] = "unrelated";
        await tasks.SaveAsync(JsonSerializer.SerializeToElement(altered));
        var model = new CountingModel();

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => RunPhaseAsync(tasks, sessions, Answer(paused), model, () => effects++, () => approvals++));

        Assert.Equal(-32004, error.Code); Assert.Equal(0, model.Calls); Assert.Equal(1, effects); Assert.Equal(0, approvals);
        Assert.Empty(sessions.Latest!.InterruptState!.Responses);
        Assert.Equal("input-required", (await tasks.LoadAsync(paused.GetProperty("id").GetString()!))!.Value.GetProperty("status").GetProperty("state").GetString());
    }
    [Fact]
    public async Task AConflictingContextFailsBeforeFactoryInitializationAndDisposalHandlesTheLoadedTask()
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore();
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => { }, () => { });
        var altered = JsonNode.Parse(Answer(paused).GetRawText())!; altered["message"]!["contextId"] = "unrelated"; var factories = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { factories++; throw new InvalidOperationException("must not initialize"); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = tasks });
        using var http = new HttpClient(new A2AServerTests.Handler(server));

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => new A2AClient(http, new(new Uri("http://local/"))).CallAsync("message/send", JsonSerializer.SerializeToElement(altered)));

        Assert.Equal(-32602, error.Code); Assert.Equal(0, factories);
    }
    [Fact]
    public async Task EachPartialAnswerCanSurviveASeparateServerRestartWithoutRepeatingAcceptedSiblings()
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore(); var effects = 0; var approvals = 0;
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new TwoQuestionModel(), () => effects++, () => approvals++);
        Assert.Equal(2, paused.GetProperty("status").GetProperty("message").GetProperty("parts")[1].GetProperty("data").GetProperty("interrupts").GetArrayLength());

        var remaining = await RunPhaseAsync(tasks, sessions, Answer(paused), new A2AServerTests.EchoModel(), () => effects++, () => approvals++);
        Assert.Equal("input-required", remaining.GetProperty("status").GetProperty("state").GetString()); Assert.Equal(1, approvals);
        var completed = await RunPhaseAsync(tasks, sessions, Answer(remaining), new A2AServerTests.EchoModel(), () => effects++, () => approvals++);

        Assert.Equal("completed", completed.GetProperty("status").GetProperty("state").GetString()); Assert.Equal(1, effects); Assert.Equal(2, approvals);
    }
    [Fact]
    public async Task CancellationWhileRestoringTheNativeSessionRetainsTheCallerTokenAndTheOriginalPause()
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore(); var effects = 0; var approvals = 0;
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => effects++, () => approvals++);
        var blocked = new BlockingLoadStore(sessions);
        var model = new CountingModel(); var sink = new A2AExecutorTests.Sink();
        await using var executor = new A2AExecutor(new() { AgentFactory = (id, _) => new(CreateAgent(id, model, blocked, () => effects++, () => approvals++)) });
        using var cancellation = new CancellationTokenSource();
        var context = Context(paused, Answer(paused));
        var execution = executor.ExecuteAsync(context, sink, cancellation.Token);
        await WithDeadline(blocked.Entered.Task); cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(sink.Events); Assert.Equal(0, model.Calls); Assert.Equal(1, effects); Assert.Equal(0, approvals);
        Assert.Empty(sessions.Latest!.InterruptState!.Responses);
        var completed = await RunPhaseAsync(tasks, sessions, Answer(paused), new A2AServerTests.EchoModel(), () => effects++, () => approvals++);
        Assert.Equal("completed", completed.GetProperty("status").GetProperty("state").GetString()); Assert.Equal(1, effects); Assert.Equal(1, approvals);
    }
    [Fact]
    public async Task ARestoredTaskAdmitsOneConcurrentResumeAndOneNativeFactory()
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore(); var effects = 0; var approvals = 0;
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => effects++, () => approvals++);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var factories = 0;
        await using var executor = new A2AExecutor(new()
        {
            AgentFactory = async (id, token) =>
            {
                Interlocked.Increment(ref factories); entered.TrySetResult(true); await release.Task; token.ThrowIfCancellationRequested();
                return CreateAgent(id, new A2AServerTests.EchoModel(), sessions, () => effects++, () => approvals++);
            }
        });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = tasks });
        using var http = new HttpClient(new A2AServerTests.Handler(server)); var client = new A2AClient(http, new(new Uri("http://local/")));
        var first = client.CallAsync("message/send", Answer(paused));
        try
        {
            await WithDeadline(entered.Task);
            var error = await Assert.ThrowsAsync<A2ARpcException>(() => client.CallAsync("message/send", Answer(paused)));
            Assert.Equal(-32602, error.Code); Assert.Equal(1, factories); Assert.Equal(0, approvals);
        }
        finally { release.TrySetResult(true); }
        var completed = await first;
        Assert.Equal("completed", completed.GetProperty("status").GetProperty("state").GetString()); Assert.Equal(1, effects); Assert.Equal(1, approvals); Assert.Equal(1, factories);
    }
    [Theory]
    [InlineData("submitted")]
    [InlineData("working")]
    [InlineData("auth-required")]
    [InlineData("completed")]
    public async Task TaskPersistenceDoesNotInventACrashRecoveryPolicyForOtherStates(string state)
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore();
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => { }, () => { });
        var altered = JsonNode.Parse(paused.GetRawText())!; altered["status"]!["state"] = state;
        await tasks.SaveAsync(JsonSerializer.SerializeToElement(altered)); var factories = 0;
        await using var executor = new A2AExecutor(new() { AgentFactory = (_, _) => { factories++; throw new InvalidOperationException("No supported native recovery boundary."); } });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = tasks });
        using var http = new HttpClient(new A2AServerTests.Handler(server));

        var error = await Assert.ThrowsAsync<A2ARpcException>(() => new A2AClient(http, new(new Uri("http://local/"))).CallAsync("message/send", Answer(paused)));

        Assert.Equal(-32004, error.Code); Assert.Equal(0, factories); Assert.Empty(sessions.Latest!.InterruptState!.Responses);
    }
    [Fact]
    public async Task TrustedCurrentTaskIsDetachedAndRejectsForeignIdentifiers()
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore();
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => { }, () => { });
        A2ARequestContext context;
        using (var document = JsonDocument.Parse(paused.GetRawText())) context = Context(document.RootElement, Answer(paused));
        Assert.Equal(paused.GetRawText(), context.CurrentTask!.Value.GetRawText());
        foreach (var property in new[] { "id", "contextId" })
        {
            var altered = JsonNode.Parse(paused.GetRawText())!; altered[property] = "foreign";
            Assert.Throws<ArgumentException>(() => new A2ARequestContext(context.TaskId, context.ContextId, context.Message) { CurrentTask = JsonSerializer.SerializeToElement(altered) });
        }
    }
    [Fact]
    public async Task FailedNativeAbortPersistenceKeepsTheStoredPauseRecoverableWithoutReplayingEffects()
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore(); var effects = 0; var approvals = 0;
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => effects++, () => approvals++);
        var failure = new IOException("native pause save");
        await using (var executor = new A2AExecutor(new() { AgentFactory = (id, _) => new(CreateAgent(id, new CountingModel(), new FailingSaveStore(sessions, failure), () => effects++, () => approvals++)) }))
        await using (var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = tasks }))
        using (var http = new HttpClient(new A2AServerTests.Handler(server)))
        {
            var error = await Assert.ThrowsAsync<A2ARpcException>(() => new A2AClient(http, new(new Uri("http://local/"))).CancelTaskAsync(paused.GetProperty("id").GetString()!));
            Assert.Equal(-32603, error.Code);
        }
        Assert.NotNull(sessions.Latest!.InterruptState); Assert.Empty(sessions.Latest.InterruptState!.Responses);
        Assert.Equal("input-required", (await tasks.LoadAsync(paused.GetProperty("id").GetString()!))!.Value.GetProperty("status").GetProperty("state").GetString());
        await using var recoveredExecutor = new A2AExecutor(new() { AgentFactory = (id, _) => new(CreateAgent(id, new CountingModel(), sessions, () => effects++, () => approvals++)) });
        await using var recoveredServer = new A2AServer(recoveredExecutor, A2AServerTests.Options() with { TaskStore = tasks });
        using var recoveredHttp = new HttpClient(new A2AServerTests.Handler(recoveredServer));
        var canceled = await new A2AClient(recoveredHttp, new(new Uri("http://local/"))).CancelTaskAsync(paused.GetProperty("id").GetString()!);
        Assert.Equal("canceled", canceled.GetProperty("status").GetProperty("state").GetString()); Assert.Null(sessions.Latest.InterruptState); Assert.Equal(1, effects); Assert.Equal(0, approvals);
    }
    [Fact]
    public async Task AFailedWorkingTaskSaveDoesNotAcceptAnswersOrExecuteTheRestoredTools()
    {
        var tasks = new InMemoryA2ATaskStore(); var sessions = new SnapshotStore(); var effects = 0; var approvals = 0;
        var paused = await RunPhaseAsync(tasks, sessions, A2AServerTests.Send("run"), new AbortInterruptTests.PausingModel(), () => effects++, () => approvals++);
        var model = new CountingModel();
        var error = await Assert.ThrowsAsync<A2ARpcException>(() => RunPhaseAsync(new FailingTaskSaveStore(tasks), sessions, Answer(paused), model, () => effects++, () => approvals++));
        Assert.Equal(-32603, error.Code); Assert.Equal(0, model.Calls); Assert.Empty(sessions.Latest!.InterruptState!.Responses); Assert.Equal(0, approvals);
        var completed = await RunPhaseAsync(tasks, sessions, Answer(paused), new A2AServerTests.EchoModel(), () => effects++, () => approvals++);
        Assert.Equal("completed", completed.GetProperty("status").GetProperty("state").GetString()); Assert.Equal(1, effects); Assert.Equal(1, approvals);
    }
    [Fact]
    public async Task AnExplicitJsonNullReasonMatchesItsPublishedNullWhenReattachingATrustedPause()
    {
        var tasks = new InMemoryA2ATaskStore(); var approvals = 0; JsonElement paused;
        await using var agent = new Agent(new()
        {
            Model = new NullReasonModel(),
            Tools = [new FunctionTool(new("approve", "approve", AbortInterruptTests.Json("{\"type\":\"object\"}")), (_, context, _) =>
            { context.Interrupt("approval", JsonSerializer.SerializeToElement<object?>(null)); approvals++; return new ValueTask<string>("approved"); })]
        });
        await using (var executor = new A2AExecutor(new() { AgentFactory = (_, _) => new(agent), OwnsAgents = false }))
        await using (var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = tasks }))
        using (var http = new HttpClient(new A2AServerTests.Handler(server)))
            paused = await new A2AClient(http, new(new Uri("http://local/"))).CallAsync("message/send", A2AServerTests.Send("run"));
        Assert.Equal(JsonValueKind.Null, agent.InterruptState!.Interrupts[0].Reason!.Value.ValueKind);
        await using var restoredExecutor = new A2AExecutor(new() { AgentFactory = (_, _) => new(agent), OwnsAgents = false });
        await using var restoredServer = new A2AServer(restoredExecutor, A2AServerTests.Options() with { TaskStore = tasks });
        using var restoredHttp = new HttpClient(new A2AServerTests.Handler(restoredServer));

        var completed = await new A2AClient(restoredHttp, new(new Uri("http://local/"))).CallAsync("message/send", Answer(paused));

        Assert.Equal("completed", completed.GetProperty("status").GetProperty("state").GetString()); Assert.Equal(1, approvals);
    }
    private static A2ARequestContext Context(JsonElement task, JsonElement parameters) => new(task.GetProperty("id").GetString()!, task.GetProperty("contextId").GetString()!, parameters.GetProperty("message")) { CurrentTask = task };
    private static async Task WithDeadline(Task task)
    { Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)))); await task; }
    private sealed class BlockingLoadStore(ISessionStore inner) : ISessionStore
    {
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); return await inner.LoadAsync(sessionId, cancellationToken); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => inner.SaveAsync(sessionId, snapshot, cancellationToken);
    }
    private sealed class FailingSaveStore(ISessionStore inner, Exception failure) : ISessionStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) => inner.LoadAsync(sessionId, cancellationToken);
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default) => throw failure;
    }
    private sealed class FailingTaskSaveStore(IA2ATaskStore inner) : IA2ATaskStore
    {
        public ValueTask<JsonElement?> LoadAsync(string taskId, CancellationToken cancellationToken = default) => inner.LoadAsync(taskId, cancellationToken);
        public ValueTask SaveAsync(JsonElement task, CancellationToken cancellationToken = default) => throw new IOException("working task save");
    }
    internal static async Task<JsonElement> RunPhaseAsync(IA2ATaskStore tasks, ISessionStore sessions, JsonElement parameters, IModel model, Action effect, Action approved)
    {
        await using var executor = new A2AExecutor(new() { AgentFactory = (id, _) => new(CreateAgent(id, model, sessions, effect, approved)) });
        await using var server = new A2AServer(executor, A2AServerTests.Options() with { TaskStore = tasks });
        using var http = new HttpClient(new A2AServerTests.Handler(server));
        return await new A2AClient(http, new(new Uri("http://local/"))).CallAsync("message/send", parameters);
    }
    internal sealed class SnapshotStore : ISessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SessionSnapshot> _values = new(StringComparer.Ordinal);
        internal SessionSnapshot? Latest { get; private set; }
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { lock (_gate) return new(_values.TryGetValue(sessionId, out var snapshot) ? JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot)) : null); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { lock (_gate) _values[sessionId] = Latest = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot))!; return default; }
    }
    internal sealed class CountingModel : IModel
    {
        internal int Calls { get; private set; }
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { Calls++; return new A2AServerTests.EchoModel().StreamAsync(request, cancellationToken); }
    }
    private sealed class TwoQuestionModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent();
            var calls = new[] { new ToolUseBlock("done", "done", AbortInterruptTests.Json("{}")), new ToolUseBlock("first", "approve", AbortInterruptTests.Json("{}")), new ToolUseBlock("second", "approve", AbortInterruptTests.Json("{}")) };
            for (var index = 0; index < calls.Length; index++) { yield return new ModelContentBlockStartEvent(index, calls[index]); yield return new ModelContentBlockStopEvent(index); }
            yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }
    private sealed class NullReasonModel : IModel
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request.Messages.Any(message => message.Content.OfType<ToolResultBlock>().Any()))
            {
                await foreach (var update in new A2AServerTests.EchoModel().StreamAsync(request, cancellationToken)) yield return update;
                yield break;
            }
            cancellationToken.ThrowIfCancellationRequested(); yield return new ModelMessageStartEvent();
            yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("approval", "approve", AbortInterruptTests.Json("{}")));
            yield return new ModelContentBlockStopEvent(0); yield return new ModelMessageStopEvent(StopReasons.ToolUse);
        }
    }
    internal static Agent CreateAgent(string contextId, IModel model, ISessionStore store, Action effect, Action approved) => new(new()
    {
        Model = model,
        SessionStore = store,
        SessionId = contextId,
        Tools = [new FunctionTool(new("done", "done", AbortInterruptTests.Json("{\"type\":\"object\"}")), (_, _, _) => { effect(); return new ValueTask<string>("saved"); }),
            new FunctionTool(new("approve", "approve", AbortInterruptTests.Json("{\"type\":\"object\"}")), (_, context, _) => { context.Interrupt("approval"); approved(); return new ValueTask<string>("approved"); })]
    });
    internal static JsonElement Answer(JsonElement task)
    {
        var question = task.GetProperty("status").GetProperty("message").GetProperty("parts").EnumerateArray().Single(part => part.GetProperty("kind").GetString() == "data").GetProperty("data").GetProperty("interrupts")[0].GetProperty("interruptId").GetString();
        return JsonSerializer.SerializeToElement(new { message = new { kind = "message", messageId = Guid.NewGuid().ToString("N"), role = "user", taskId = task.GetProperty("id").GetString(), contextId = task.GetProperty("contextId").GetString(), parts = new[] { new { kind = "data", data = new { interruptResponse = new { interruptId = question, response = true } } } } } });
    }
}
