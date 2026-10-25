using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Middleware;
using Strands.Agents.SessionCatalogs;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class ReceiptCatalogSafetyTests
{
    private const string Session = "receipt-session";
    private const string AgentId = "catalog-agent";
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static string LatestKey => Session + "/scopes/agent/" + AgentId + "/snapshots/snapshot_latest.json";


    [Fact]
    public async Task FailedLatestReceiptWriteRemainsDirtyAndRetriesWithoutProviderReplay()
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        var sentinel = new IOException("latest receipt write");
        var failed = false;
        var accepted = new List<SessionSnapshot>();
        storage.BeforeWrite = (key, bytes, _) =>
        {
            using var document = JsonDocument.Parse(bytes);
            if (key == LatestKey)
            {
                var snapshot = document.RootElement.GetProperty("snapshot").Deserialize<SessionSnapshot>(Web)!;
                if (!failed && snapshot.SchemaVersion == 2) { failed = true; throw sentinel; }
                accepted.Add(snapshot);
            }
            return default;
        };
        var model = new ScriptStatefulModel(Text("discarded", "state-1"), Call("answer", 7, "state-2"));
        var manager = new SnapshotSessionManager(storage, Session);
        await using var agent = Create(model, manager, maxTurns: 1);
        var error = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));
        Assert.Same(sentinel, error);
        Assert.Single(model.Requests);
        Assert.Equal("state-1", agent.ModelState.Get<string>("responseId"));
        Assert.Equal("question", Assert.Single(agent.Messages).Text);
        var live = await agent.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.Equal("2.0", live.SchemaVersion);
        Assert.Equal(1, live.Data["modelInputReceipt"].GetProperty("consumedMessageCount").GetInt32());
        Assert.Equal("state-1", live.Data["modelState"].GetProperty("responseId").GetString());
        Assert.Empty(accepted);

        Assert.Equal(7, Value(await agent.InvokeAsync(null, new() { MaxTurns = 2 })));

        Assert.Empty(model.Requests[1].Messages);
        Assert.Equal("state-1", model.Requests[1].ModelState!["responseId"].GetString());
        // Invocation policy retries at a later completion, capturing its newer provider state.
        var latest = await ReadLatest(storage);
        var committed = Assert.Single(accepted);
        Assert.Equal(1, latest.SchemaVersion);
        Assert.Null(latest.ModelInputReceipt);
        Assert.Equal("state-2", latest.ModelState!["responseId"].GetString());
        Assert.Equal(2, latest.ModelHistoryCount);
        Assert.Equal(1, latest.Messages.Count(message => message.Text == "question"));
        Assert.Equal(latest.ModelInputReceipt, committed.ModelInputReceipt);
        Assert.Equal("state-2", committed.ModelState!["responseId"].GetString());
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(2, model.IteratorDisposals);
        Assert.Equal(0, model.Disposals);
        Assert.Equal(0, storage.Disposals);
    }

    [Fact]
    public async Task TriggeredImmutableAcceptancePrecedesFailedLatestAndUsesOneReceiptPair()
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        var previous = SnapshotSessionCodec.Write(Receipt(), Session, AgentId, null, true);
        await storage.WriteAsync(LatestKey, previous);
        var sentinel = new IOException("latest after immutable");
        var failed = false;
        storage.BeforeWrite = (key, _, _) =>
        {
            if (key == LatestKey && !failed) { failed = true; throw sentinel; }
            return default;
        };
        var manager = new SnapshotSessionManager(storage, Session, SnapshotSessionSaveStrategy.Trigger, _ => true);
        var model = new ScriptStatefulModel(Text("discarded", "state-2"));
        await using var agent = Create(model, manager, maxTurns: 1);

        Assert.Same(sentinel, await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("new question")));

        Assert.Equal(previous, await storage.ReadAsync(LatestKey));
        var id = Assert.Single(await manager.ListSnapshotIdsAsync(agent));
        var key = Session + "/scopes/agent/" + AgentId + "/snapshots/immutable_history/snapshot_" + id + ".json";
        var immutable = SnapshotSessionCodec.Read((await storage.ReadAsync(key))!, Session, AgentId, id, true);
        Assert.Equal(2, immutable.SchemaVersion);
        Assert.Equal(2, immutable.ModelInputReceipt!.ConsumedMessageCount);
        Assert.Equal(immutable.Messages.Last().TrackingId, immutable.ModelInputReceipt.LastConsumedTrackingId);
        Assert.Equal("state-2", immutable.ModelState!["responseId"].GetString());
        Assert.Null(await manager.SaveSnapshotAsync(agent, true));
        var latest = await ReadLatest(storage);
        Assert.Equal(immutable.ModelInputReceipt, latest.ModelInputReceipt);
        Assert.Equal(immutable.Messages.Select(message => message.TrackingId), latest.Messages.Select(message => message.TrackingId));
        Assert.Single(model.Requests);
        Assert.Equal("new question", Assert.Single(model.Requests[0].Messages).Text);
        Assert.Equal(0, storage.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldReceiptWriteDetachesSnapshotAndPhysicallyJoins(bool cancelCaller)
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        using var caller = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        byte[]? captured = null;
        storage.IgnoreWriteCancellation = true;
        storage.BeforeWrite = async (key, bytes, _) =>
        {
            using var document = JsonDocument.Parse(bytes);
            if (key != LatestKey || document.RootElement.GetProperty("snapshot").GetProperty("schemaVersion").GetInt32() != 2) return;
            captured = (byte[])bytes.Clone();
            entered.TrySetResult(true);
            await release.Task;
        };
        var model = new ScriptStatefulModel(Text("discarded", "state-1"));
        var manager = new SnapshotSessionManager(storage, Session);
        await using var agent = Create(model, manager, maxTurns: 1);
        Task<AgentResult>? invocation = null;
        try
        {
            invocation = agent.InvokeAsync("question", cancellationToken: caller.Token);
            await Wait(entered.Task);
            agent.State.Set("label", "after-capture");
            agent.ModelState.Set("application", "after-capture");
            if (cancelCaller) caller.Cancel();
            Assert.False(invocation.IsCompleted);
            using var bytes = JsonDocument.Parse(captured!);
            var selected = bytes.RootElement.GetProperty("snapshot").Deserialize<SessionSnapshot>(Web)!;
            AssertReceipt(selected, "state-1");
            Assert.Equal("configured", selected.State!["label"].GetString());
            Assert.False(selected.ModelState!.ContainsKey("application"));
            release.TrySetResult(true);
            // This write runs in completion after the terminal MaxTurns result was accepted.
            Assert.Equal(StopReasons.MaxTurns, (await invocation).StopReason);
            Assert.Equal(cancelCaller, caller.IsCancellationRequested);
            Assert.Equal(captured, await storage.ReadAsync(LatestKey));
            Assert.Single(model.Requests);
            Assert.Equal(1, model.IteratorDisposals);
            Assert.Equal(0, storage.Disposals);
        }
        finally { release.TrySetResult(true); await Drain(invocation); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptRestoreCancellationNeverPublishesPartialState(bool holdRead)
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        var original = SnapshotSessionCodec.Write(Receipt(), Session, AgentId, null, true);
        await storage.WriteAsync(LatestKey, original);
        storage.Writes.Clear();
        using var caller = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        storage.BeforeRead = async (key, _) => { if (key == LatestKey) { entered.TrySetResult(true); await release.Task; } };
        var model = new ScriptStatefulModel();
        var observer = new ReadingPlugin();
        var manager = new SnapshotSessionManager(storage, Session);
        await using var agent = Create(model, manager, observer, messages: [new Message(Role.User, [new TextBlock("configured")], "configured-u")]);
        Task<bool>? restoring = null;
        try
        {
            if (!holdRead) caller.Cancel();
            restoring = manager.RestoreSnapshotAsync(agent, cancellationToken: caller.Token).AsTask();
            if (holdRead)
            {
                await Wait(entered.Task);
                caller.Cancel();
                Assert.False(restoring.IsCompleted);
                release.TrySetResult(true);
            }
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoring);
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Equal("configured", Assert.Single(agent.Messages).Text);
            Assert.Equal("configured", agent.State.Get<string>("label"));
            Assert.Equal("configured", agent.ModelState.Get<string>("responseId"));
            Assert.Equal("configured", agent.SystemPrompt);
            Assert.Equal(0, observer.Calls);
            Assert.Empty(model.Requests);
            Assert.Empty(storage.Writes);
        }
        finally { release.TrySetResult(true); await Drain(restoring); }
        storage.BeforeRead = null;
        Assert.Equal(original, await storage.ReadAsync(LatestKey));
        Assert.Equal(0, storage.Disposals);
    }

    [Fact]
    public async Task CatalogAbortSyntheticToolPausePreservesReceiptPair()
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        await storage.WriteAsync(LatestKey, SnapshotSessionCodec.Write(Receipt(), Session, AgentId, null, true));
        var effects = 0;
        var firstModel = new ScriptStatefulModel();
        {
            var manager = new SnapshotSessionManager(storage, Session, SnapshotSessionSaveStrategy.Message);
            await using var agent = Create(firstModel, manager, middleware: SyntheticApproval(), tools: [Approval(() => effects++)]);
            var paused = await agent.InvokeAsync(null);
            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            Assert.Single(paused.Interrupts);
            Assert.Empty(firstModel.Requests);
            Assert.Equal(0, firstModel.IteratorDisposals);
            Assert.Equal(0, effects);
            Assert.True(await agent.AbortInterruptsAsync("not approved"));
            Assert.Null(agent.InterruptState);
            var latest = await ReadLatest(storage);
            AssertReceipt(latest, "state-1");
            var result = Assert.IsType<ToolResultBlock>(Assert.Single(latest.Messages.Last().Content));
            Assert.Equal("approve-call", result.ToolUseId);
            Assert.Equal(ToolResultStatus.Error, result.Status);
            Assert.Equal("not approved", result.Content);
        }
        var model = new ScriptStatefulModel(Call("answer", 7, "state-2"));
        var reopened = new SnapshotSessionManager(storage, Session, SnapshotSessionSaveStrategy.Message);
        await using var reader = Create(model, reopened);
        Assert.Equal(7, Value(await reader.InvokeAsync(null)));
        var request = Assert.Single(model.Requests);
        Assert.Equal(new[] { Role.Assistant, Role.User }, request.Messages.Select(message => message.Role));
        Assert.DoesNotContain(request.Messages, message => message.TrackingId == "consumed-u");
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        Assert.Null((await ReadLatest(storage)).ModelInputReceipt);
        Assert.Equal(0, effects);
        Assert.Equal(0, storage.Disposals);
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

    private static Agent Create(IModel model, SnapshotSessionManager manager, ReadingPlugin? plugin = null,
        int maxTurns = 5, IReadOnlyList<Message>? messages = null, MiddlewareRegistry? middleware = null, IReadOnlyList<ITool>? tools = null) => new(new()
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
            Plugins = plugin is null ? [manager] : [manager, plugin],
            StructuredOutput = new StructuredOutputOptions<JsonElement>
            {
                Behavior = StructuredOutputBehavior.TypeScript,
                Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
            }
        });

    private static SessionSnapshot Receipt() => new(2, [new Message(Role.User, [new TextBlock("consumed")], "consumed-u")])
    {
        AgentId = AgentId,
        State = Values("label", "saved"),
        ModelState = Values("responseId", "state-1"),
        HasSystemPrompt = true,
        SystemPrompt = "saved-prompt",
        ModelHistoryCount = 0,
        ModelInputReceipt = new(1, "consumed-u", "discarded-a")
    };

    private static AgentSnapshot PublicReceipt() => new(new Dictionary<string, JsonElement>
    {
        ["messages"] = JsonSerializer.SerializeToElement(Receipt().Messages, Web),
        ["state"] = JsonSerializer.SerializeToElement(Receipt().State, Web),
        ["modelState"] = JsonSerializer.SerializeToElement(Receipt().ModelState, Web),
        ["systemPrompt"] = JsonSerializer.SerializeToElement("saved-prompt"),
        ["modelHistoryCount"] = JsonSerializer.SerializeToElement(0),
        ["modelInputReceipt"] = JsonSerializer.SerializeToElement(Receipt().ModelInputReceipt, Web)
    }, schemaVersion: "2.0");

    private static async Task<SessionSnapshot> ReadLatest(IStorage storage)
    {
        var bytes = Assert.IsType<byte[]>(await storage.ReadAsync(LatestKey));
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        return document.RootElement.GetProperty("snapshot").Deserialize<SessionSnapshot>(Web)!;
    }

    private static void AssertReceipt(SessionSnapshot snapshot, string state)
    {
        Assert.Equal(0, snapshot.ModelHistoryCount);
        Assert.NotNull(snapshot.ModelInputReceipt);
        Assert.Equal(1, snapshot.ModelInputReceipt.ConsumedMessageCount);
        Assert.Equal(snapshot.Messages[0].TrackingId, snapshot.ModelInputReceipt.LastConsumedTrackingId);
        Assert.DoesNotContain(snapshot.Messages, message => message.TrackingId == snapshot.ModelInputReceipt.DiscardedResponseTrackingId);
        Assert.Equal(state, snapshot.ModelState!["responseId"].GetString());
    }

    private static IReadOnlyDictionary<string, JsonElement> Values(string key, string value) => new Dictionary<string, JsonElement>
    { [key] = JsonSerializer.SerializeToElement(value) };
    private static int Value(AgentResult result) => result.StructuredOutput!.Value.GetProperty("value").GetInt32();
    private sealed record Turn(string State, ContentBlock[] Blocks, string Reason);
    private static Turn Text(string text, string state) => new(state, [new TextBlock(text)], StopReasons.EndTurn);
    private static Turn Call(string id, int value, string state) => new(state,
        [new ToolUseBlock(id, OutputName, JsonSerializer.SerializeToElement(new { value }))], StopReasons.ToolUse);

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
        public string Name => "test:receipt-catalog-reader";
        internal int Calls { get; private set; }
        internal Message[]? Messages { get; private set; }
        internal string? ResponseId { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Messages = agent.Messages.ToArray();
            ResponseId = agent.ModelState.Get<string>("responseId");
            return default;
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class RecordingStorage(IStorage inner) : IStorage, IStorageCharacteristics, IAsyncDisposable
    {
        public bool IsEphemeral => false;
        public bool IsNamespaced => true;
        internal List<string> Writes { get; } = [];
        internal Func<string, byte[], CancellationToken, ValueTask>? BeforeWrite { get; set; }
        internal Func<string, CancellationToken, ValueTask>? BeforeRead { get; set; }
        internal bool IgnoreWriteCancellation { get; set; }
        internal int Disposals { get; private set; }
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        { if (BeforeWrite is { } before) await before(key, data, cancellationToken); await inner.WriteAsync(key, data, IgnoreWriteCancellation ? CancellationToken.None : cancellationToken); Writes.Add(key); }
        public async ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
        { var data = await inner.ReadAsync(key, cancellationToken); if (BeforeRead is { } before) await before(key, cancellationToken); return data; }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }

    private sealed class FileFixture : IDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "receipt-catalog-" + Guid.NewGuid().ToString("N"));
        internal RecordingStorage Open() => new(new LocalFileStorage(Root).Namespace("fixture"));
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The test fixture must remain inside its selected temporary directory.");
            Directory.Delete(Root, true);
        }
    }
}
