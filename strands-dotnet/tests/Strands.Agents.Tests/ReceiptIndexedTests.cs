using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class ReceiptIndexedTests
{
    private const string Session = "receipt-indexed";
    private const string AgentId = "indexed-agent";
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task IndexedRealDiscardRoundTripsProviderStateWithoutReappendingConsumedInput()
    {
        using var files = new FileFixture();
        await using var firstRepository = files.Open();
        var firstModel = new ScriptStatefulModel(Text("ignored", "state-1"));
        {
            await using var writer = Create(firstModel, firstRepository, maxTurns: 1);
            var result = await writer.InvokeAsync("question");
            Assert.Equal(StopReasons.MaxTurns, result.StopReason);
            var record = Assert.IsType<SessionAgentRecord>(await firstRepository.ReadAgentAsync(Session, AgentId));
            Assert.Equal(3, record.InternalState.GetProperty("nativeSessionVersion").GetInt32());
            var metadata = Metadata(record);
            Assert.Equal(2, metadata.SchemaVersion);
            Assert.Empty(metadata.Messages);
            Assert.Equal(0, metadata.ModelHistoryCount);
            Assert.Equal(1, metadata.ModelInputReceipt!.ConsumedMessageCount);
            Assert.Equal("state-1", metadata.ModelState!["responseId"].GetString());
            Assert.Equal(1, record.InternalState.GetProperty("statefulProjection").GetProperty("version").GetInt32());
            var rows = await firstRepository.ListMessagesAsync(Session, AgentId);
            var user = Assert.Single(rows);
            Assert.Equal(0, user.MessageId);
            Assert.Equal("question", user.ToMessage().Text);
            Assert.Equal(user.ToMessage().TrackingId, metadata.ModelInputReceipt.LastConsumedTrackingId);
            Assert.Equal(new long[] { 0 }, ActiveIds(record));
            Assert.DoesNotContain(rows, row => row.ToMessage().Text == "ignored");
        }
        Assert.Equal(0, firstModel.Disposals);
        Assert.Equal(1, firstModel.IteratorDisposals);
        Assert.Equal(0, firstRepository.Disposals);

        await using var reopened = files.Open();
        var model = new ScriptStatefulModel(Call("answer", 7, "state-2"));
        var plugin = new ReadingPlugin();
        {
            await using var reader = Create(model, reopened, plugin);
            Assert.Equal(7, Value(await reader.InvokeAsync(null)));
            var request = Assert.Single(model.Requests);
            Assert.Empty(request.Messages);
            Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
            Assert.Null(request.ToolChoice);
            Assert.Equal("question", Assert.Single(plugin.Messages!).Text);
            Assert.Equal("state-1", plugin.ResponseId);
            var record = Assert.IsType<SessionAgentRecord>(await reopened.ReadAgentAsync(Session, AgentId));
            Assert.Equal(2, record.InternalState.GetProperty("nativeSessionVersion").GetInt32());
            Assert.Equal(1, Metadata(record).SchemaVersion);
            Assert.Null(Metadata(record).ModelInputReceipt);
            Assert.Equal("state-2", Metadata(record).ModelState!["responseId"].GetString());
            var rows = await reopened.ListMessagesAsync(Session, AgentId);
            Assert.Equal(new long[] { 0, 1, 2 }, rows.Select(row => row.MessageId));
            Assert.Equal(1, rows.Count(row => row.ToMessage().Text == "question"));
            Assert.Equal(new[] { Role.User, Role.Assistant, Role.User }, rows.Select(row => row.ToMessage().Role));
            var call = Assert.IsType<ToolUseBlock>(Assert.Single(rows[1].ToMessage().Content));
            Assert.Equal("answer", call.ToolUseId);
            Assert.Equal(OutputName, call.Name);
            var accepted = Assert.IsType<ToolResultBlock>(Assert.Single(rows[2].ToMessage().Content));
            Assert.Equal(call.ToolUseId, accepted.ToolUseId);
            Assert.Equal(ToolResultStatus.Success, accepted.Status);
            Assert.Equal(7, Assert.IsType<JsonBlock>(Assert.Single(accepted.ContentBlocks)).Json.GetProperty("value").GetInt32());
            Assert.Equal(new long[] { 0, 1, 2 }, ActiveIds(record));
            Assert.Equal(2, Metadata(record).ModelHistoryCount);
        }
        Assert.Equal(0, model.Disposals);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Equal(0, reopened.Disposals);
    }

    [Fact]
    public async Task SeededReceiptVersionHydratesBoundaryAgainstActiveRowsNotWholeArchive()
    {
        using var files = new FileFixture();
        await using (var seed = files.Open()) await SeedReceipt(seed);
        await using var repository = files.Open();
        var model = new ScriptStatefulModel(Call("accepted", 8, "state-2"));
        var plugin = new ReadingPlugin();
        await using var agent = Create(model, repository, plugin);

        await agent.InitializeAsync();

        Assert.Equal("active-u", Assert.Single(agent.Messages).TrackingId);
        Assert.Equal("state-1", agent.ModelState.Get<string>("responseId"));
        Assert.Equal("active-u", Assert.Single(plugin.Messages!).TrackingId);
        Assert.Empty(model.Requests);
        Assert.Equal(8, Value(await agent.InvokeAsync(null)));
        var request = Assert.Single(model.Requests);
        Assert.Empty(request.Messages);
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        var allRows = await repository.ListMessagesAsync(Session, AgentId);
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, allRows.Select(row => row.MessageId));
        Assert.Equal(new[] { Role.User, Role.Assistant, Role.User, Role.Assistant, Role.User }, allRows.Select(row => row.ToMessage().Role));
        var call = Assert.IsType<ToolUseBlock>(Assert.Single(allRows[3].ToMessage().Content));
        Assert.Equal("accepted", call.ToolUseId);
        Assert.Equal(OutputName, call.Name);
        var accepted = Assert.IsType<ToolResultBlock>(Assert.Single(allRows[4].ToMessage().Content));
        Assert.Equal(call.ToolUseId, accepted.ToolUseId);
        Assert.Equal(ToolResultStatus.Success, accepted.Status);
        Assert.Equal(8, Assert.IsType<JsonBlock>(Assert.Single(accepted.ContentBlocks)).Json.GetProperty("value").GetInt32());
        Assert.Equal(new[] { "archived-u", "archived-a", "active-u" }, allRows.Take(3).Select(row => row.ToMessage().TrackingId));
        Assert.Equal(1, allRows.Count(row => row.ToMessage().TrackingId == "active-u"));
        var record = Assert.IsType<SessionAgentRecord>(await repository.ReadAgentAsync(Session, AgentId));
        Assert.Equal(2, record.InternalState.GetProperty("nativeSessionVersion").GetInt32());
        Assert.Equal(new long[] { 2, 3, 4 }, ActiveIds(record));
        Assert.Equal(2, Metadata(record).ModelHistoryCount);
        Assert.Null(Metadata(record).ModelInputReceipt);
        Assert.Equal(1, plugin.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexedPriorModesRemainReadable(bool stateful)
    {
        // Both the native1 stateless form and a legacy application-only internal object stay readable.
        foreach (var legacy in stateful ? new[] { false } : new[] { false, true })
        {
            using var files = new FileFixture();
            await using var repository = files.Open();
            var snapshot = new SessionSnapshot(1, [])
            {
                AgentId = AgentId,
                HasSystemPrompt = true,
                SystemPrompt = "saved-prompt",
                ModelHistoryCount = stateful ? 2 : null,
                ModelState = Values("responseId", "state-1")
            };
            var internalState = legacy ? JsonSerializer.SerializeToElement(new { applicationExtension = "legacy" })
                : NativeState(snapshot, stateful ? 2 : 1, [0, 1], 1);
            await Seed(repository, internalState,
                [new(User("old-u", "old-u"), 0), new(new Message(Role.Assistant, [new TextBlock("old-a")], "old-a"), 1)]);
            repository.Writes.Clear();
            var plugin = new ReadingPlugin();
            var model = new ScriptStatefulModel();
            await using var agent = Create(stateful ? model : new UnusedModel(), repository, plugin);
            await agent.InitializeAsync();
            Assert.Equal(new[] { "old-u", "old-a" }, agent.Messages.Select(message => message.Text));
            Assert.Equal("saved", agent.State.Get<string>("label"));
            Assert.Equal(stateful || !legacy ? "state-1" : "configured", agent.ModelState.Get<string>("responseId"));
            Assert.Equal(legacy ? "configured" : "saved-prompt", agent.SystemPrompt);
            Assert.Equal(1, plugin.Calls);
            Assert.Empty(model.Requests);
            Assert.Equal(new long[] { 0, 1 }, (await repository.ListMessagesAsync(Session, AgentId)).Select(row => row.MessageId));
            Assert.Equal(0, repository.Disposals);
        }
    }

    [Theory]
    [InlineData("native2-inner2")]
    [InlineData("native3-inner1")]
    [InlineData("native3-missing-receipt")]
    [InlineData("native1-inner2")]
    [InlineData("missing-marker-receipt")]
    public async Task IndexedVersionPairsCannotDowngradeOrInventReceiptCapability(string mutation)
    {
        using var files = new FileFixture();
        await using var repository = files.Open();
        var snapshot = Receipt();
        var version = mutation == "native2-inner2" ? 2 : mutation == "native1-inner2" ? 1 : 3;
        if (mutation == "native3-inner1") snapshot = snapshot with { SchemaVersion = 1 };
        if (mutation == "native3-missing-receipt") snapshot = snapshot with { ModelInputReceipt = null };
        var metadata = JsonNode.Parse(NativeState(snapshot, version, [2], 2).GetRawText())!.AsObject();
        if (mutation == "missing-marker-receipt")
        {
            // Keep only recognizable receipt-bearing native snapshot intent, so the ordinary
            // legacy-frontier check cannot accidentally stand in for the requested metadata gate.
            metadata.Remove("nativeSessionVersion");
            metadata.Remove("journalFrontier");
            metadata.Remove("statefulProjection");
        }
        await Seed(repository, JsonSerializer.SerializeToElement(metadata), ReceiptRows());
        await AssertRejectsBeforePublication(repository, stateful: mutation != "missing-marker-receipt");
    }

    [Theory]
    [InlineData("archived-boundary")]
    [InlineData("count-past-active")]
    [InlineData("empty-provider-state")]
    [InlineData("legacy-cursor-on-user")]
    public async Task HydratedReceiptValidationUsesCompleteActiveHistoryAndState(string mutation)
    {
        using var files = new FileFixture();
        await using var repository = files.Open();
        var snapshot = Receipt();
        switch (mutation)
        {
            case "archived-boundary": snapshot = snapshot with { ModelInputReceipt = snapshot.ModelInputReceipt! with { LastConsumedTrackingId = "archived-u" } }; break;
            case "count-past-active": snapshot = snapshot with { ModelInputReceipt = snapshot.ModelInputReceipt! with { ConsumedMessageCount = 3 } }; break;
            case "empty-provider-state": snapshot = snapshot with { ModelState = new Dictionary<string, JsonElement>() }; break;
            case "legacy-cursor-on-user": snapshot = snapshot with { ModelHistoryCount = 1 }; break;
        }
        await Seed(repository, NativeState(snapshot, 3, [2], 2), ReceiptRows());
        await AssertRejectsBeforePublication(repository, stateful: true);
    }

    private static async Task AssertRejectsBeforePublication(ProbeRepository repository, bool stateful)
    {
        var before = await Bytes(repository.Storage);
        repository.Writes.Clear();
        var plugin = new ReadingPlugin();
        var model = new ScriptStatefulModel();
        var initialized = 0;
        var hooks = new HookRegistry();
        using var registration = hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        await using var agent = Create(stateful ? model : new UnusedModel(), repository, plugin,
            messages: [User("configured", "configured-u")], hooks: hooks);
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Equal("configured", agent.SystemPrompt);
        Assert.Equal("configured", agent.State.Get<string>("label"));
        Assert.Equal("configured", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(0, plugin.Calls);
        Assert.Equal(0, initialized);
        Assert.Empty(model.Requests);
        Assert.Empty(repository.Writes);
        var after = await Bytes(repository.Storage);
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.Ordinal), after.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
        Assert.Equal(0, repository.Disposals);
    }

    private static Agent Create(IModel model, ProbeRepository repository, ReadingPlugin? plugin = null, int maxTurns = 5,
        IReadOnlyList<Message>? messages = null, HookRegistry? hooks = null)
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
        internal int IteratorDisposals { get; private set; }
        internal int Disposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        internal int Disposals { get; private set; }
        public async ValueTask<SessionRecord> CreateSessionAsync(SessionRecord session, CancellationToken cancellationToken = default)
        { Writes.Add("create-session"); return await _inner.CreateSessionAsync(session, cancellationToken); }
        public ValueTask<SessionRecord?> ReadSessionAsync(string id, CancellationToken cancellationToken = default) => _inner.ReadSessionAsync(id, cancellationToken);
        public async ValueTask CreateAgentAsync(string id, SessionAgentRecord agent, CancellationToken cancellationToken = default)
        { Writes.Add("create-agent"); await _inner.CreateAgentAsync(id, agent, cancellationToken); }
        public ValueTask<SessionAgentRecord?> ReadAgentAsync(string id, string agent, CancellationToken cancellationToken = default) => _inner.ReadAgentAsync(id, agent, cancellationToken);
        public async ValueTask UpdateAgentAsync(string id, SessionAgentRecord agent, CancellationToken cancellationToken = default)
        { Writes.Add("update-agent"); await _inner.UpdateAgentAsync(id, agent, cancellationToken); }
        public async ValueTask CreateMessageAsync(string id, string agent, SessionMessageRecord message, CancellationToken cancellationToken = default)
        { Writes.Add("create-message"); await _inner.CreateMessageAsync(id, agent, message, cancellationToken); }
        public ValueTask<SessionMessageRecord?> ReadMessageAsync(string id, string agent, long message, CancellationToken cancellationToken = default) => _inner.ReadMessageAsync(id, agent, message, cancellationToken);
        public async ValueTask UpdateMessageAsync(string id, string agent, SessionMessageRecord message, CancellationToken cancellationToken = default)
        { Writes.Add("update-message"); await _inner.UpdateMessageAsync(id, agent, message, cancellationToken); }
        public ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string id, string agent, int? limit = null, int offset = 0, CancellationToken cancellationToken = default) => _inner.ListMessagesAsync(id, agent, limit, offset, cancellationToken);
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
