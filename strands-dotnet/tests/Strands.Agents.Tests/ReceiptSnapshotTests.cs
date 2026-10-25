using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Strands.Agents.Tests;

public sealed class ReceiptSnapshotTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CompleteCaptureRoundTripsItsConsumedInputPairIntoAFreshAgent()
    {
        await using var source = WithReceipt(new ScriptModel());
        await source.InitializeAsync();

        var captured = await source.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });

        Assert.Equal("2.0", captured.SchemaVersion);
        var receipt = captured.Data["modelInputReceipt"].Deserialize<ModelInputReceipt>(Web)!;
        Assert.Equal(new ModelInputReceipt(1, "consumed-u", "discarded"), receipt);
        Assert.Equal(0, captured.Data["modelHistoryCount"].GetInt32());
        Assert.Equal("state-1", captured.Data["modelState"].GetProperty("responseId").GetString());
        var copy = JsonSerializer.Deserialize<AgentSnapshot>(JsonSerializer.SerializeToUtf8Bytes(captured, Web), Web)!;
        var model = new ScriptModel();
        await using var restored = new Agent(new() { Model = model, StructuredOutput = Format() });
        await restored.LoadSnapshotAsync(copy);

        var result = await restored.InvokeAsync(null);

        Assert.Equal(7, result.StructuredOutput!.Value.GetProperty("value").GetInt32());
        var request = Assert.Single(model.Requests);
        Assert.Empty(request.Messages);
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        Assert.Null(request.ToolChoice);
        Assert.Equal(1, model.IteratorDisposals);
        var settled = await restored.TakeSnapshotAsync(new() { Preset = SnapshotPreset.Session });
        Assert.Equal("1.0", settled.SchemaVersion);
        Assert.False(settled.Data.ContainsKey("modelInputReceipt"));
        Assert.Equal(2, settled.Data["modelHistoryCount"].GetInt32());
    }

    [Fact]
    public async Task ExplicitValidVersionTwoCheckpointRestoresItsConsumedInputWithoutReplay()
    {
        var checkpoint = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { User("consumed-u", "consumed input") }, Web),
            ["modelState"] = JsonSerializer.SerializeToElement(ProviderState("state-1")),
            ["modelHistoryCount"] = JsonSerializer.SerializeToElement(0),
            ["modelInputReceipt"] = JsonSerializer.SerializeToElement(new ModelInputReceipt(1, "consumed-u", "discarded"), Web)
        }, schemaVersion: "2.0");
        var model = new ScriptModel();
        await using var agent = new Agent(new() { Model = model, StructuredOutput = Format() });

        await agent.LoadSnapshotAsync(checkpoint);

        Assert.Equal("consumed-u", Assert.Single(agent.Messages).TrackingId);
        Assert.Equal("state-1", agent.ModelState.GetAll()["responseId"].GetString());
        await agent.InvokeAsync(null);
        var request = Assert.Single(model.Requests);
        Assert.Empty(request.Messages);
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        Assert.Null(request.ToolChoice);
    }

    [Theory]
    [InlineData(SnapshotFields.Messages)]
    [InlineData(SnapshotFields.ModelState)]
    public async Task ActiveReceiptCaptureRejectsOnlyOneHalfOfItsProviderHistoryPair(SnapshotFields fields)
    {
        var model = new ScriptModel();
        await using var agent = WithReceipt(model);
        await agent.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await agent.TakeSnapshotAsync(new() { Include = fields }));

        Assert.Equal("consumed-u", Assert.Single(agent.Messages).TrackingId);
        Assert.Equal("state-1", agent.ModelState.GetAll()["responseId"].GetString());
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task UnrelatedStateOnlyCaptureKeepsItsExistingVersionOneShape()
    {
        var model = new ScriptModel();
        await using var agent = WithReceipt(model);
        await agent.InitializeAsync();

        var captured = await agent.TakeSnapshotAsync(new() { Include = SnapshotFields.State });

        Assert.Equal("1.0", captured.SchemaVersion);
        Assert.Equal("seed", captured.Data["state"].GetProperty("label").GetString());
        Assert.Single(captured.Data);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderStateOnlyReplacementRequiresAnUnchangedActiveReceiptPair(bool changed)
    {
        var model = new ScriptModel();
        await using var agent = WithReceipt(model);
        await agent.InitializeAsync();
        var replacement = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["modelState"] = JsonSerializer.SerializeToElement(ProviderState(changed ? "different" : "state-1"))
        });

        if (changed)
            await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(replacement));
        else
            await agent.LoadSnapshotAsync(replacement);

        Assert.Equal("state-1", agent.ModelState.GetAll()["responseId"].GetString());
        await agent.InvokeAsync(null);
        var request = Assert.Single(model.Requests);
        Assert.Empty(request.Messages);
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
    }

    [Fact]
    public async Task CompleteVersionOneReplacementClearsThePreviousInputReceipt()
    {
        var model = new ScriptModel();
        await using var agent = WithReceipt(model);
        await agent.InitializeAsync();
        var replacement = new AgentSnapshot(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { User("replacement-u", "new input") }, Web),
            ["modelState"] = JsonSerializer.SerializeToElement(ProviderState("replacement-state")),
            ["modelHistoryCount"] = JsonSerializer.SerializeToElement(0)
        });

        await agent.LoadSnapshotAsync(replacement);
        await agent.InvokeAsync(null);

        var request = Assert.Single(model.Requests);
        Assert.Equal("replacement-u", Assert.Single(request.Messages).TrackingId);
        Assert.Equal("replacement-state", request.ModelState!["responseId"].GetString());
    }

    [Theory]
    [InlineData("missing-messages")]
    [InlineData("missing-provider-state")]
    [InlineData("missing-cursor")]
    [InlineData("missing-receipt")]
    [InlineData("wrong-boundary-id")]
    [InlineData("version-one-receipt")]
    public async Task MalformedReceiptCheckpointRejectsBeforeInitializationOrLiveMutation(string kind)
    {
        var data = new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(new[] { User("consumed-u", "consumed input") }, Web),
            ["modelState"] = JsonSerializer.SerializeToElement(ProviderState("state-1")),
            ["modelHistoryCount"] = JsonSerializer.SerializeToElement(0),
            ["modelInputReceipt"] = JsonSerializer.SerializeToElement(new ModelInputReceipt(1, "consumed-u", "discarded"), Web),
            ["state"] = JsonSerializer.SerializeToElement(new { label = "changed" }),
            ["systemPrompt"] = JsonSerializer.SerializeToElement("changed")
        };
        switch (kind)
        {
            case "missing-messages": data.Remove("messages"); break;
            case "missing-provider-state": data.Remove("modelState"); break;
            case "missing-cursor": data.Remove("modelHistoryCount"); break;
            case "missing-receipt": data.Remove("modelInputReceipt"); break;
            case "wrong-boundary-id": data["modelInputReceipt"] = JsonSerializer.SerializeToElement(new ModelInputReceipt(1, "wrong", "discarded"), Web); break;
            case "version-one-receipt": break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var checkpoint = new AgentSnapshot(data, schemaVersion: kind == "version-one-receipt" ? "1.0" : "2.0");
        var model = new ScriptModel();
        var plugin = new ProbePlugin();
        await using var agent = new Agent(new()
        {
            Id = "seed-agent",
            Model = model,
            SystemPrompt = "seed-prompt",
            Messages = [User("seed-u", "seed input")],
            State = new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("seed") },
            ModelState = ProviderState("seed-state"),
            Plugins = [plugin]
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () => await agent.LoadSnapshotAsync(checkpoint));

        Assert.Equal(0, plugin.Initializations);
        Assert.Equal("seed-agent", agent.Id);
        Assert.Equal("seed-prompt", agent.SystemPrompt);
        Assert.Equal("seed", agent.State.GetAll()["label"].GetString());
        Assert.Equal("seed-state", agent.ModelState.GetAll()["responseId"].GetString());
        Assert.Equal("seed-u", Assert.Single(agent.Messages).TrackingId);
        Assert.Empty(model.Requests);
    }

    private static Agent WithReceipt(ScriptModel model) => new(new()
    {
        Model = model,
        SessionId = "receipt",
        SessionStore = new LoadedStore(),
        StructuredOutput = Format()
    });

    private static StructuredOutputOptions<JsonElement> Format() => new()
    {
        Behavior = StructuredOutputBehavior.TypeScript,
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
    };

    private static Message User(string id, string text) => new(Role.User, [new TextBlock(text)], id);
    private static Dictionary<string, JsonElement> ProviderState(string id) => new() { ["responseId"] = JsonSerializer.SerializeToElement(id) };

    private sealed class LoadedStore : ISessionStore
    {
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(new SessionSnapshot(2, [User("consumed-u", "consumed input")])
            {
                ModelHistoryCount = 0,
                ModelInputReceipt = new(1, "consumed-u", "discarded"),
                ModelState = ProviderState("state-1"),
                State = new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("seed") }
            });
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return default; }
    }

    private sealed class ProbePlugin : IPlugin
    {
        public string Name => "test:receipt-snapshot-init";
        internal int Initializations { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Initializations++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class ScriptModel : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        internal int IteratorDisposals { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with { Messages = request.Messages.ToArray(), ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()) });
            if (Requests.Count > 1) throw new InvalidOperationException("A completed structured response must not open a second provider call.");
            try
            {
                yield return new ModelMessageStartEvent();
                yield return new ModelContentBlockStartEvent(0, new ToolUseBlock("answer", "strands_structured_output", JsonSerializer.SerializeToElement(new { value = 7 })));
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(StopReasons.ToolUse) { ModelState = ProviderState("state-2") };
                await Task.CompletedTask;
            }
            finally { IteratorDisposals++; }
        }
    }
}
