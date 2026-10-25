using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.Middleware;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class ReceiptIndexedSafetyTests
{
    private const string Session = "receipt-indexed";
    private const string AgentId = "indexed-agent";
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);


    [Fact]
    public async Task FailedReceiptMetadataUpdateRetriesWithoutReplayingAcceptedRowsOrProvider()
    {
        using var files = new FileFixture();
        await using var repository = files.Open();
        var sentinel = new IOException("receipt metadata update");
        var failed = false;
        var accepted = false;
        repository.BeforeUpdate = (record, _) =>
        {
            if (record.InternalState.GetProperty("nativeSessionVersion").GetInt32() == 3)
            {
                if (!failed) { failed = true; throw sentinel; }
                accepted = true;
            }
            return default;
        };
        var model = new ScriptStatefulModel(Text("discarded", "state-1"), Call("answer", 7, "state-2"));
        await using var agent = Create(model, repository, maxTurns: 1);
        Assert.Same(sentinel, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question")));
        Assert.Single(model.Requests);
        var firstRows = await repository.ListMessagesAsync(Session, AgentId);
        Assert.Equal("question", Assert.Single(firstRows).ToMessage().Text);
        model.BeforeRequest = _ => Assert.True(accepted, "Pending metadata must be accepted before another provider call.");

        Assert.Equal(7, Value(await agent.InvokeAsync(null, new() { MaxTurns = 2 })));

        Assert.Empty(model.Requests[1].Messages);
        Assert.Equal("state-1", model.Requests[1].ModelState!["responseId"].GetString());
        var rows = await repository.ListMessagesAsync(Session, AgentId);
        Assert.Equal(new long[] { 0, 1, 2 }, rows.Select(row => row.MessageId));
        Assert.Equal(1, rows.Count(row => row.ToMessage().Text == "question"));
        var record = (await repository.ReadAgentAsync(Session, AgentId))!;
        Assert.Equal(2, record.InternalState.GetProperty("nativeSessionVersion").GetInt32());
        Assert.Null(Metadata(record).ModelInputReceipt);
        Assert.Equal(0, repository.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldReceiptMetadataWriteDetachesAndPhysicallyJoins(bool cancelCaller)
    {
        using var files = new FileFixture();
        await using var repository = files.Open();
        using var caller = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        SessionAgentRecord? captured = null;
        repository.IgnoreUpdateCancellation = true;
        repository.BeforeUpdate = async (record, _) =>
        {
            if (record.InternalState.GetProperty("nativeSessionVersion").GetInt32() != 3) return;
            captured = record;
            entered.TrySetResult(true);
            await release.Task;
        };
        var model = new ScriptStatefulModel(Text("discarded", "state-1"));
        await using var agent = Create(model, repository, maxTurns: 1);
        Task<AgentResult>? invocation = null;
        try
        {
            invocation = agent.InvokeAsync("question", cancellationToken: caller.Token);
            await Wait(entered.Task);
            agent.State.Set("label", "after-capture");
            agent.ModelState.Set("application", "after-capture");
            if (cancelCaller) caller.Cancel();
            Assert.False(invocation.IsCompleted);
            var snapshot = Metadata(captured!);
            Assert.Equal(2, snapshot.SchemaVersion);
            Assert.Equal(1, snapshot.ModelInputReceipt!.ConsumedMessageCount);
            Assert.Equal("state-1", snapshot.ModelState!["responseId"].GetString());
            Assert.False(snapshot.ModelState.ContainsKey("application"));
            Assert.Equal("configured", captured!.State.GetProperty("label").GetString());
            release.TrySetResult(true);
            // Accepted persistence completes first; requested execution cancellation becomes a result.
            Assert.Equal(cancelCaller ? StopReasons.Cancelled : StopReasons.MaxTurns, (await invocation).StopReason);
            Assert.Equal(cancelCaller, caller.IsCancellationRequested);
            Assert.Single(model.Requests);
            Assert.Equal(1, model.IteratorDisposals);
            var accepted = (await repository.ReadAgentAsync(Session, AgentId))!;
            Assert.Equal(captured.InternalState.GetRawText(), accepted.InternalState.GetRawText());
            Assert.Equal(0, repository.Disposals);
        }
        finally { release.TrySetResult(true); await Drain(invocation); }
    }

    [Theory]
    [InlineData("read-agent")]
    [InlineData("list-messages")]
    public async Task ValidReceiptReadCancellationPreservesConfiguredAgentAndBackend(string stage)
    {
        using var files = new FileFixture();
        await using var repository = files.Open();
        await SeedReceipt(repository);
        var before = await Bytes(repository.Storage);
        repository.Writes.Clear();
        using var caller = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        repository.AfterRead = async (name, _) => { if (name == stage) { entered.TrySetResult(true); await release.Task; } };
        var model = new ScriptStatefulModel();
        var plugin = new ReadingPlugin();
        await using (var agent = Create(model, repository, plugin, messages: [User("configured", "configured-u")]))
        {
            Task? loading = null;
            try
            {
                loading = agent.InitializeAsync(caller.Token);
                await Wait(entered.Task);
                caller.Cancel();
                release.TrySetResult(true);
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
                Assert.Equal(caller.Token, error.CancellationToken);
                Assert.Equal("configured", Assert.Single(agent.Messages).Text);
                Assert.Equal("configured", agent.State.Get<string>("label"));
                Assert.Equal("configured", agent.ModelState.Get<string>("responseId"));
                Assert.Equal("configured", agent.SystemPrompt);
                Assert.Equal(0, plugin.Calls);
                Assert.Empty(model.Requests);
                Assert.Empty(repository.Writes);
            }
            finally { release.TrySetResult(true); await Drain(loading); }
        }
        repository.AfterRead = null;
        var after = await Bytes(repository.Storage);
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.Ordinal), after.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
        await using var reopened = files.Open();
        var observer = new ReadingPlugin();
        await using var retry = Create(new ScriptStatefulModel(), reopened, observer);
        await retry.InitializeAsync();
        Assert.Equal("active-u", Assert.Single(retry.Messages).TrackingId);
        Assert.Equal("state-1", retry.ModelState.Get<string>("responseId"));
        Assert.Equal(1, observer.Calls);
        Assert.Equal(0, repository.Disposals);
    }

    [Fact]
    public async Task IndexedAbortExtraRowKeepsReceiptAndAcceptedProjection()
    {
        using var files = new FileFixture();
        await using var repository = files.Open();
        await SeedReceipt(repository);
        var effects = 0;
        var firstModel = new ScriptStatefulModel();
        {
            await using var agent = Create(firstModel, repository, middleware: SyntheticApproval(), tools: [Approval(() => effects++)]);
            var paused = await agent.InvokeAsync(null);
            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            Assert.Empty(firstModel.Requests);
            Assert.Equal(0, firstModel.IteratorDisposals);
            Assert.Equal(0, effects);
            var sentinel = new IOException("abort metadata");
            var failOnce = true;
            repository.BeforeUpdate = (_, _) => { if (failOnce) { failOnce = false; throw sentinel; } return default; };
            Assert.Same(sentinel, await Assert.ThrowsAsync<IOException>(async () => await agent.AbortInterruptsAsync("not approved")));
            Assert.NotNull(agent.InterruptState);
            Assert.True(await agent.AbortInterruptsAsync("not approved"));
            Assert.Null(agent.InterruptState);
            var rows = await repository.ListMessagesAsync(Session, AgentId);
            Assert.Equal(rows.Count, rows.Select(row => row.MessageId).Distinct().Count());
            var error = Assert.Single(rows.Select(row => row.ToMessage()).SelectMany(message => message.Content).OfType<ToolResultBlock>());
            Assert.Equal("approve-call", error.ToolUseId);
            Assert.Equal(ToolResultStatus.Error, error.Status);
            Assert.Equal("not approved", error.Content);
            var record = (await repository.ReadAgentAsync(Session, AgentId))!;
            Assert.Equal(3, record.InternalState.GetProperty("nativeSessionVersion").GetInt32());
            Assert.Equal(new ModelInputReceipt(1, "active-u", "discarded-a"), Metadata(record).ModelInputReceipt);
            Assert.Equal(rows.Skip(2).Select(row => row.MessageId), ActiveIds(record));
            Assert.Equal("state-1", Metadata(record).ModelState!["responseId"].GetString());
        }
        await using var reopened = files.Open();
        var model = new ScriptStatefulModel(Call("answer", 7, "state-2"));
        await using var reader = Create(model, reopened);
        Assert.Equal(7, Value(await reader.InvokeAsync(null)));
        var request = Assert.Single(model.Requests);
        Assert.Equal(new[] { Role.Assistant, Role.User }, request.Messages.Select(message => message.Role));
        Assert.DoesNotContain(request.Messages, message => message.TrackingId == "active-u");
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        var finalRows = await reopened.ListMessagesAsync(Session, AgentId);
        Assert.Equal(1, finalRows.Count(row => row.ToMessage().TrackingId == "active-u"));
        Assert.Equal(finalRows.Count, finalRows.Select(row => row.MessageId).Distinct().Count());
        var final = (await reopened.ReadAgentAsync(Session, AgentId))!;
        Assert.Equal(2, final.InternalState.GetProperty("nativeSessionVersion").GetInt32());
        Assert.Null(Metadata(final).ModelInputReceipt);
        Assert.Equal(0, effects);
        Assert.Equal(0, repository.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrecognizedLegacySnapshotMetadataIsNotBroadlyRejected(bool applicationOnly)
    {
        using var files = new FileFixture();
        await using var repository = files.Open();
        var internalState = applicationOnly
            ? JsonSerializer.SerializeToElement(new { snapshot = new { application = "custom", schemaVersion = "application" } })
            : JsonSerializer.SerializeToElement(new { snapshot = new { schemaVersion = 2, modelInputReceipt = (object?)null } });
        await Seed(repository, internalState, [new(User("legacy", "legacy-u"), 0), new(new Message(Role.Assistant, [new TextBlock("legacy answer")], "legacy-a"), 1)]);
        var before = await Bytes(repository.Storage);
        repository.Writes.Clear();
        var observer = new ReadingPlugin();
        await using var agent = Create(new UnusedModel(), repository, observer);
        await agent.InitializeAsync();
        Assert.Equal(new[] { "legacy-u", "legacy-a" }, agent.Messages.Select(message => message.TrackingId));
        Assert.Equal(1, observer.Calls);
        Assert.Equal("update-agent", Assert.Single(repository.Writes));
        var after = await Bytes(repository.Storage);
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.Ordinal), after.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var item in before.Where(item => !item.Key.EndsWith("/agent.json", StringComparison.Ordinal)))
            Assert.Equal(item.Value, after[item.Key]);
        // Initialized synchronization canonically writes native metadata without rewriting archive rows.
        var record = (await repository.ReadAgentAsync(Session, AgentId))!;
        Assert.Equal(1, record.InternalState.GetProperty("nativeSessionVersion").GetInt32());
        Assert.Equal(1, Metadata(record).SchemaVersion);
        Assert.Null(Metadata(record).ModelInputReceipt);
        Assert.Equal("saved", record.State.GetProperty("label").GetString());
        Assert.Equal(0, repository.Disposals);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
        await task;
    }
    private static async Task Drain(Task? task)
    {
        if (task is null) return;
        try { await task; } catch (Exception) { /* The test body owns the operation outcome; cleanup only joins it. */ }
    }
    private static MiddlewareRegistry SyntheticApproval()
    {
        var middleware = new MiddlewareRegistry();
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, Synthetic);
        return middleware;
        static async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> Synthetic(InvokeModelContext context,
            MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // This synthetic pass consumes no provider input and cannot acknowledge the saved receipt.
            var message = new Message(Role.Assistant, [new ToolUseBlock("approve-call", "approve", JsonSerializer.SerializeToElement(new { }))], "synthetic-a");
            yield return new MiddlewareResultEvent<InvokeModelResult>(new(message, StopReasons.ToolUse, new()));
            await Task.CompletedTask;
        }
    }
    private static ITool Approval(Action effect) => new FunctionTool(new("approve", "Approve", JsonSerializer.SerializeToElement(new { type = "object" })), (_, context, _) =>
    {
        context.Interrupt("approval");
        effect();
        return new ValueTask<string>("approved");
    });

    private static Agent Create(IModel model, ProbeRepository repository, ReadingPlugin? plugin = null, int maxTurns = 5,
        IReadOnlyList<Message>? messages = null, HookRegistry? hooks = null, MiddlewareRegistry? middleware = null, IReadOnlyList<ITool>? tools = null)
    {
        var manager = new RepositorySessionManager(Session, repository);
        return new(new()
        {
            Id = AgentId,
            Model = model,
            MaxTurns = maxTurns,
            SystemPrompt = "configured",
            State = Values("label", "configured"),
            ModelState = Values("responseId", "configured"),
            Messages = messages ?? [],
            Middleware = middleware ?? new(),
            Tools = tools ?? [],
            Hooks = hooks ?? new(),
            Plugins = plugin is null ? [manager] : [manager, plugin],
            StructuredOutput = new StructuredOutputOptions<JsonElement>
            {
                Behavior = StructuredOutputBehavior.TypeScript,
                Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
            }
        });
    }

    private static SessionSnapshot Receipt() => new(2, [])
    {
        AgentId = AgentId,
        HasSystemPrompt = true,
        SystemPrompt = "saved-prompt",
        ModelHistoryCount = 0,
        ModelState = Values("responseId", "state-1"),
        ModelInputReceipt = new(1, "active-u", "discarded-a")
    };
    private static SessionMessageRecord[] ReceiptRows() =>
    [new(User("archived", "archived-u"), 0), new(new Message(Role.Assistant, [new TextBlock("archived-answer")], "archived-a"), 1), new(User("active", "active-u"), 2)];
    private static ValueTask SeedReceipt(ProbeRepository repository) => Seed(repository, NativeState(Receipt(), 3, [2], 2), ReceiptRows());
    private static async ValueTask Seed(ProbeRepository repository, JsonElement internalState, IReadOnlyList<SessionMessageRecord> rows)
    {
        await repository.CreateSessionAsync(new(Session));
        await repository.CreateAgentAsync(Session, new(AgentId, JsonSerializer.SerializeToElement(Values("label", "saved")), JsonSerializer.SerializeToElement(new { }), internalState));
        foreach (var row in rows) await repository.CreateMessageAsync(Session, AgentId, row);
    }
    private static JsonElement NativeState(SessionSnapshot snapshot, int version, long[] ids, long last)
    {
        var data = new Dictionary<string, object?>
        {
            ["nativeSessionVersion"] = version,
            ["snapshot"] = snapshot,
            ["journalFrontier"] = new { lastMessageId = last, excludedMessageIds = Array.Empty<long>() }
        };
        if (version != 1) data["statefulProjection"] = new { version = 1, activeMessageIds = ids };
        return JsonSerializer.SerializeToElement(data, Web);
    }
    private static SessionSnapshot Metadata(SessionAgentRecord record) => record.InternalState.GetProperty("snapshot").Deserialize<SessionSnapshot>(Web)!;
    private static long[] ActiveIds(SessionAgentRecord record) => record.InternalState.GetProperty("statefulProjection").GetProperty("activeMessageIds").EnumerateArray().Select(value => value.GetInt64()).ToArray();
    private static async Task<Dictionary<string, byte[]>> Bytes(IStorage storage)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in await storage.ListAsync()) result.Add(key, (await storage.ReadAsync(key))!);
        return result;
    }
    private static Dictionary<string, JsonElement> Values(string key, string value) => new() { [key] = JsonSerializer.SerializeToElement(value) };
    private static Message User(string text, string id) => new(Role.User, [new TextBlock(text)], id);
    private static int Value(AgentResult result) => result.StructuredOutput!.Value.GetProperty("value").GetInt32();
    private sealed record Turn(string State, string Reason, ContentBlock[] Blocks);
    private static Turn Text(string text, string state) => new(state, StopReasons.EndTurn, [new TextBlock(text)]);
    private static Turn Call(string id, int value, string state) => new(state, StopReasons.ToolUse, [new ToolUseBlock(id, OutputName, JsonSerializer.SerializeToElement(new { value }))]);

    private sealed class ScriptStatefulModel(params Turn[] turns) : IStatefulModel, IAsyncDisposable
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        internal Action<ModelRequest>? BeforeRequest { get; set; }
        internal int IteratorDisposals { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeRequest?.Invoke(request);
            Requests.Add(request with
            {
                Messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray(),
                ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            });
            if (Requests.Count > turns.Length) throw new InvalidOperationException("No extra fake provider call is allowed.");
            var turn = turns[Requests.Count - 1];
            try
            {
                yield return new ModelMessageStartEvent();
                for (var index = 0; index < turn.Blocks.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (turn.Blocks[index] is ToolUseBlock call)
                    {
                        yield return new ModelContentBlockStartEvent(index, call);
                        yield return new ModelContentBlockStopEvent(index);
                    }
                    else
                    {
                        yield return new ModelContentBlockStartEvent(index) { ContentBlock = turn.Blocks[index] };
                        yield return new ModelContentBlockStopEvent(index) { ContentBlock = turn.Blocks[index] };
                    }
                }
                yield return new ModelMessageStopEvent(turn.Reason) { ModelState = Values("responseId", turn.State) };
                await Task.CompletedTask;
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class UnusedModel : IModel
    {
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Initialization must not call a model.");
    }
    private sealed class ReadingPlugin : IPlugin
    {
        public string Name => "test:receipt-indexed-reader";
        internal int Calls { get; private set; }
        internal Message[]? Messages { get; private set; }
        internal string? ResponseId { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Messages = agent.Messages.ToArray(); ResponseId = agent.ModelState.Get<string>("responseId");
            return default;
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class ProbeRepository(IStorage storage) : ISessionRepository, IAsyncDisposable
    {
        private readonly StorageSessionRepository _inner = new(storage);
        internal IStorage Storage { get; } = storage;
        internal List<string> Writes { get; } = [];
        internal Func<SessionAgentRecord, CancellationToken, ValueTask>? BeforeUpdate { get; set; }
        internal Func<string, CancellationToken, ValueTask>? AfterRead { get; set; }
        internal bool IgnoreUpdateCancellation { get; set; }
        internal int Disposals { get; private set; }
        public async ValueTask<SessionRecord> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default)
        { Writes.Add("create-session"); return await _inner.CreateSessionAsync(session, cancellationToken); }
        public ValueTask<SessionRecord?> ReadSessionAsync(string id, CancellationToken cancellationToken = default) => _inner.ReadSessionAsync(id, cancellationToken);
        public async ValueTask CreateAgentAsync(string id, SessionAgentRecord agent, CancellationToken cancellationToken = default)
        { Writes.Add("create-agent"); await _inner.CreateAgentAsync(id, agent, cancellationToken); }
        public async ValueTask<SessionAgentRecord?> ReadAgentAsync(string id, string agent, CancellationToken cancellationToken = default)
        { var record = await _inner.ReadAgentAsync(id, agent, cancellationToken); if (AfterRead is { } after) await after("read-agent", cancellationToken); return record; }
        public async ValueTask UpdateAgentAsync(string id, SessionAgentRecord agent, CancellationToken cancellationToken = default)
        { Writes.Add("update-agent"); if (BeforeUpdate is { } before) await before(agent, cancellationToken); await _inner.UpdateAgentAsync(id, agent, IgnoreUpdateCancellation ? CancellationToken.None : cancellationToken); }
        public async ValueTask CreateMessageAsync(string id, string agent, SessionMessageRecord message, CancellationToken cancellationToken = default)
        { Writes.Add("create-message"); await _inner.CreateMessageAsync(id, agent, message, cancellationToken); }
        public ValueTask<SessionMessageRecord?> ReadMessageAsync(string id, string agent, long message, CancellationToken cancellationToken = default) => _inner.ReadMessageAsync(id, agent, message, cancellationToken);
        public async ValueTask UpdateMessageAsync(string id, string agent, SessionMessageRecord message, CancellationToken cancellationToken = default)
        { Writes.Add("update-message"); await _inner.UpdateMessageAsync(id, agent, message, cancellationToken); }
        public async ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string id, string agent, int? limit = null, int offset = 0, CancellationToken cancellationToken = default)
        { var rows = await _inner.ListMessagesAsync(id, agent, limit, offset, cancellationToken); if (AfterRead is { } after) await after("list-messages", cancellationToken); return rows; }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class FileFixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "receipt-indexed-" + Guid.NewGuid().ToString("N"));
        private string StorageRoot
        {
            get
            {
                var root = Path.GetFullPath(Root);
                if (Path.DirectorySeparatorChar != '\\' || root.StartsWith(@"\\?\", StringComparison.Ordinal)) return root;
                return root.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + root.Substring(2) : @"\\?\" + root;
            }
        }
        internal ProbeRepository Open() => new(new LocalFileStorage(StorageRoot));
        public void Dispose()
        {
            if (!Directory.Exists(StorageRoot)) return;
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test fixture must remain inside its selected temporary directory.");
            Directory.Delete(StorageRoot, true);
        }
    }
}
