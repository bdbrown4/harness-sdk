using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.SessionCatalogs;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class ReceiptCatalogTests
{
    private const string Session = "receipt-session";
    private const string AgentId = "catalog-agent";
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static string LatestKey => Session + "/scopes/agent/" + AgentId + "/snapshots/snapshot_latest.json";

    [Fact]
    public async Task AutomaticLatestKeepsConsumedInputAndClearsReceiptAfterAcceptedAssistant()
    {
        using var files = new FileFixture();
        await using var writerStorage = files.Open();
        var firstModel = new ScriptStatefulModel(Text("ignored", "state-1"));
        {
            var manager = new SnapshotSessionManager(writerStorage, Session, SnapshotSessionSaveStrategy.Message);
            await using var writer = Create(firstModel, manager, maxTurns: 1);
            var result = await writer.InvokeAsync("question");
            Assert.Equal(StopReasons.MaxTurns, result.StopReason);
            var latest = await ReadLatest(writerStorage);
            Assert.Equal(2, latest.SchemaVersion);
            AssertReceipt(latest, "state-1");
            Assert.Equal("question", Assert.Single(latest.Messages).Text);
            Assert.DoesNotContain(latest.Messages, message => message.Text == "ignored");
        }
        Assert.Equal(0, firstModel.Disposals);
        Assert.Equal(1, firstModel.IteratorDisposals);
        Assert.Equal(0, writerStorage.Disposals);
        await writerStorage.WriteAsync("application-owned", [1, 2, 3]);

        await using var readerStorage = files.Open();
        var secondModel = new ScriptStatefulModel(Call("answer", 7, "state-2"));
        var observer = new ReadingPlugin();
        var reopened = new SnapshotSessionManager(readerStorage, Session, SnapshotSessionSaveStrategy.Message);
        {
            await using var reader = Create(secondModel, reopened, observer);
            var result = await reader.InvokeAsync(null);
            Assert.Equal(7, Value(result));
            var request = Assert.Single(secondModel.Requests);
            Assert.Empty(request.Messages);
            Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
            Assert.Equal(1, observer.Calls);
            Assert.Equal("question", Assert.Single(observer.Messages!).Text);
            Assert.Equal("state-1", observer.ResponseId);
            var latest = await ReadLatest(readerStorage);
            Assert.Equal(1, latest.SchemaVersion);
            Assert.Null(latest.ModelInputReceipt);
            Assert.Equal("state-2", latest.ModelState!["responseId"].GetString());
            Assert.Equal(2, latest.ModelHistoryCount);
            Assert.Equal(1, latest.Messages.Count(message => message.Text == "question"));
        }
        Assert.Equal(0, secondModel.Disposals);
        Assert.Equal(1, secondModel.IteratorDisposals);
        Assert.Equal(0, readerStorage.Disposals);
        Assert.Equal(new byte[] { 1, 2, 3 }, await readerStorage.ReadAsync("application-owned"));
    }

    [Fact]
    public async Task ManualImmutableAndLatestRoundTripTheSameReceiptPair()
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        var model = new ScriptStatefulModel(Call("external", 8, "state-2"), Call("rewound", 9, "state-3"), Call("latest", 10, "state-4"));
        var plugin = new ReadingPlugin();
        var manager = new SnapshotSessionManager(storage, Session, SnapshotSessionSaveStrategy.Trigger);
        await using var agent = Create(model, manager, plugin);
        await agent.InitializeAsync();
        await agent.LoadSnapshotAsync(PublicReceipt());

        var id = Assert.IsType<string>(await manager.SaveSnapshotAsync(agent, false));
        Assert.Null(await manager.SaveSnapshotAsync(agent, true));
        var latest = await ReadLatest(storage);
        Assert.Equal(2, latest.SchemaVersion);
        AssertReceipt(latest, "state-1");
        var immutableKey = Session + "/scopes/agent/" + AgentId + "/snapshots/immutable_history/snapshot_" + id + ".json";
        using var immutableJson = JsonDocument.Parse((await storage.ReadAsync(immutableKey))!);
        Assert.Equal(1, immutableJson.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(id, immutableJson.RootElement.GetProperty("snapshotId").GetString());
        var immutable = SnapshotSessionCodec.Read((await storage.ReadAsync(immutableKey))!, Session, AgentId, id, true);
        Assert.Equal(latest.ModelInputReceipt, immutable.ModelInputReceipt);
        Assert.Equal(latest.Messages.Select(message => message.TrackingId), immutable.Messages.Select(message => message.TrackingId));
        Assert.Equal(latest.ModelState!["responseId"].GetString(), immutable.ModelState!["responseId"].GetString());

        Assert.Equal(8, Value(await agent.InvokeAsync("external")));
        agent.State.Set("label", "changed");
        Assert.True(await manager.RestoreSnapshotAsync(agent, id));
        Assert.Equal("saved", agent.State.Get<string>("label"));
        Assert.Equal("saved-prompt", agent.SystemPrompt);
        Assert.Equal(AgentId, agent.Id);
        Assert.Equal(9, Value(await agent.InvokeAsync(null)));
        Assert.Empty(model.Requests[1].Messages);
        Assert.Equal("state-1", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.True(await manager.RestoreSnapshotAsync(agent));
        Assert.Equal(10, Value(await agent.InvokeAsync(null)));
        Assert.Empty(model.Requests[2].Messages);
        Assert.Equal("state-1", model.Requests[2].ModelState!["responseId"].GetString());
        Assert.Equal(1, plugin.Calls);
        Assert.Equal(3, model.IteratorDisposals);
        Assert.Equal(0, model.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCatalogRecordsRemainVersionOne(bool stateful)
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        var snapshot = new SessionSnapshot(1, [new Message(Role.User, [new TextBlock("saved-u")], "saved-u"), new Message(Role.Assistant, [new TextBlock("saved-a")], "saved-a")])
        {
            AgentId = AgentId,
            State = Values("label", "saved"),
            ModelState = Values("responseId", "state-1"),
            HasSystemPrompt = true,
            SystemPrompt = "saved-prompt",
            ModelHistoryCount = stateful ? 2 : null
        };
        var bytes = SnapshotSessionCodec.Write(snapshot, Session, AgentId, null, stateful);
        using (var encoded = JsonDocument.Parse(bytes))
        {
            Assert.Equal(1, encoded.RootElement.GetProperty("version").GetInt32());
            Assert.False(encoded.RootElement.GetProperty("snapshot").TryGetProperty("modelInputReceipt", out _));
        }
        await storage.WriteAsync(LatestKey, bytes);
        storage.Writes.Clear();
        var manager = new SnapshotSessionManager(storage, Session);
        var plugin = new ReadingPlugin();
        var model = new ScriptStatefulModel();
        await using var agent = Create(stateful ? model : new UnusedModel(), manager, plugin);
        await agent.InitializeAsync();
        Assert.Equal(new[] { "saved-u", "saved-a" }, agent.Messages.Select(message => message.Text));
        Assert.Equal("saved", agent.State.Get<string>("label"));
        Assert.Equal("state-1", agent.ModelState.Get<string>("responseId"));
        Assert.Equal("saved-prompt", agent.SystemPrompt);
        Assert.Equal(1, plugin.Calls);
        Assert.Empty(model.Requests);
        Assert.Empty(storage.Writes);
        Assert.Equal(bytes, await storage.ReadAsync(LatestKey));
    }

    [Theory]
    [InlineData("inner1-receipt")]
    [InlineData("inner2-missing-receipt")]
    [InlineData("inner2-wrong-boundary")]
    [InlineData("inner2-empty-state")]
    [InlineData("inner2-stateless")]
    [InlineData("envelope-owner")]
    [InlineData("envelope-profile")]
    public async Task CatalogCodecAndRestoreRejectInvalidReceiptMetadataBeforePublication(string mutation)
    {
        using var files = new FileFixture();
        await using var storage = files.Open();
        var snapshot = Receipt();
        var stateful = mutation != "inner2-stateless";
        switch (mutation)
        {
            case "inner1-receipt": snapshot = snapshot with { SchemaVersion = 1 }; break;
            case "inner2-missing-receipt": snapshot = snapshot with { ModelInputReceipt = null }; break;
            case "inner2-wrong-boundary": snapshot = snapshot with { ModelInputReceipt = snapshot.ModelInputReceipt! with { LastConsumedTrackingId = "wrong" } }; break;
            case "inner2-empty-state": snapshot = snapshot with { ModelState = new Dictionary<string, JsonElement>() }; break;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            SessionId = Session,
            AgentId = mutation == "envelope-owner" ? "someone-else" : AgentId,
            SnapshotId = (string?)null,
            Stateful = mutation == "envelope-profile" ? false : stateful,
            Snapshot = snapshot
        }, Web);
        // The codec itself is a safety boundary, independently of the Agent's later prepared restore.
        Assert.Throws<InvalidDataException>(() => SnapshotSessionCodec.Read(bytes, Session, AgentId, null, stateful));
        if (!mutation.StartsWith("envelope-", StringComparison.Ordinal))
            Assert.Throws<InvalidDataException>(() => SnapshotSessionCodec.Write(snapshot, Session, AgentId, null, stateful));

        await storage.WriteAsync(LatestKey, bytes);
        storage.Writes.Clear();
        var plugin = new ReadingPlugin();
        var model = new ScriptStatefulModel();
        var manager = new SnapshotSessionManager(storage, Session);
        await using var agent = Create(stateful ? model : new UnusedModel(), manager, plugin,
            messages: [new Message(Role.User, [new TextBlock("configured")], "configured-u")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());
        Assert.Equal("configured", Assert.Single(agent.Messages).Text);
        Assert.Equal("configured", agent.SystemPrompt);
        Assert.Equal("configured", agent.State.Get<string>("label"));
        Assert.Equal("configured", agent.ModelState.Get<string>("responseId"));
        Assert.Equal(0, plugin.Calls);
        Assert.Empty(model.Requests);
        Assert.Empty(storage.Writes);
        Assert.Equal(bytes, await storage.ReadAsync(LatestKey));
        Assert.Equal(0, storage.Disposals);
    }

    private static Agent Create(IModel model, SnapshotSessionManager manager, ReadingPlugin? plugin = null,
        int maxTurns = 5, IReadOnlyList<Message>? messages = null) => new(new()
        {
            Id = AgentId,
            Model = model,
            MaxTurns = maxTurns,
            SystemPrompt = "configured",
            State = Values("label", "configured"),
            ModelState = Values("responseId", "configured"),
            Messages = messages ?? [],
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
        internal int Disposals { get; private set; }
        public async ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(key, data, cancellationToken); Writes.Add(key); }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
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
