using System.Runtime.CompilerServices;
using System.Text.Json;
using Strands.Agents.Context;
using Strands.Agents.Middleware;
using Strands.Agents.Storage;

namespace Strands.Agents.Tests;

public sealed class ReceiptAgentTests
{
    private const string OutputName = "strands_structured_output";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task FreshFileAgentRestoreSkipsConsumedInputAndClearsReceiptAfterAcceptedAssistant()
    {
        using var files = new FileFixture();
        var store = new FileSessionStore(files.Root);
        await store.SaveAsync("receipt-session", Received());
        var model = new ScriptModel(Call("answer", 7)) { FirstStateNumber = 2 };
        await using var agent = Create(model, store);

        var result = await agent.InvokeAsync(null);

        Assert.Equal(7, Value(result));
        var request = Assert.Single(model.Requests);
        Assert.Empty(request.Messages);
        Assert.Equal("state-1", request.ModelState!["responseId"].GetString());
        Assert.Null(request.ToolChoice);
        var latest = Assert.IsType<SessionSnapshot>(await store.LoadAsync("receipt-session"));
        Assert.Equal(1, latest.SchemaVersion);
        Assert.Null(latest.ModelInputReceipt);
        Assert.Equal("state-2", latest.ModelState!["responseId"].GetString());
        Assert.Equal(2, latest.ModelHistoryCount);
        Assert.Equal(1, latest.Messages.Count(message => message.TrackingId == "consumed-u"));
        Assert.Equal(1, model.IteratorDisposals);
    }

    [Fact]
    public async Task RealDiscardedRefusalUpdatesStateWithoutAddingAnotherInputOrResponseRow()
    {
        var store = new RecordingStore();
        var model = new ScriptModel(Text("first"), Text("refused", "refusal"));
        await using var agent = Create(model, store);

        await Assert.ThrowsAsync<StructuredOutputException>(() => agent.InvokeAsync("question"));

        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(model.Requests[1].Messages);
        Assert.Equal("state-1", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Equal(new ToolChoice(ToolChoiceMode.Tool, OutputName), model.Requests[1].ToolChoice);
        var accepted = store.Attempts.Where(snapshot => snapshot.ModelInputReceipt is not null).ToArray();
        Assert.True(accepted.Length >= 2);
        var first = accepted.First();
        var last = accepted.Last();
        Assert.Equal(1, first.ModelInputReceipt!.ConsumedMessageCount);
        Assert.Equal(first.ModelInputReceipt.ConsumedMessageCount, last.ModelInputReceipt!.ConsumedMessageCount);
        Assert.Equal(first.ModelInputReceipt.LastConsumedTrackingId, last.ModelInputReceipt.LastConsumedTrackingId);
        Assert.NotEqual(first.ModelInputReceipt.DiscardedResponseTrackingId, last.ModelInputReceipt.DiscardedResponseTrackingId);
        Assert.Equal("state-2", last.ModelState!["responseId"].GetString());
        Assert.Equal(0, last.ModelHistoryCount);
        Assert.Equal("question", Assert.Single(last.Messages).Text);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "first" || message.Text == "refused");
        Assert.Equal(2, model.IteratorDisposals);
    }

    [Fact]
    public async Task RetriedCompletionAcknowledgesOnlyTheAcceptedProviderAttempt()
    {
        var store = new RecordingStore();
        var hooks = new HookRegistry();
        var attempts = 0;
        using var registration = hooks.Register<AfterModelCallEvent>((value, _) =>
        {
            if (++attempts == 1) value.Retry = true;
            return default;
        });
        SessionSnapshot? beforeForced = null;
        var model = new ScriptModel(Text("retried"), Text("accepted discard"), Call("answer", 7));
        model.BeforeCall = count =>
        {
            if (count == 2) Assert.DoesNotContain(store.Attempts, snapshot => snapshot.ModelInputReceipt is not null);
            if (count == 3) beforeForced = store.Saved.Last();
        };
        await using var agent = Create(model, store, hooks);

        var result = await agent.InvokeAsync("question");

        Assert.Equal(7, Value(result));
        Assert.Equal(3, model.Requests.Count);
        Assert.Equal("question", Assert.Single(model.Requests[1].Messages).Text);
        Assert.Empty(model.Requests[1].ModelState!);
        Assert.Empty(model.Requests[2].Messages);
        Assert.Equal("state-2", model.Requests[2].ModelState!["responseId"].GetString());
        Assert.NotNull(beforeForced);
        Assert.Equal(2, beforeForced.SchemaVersion);
        Assert.Equal(1, beforeForced.ModelInputReceipt!.ConsumedMessageCount);
        Assert.Equal("state-2", beforeForced.ModelState!["responseId"].GetString());
        Assert.DoesNotContain(store.Attempts, snapshot => snapshot.ModelState?.TryGetValue("responseId", out var value) == true && value.GetString() == "state-1");
        Assert.Equal(3, model.IteratorDisposals);
    }

    [Fact]
    public async Task SyntheticCompletionLeavesInputPendingAndDoesNotCreateAReceipt()
    {
        var store = new RecordingStore();
        var middleware = new MiddlewareRegistry();
        var calls = 0;
        middleware.RegisterWrap(MiddlewareStages.InvokeModel, CachedOnce);
        var model = new ScriptModel(Call("real", 7));
        model.BeforeCall = _ => Assert.DoesNotContain(store.Attempts, snapshot => snapshot.ModelInputReceipt is not null);
        await using var agent = new Agent(new()
        {
            Model = model,
            SessionId = "receipt-session",
            SessionStore = store,
            Middleware = middleware,
            StructuredOutput = Format()
        });

        var result = await agent.InvokeAsync("question");

        Assert.Equal(7, Value(result));
        var request = Assert.Single(model.Requests);
        Assert.Equal("question", Assert.Single(request.Messages).Text);
        Assert.Empty(request.ModelState!);
        Assert.DoesNotContain(agent.Messages, message => message.Text == "synthetic");
        Assert.DoesNotContain(store.Attempts, snapshot => snapshot.ModelInputReceipt is not null);

        async IAsyncEnumerable<IMiddlewareEvent<InvokeModelResult>> CachedOnce(InvokeModelContext context,
            MiddlewareNext<InvokeModelContext, InvokeModelResult> next, [EnumeratorCancellation] CancellationToken token)
        {
            if (++calls == 1)
            {
                yield return new MiddlewareResultEvent<InvokeModelResult>(new(Message.Assistant("synthetic"), StopReasons.EndTurn, new()));
                yield break;
            }
            await foreach (var item in next(context).WithCancellation(token)) yield return item;
        }
    }

    [Fact]
    public async Task ProviderCleanupFailureCannotAcknowledgeItsIncompleteAttempt()
    {
        var primary = new IOException("provider cleanup sentinel");
        var store = new RecordingStore();
        var model = new ScriptModel(Text("unaccepted")) { CleanupFailure = primary };
        await using var agent = Create(model, store);

        var failure = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));

        Assert.Same(primary, failure);
        Assert.Equal(1, model.IteratorDisposals);
        Assert.Single(model.Requests);
        Assert.Empty(agent.ModelState.GetAll());
        Assert.Equal("question", Assert.Single(agent.Messages).Text);
        Assert.DoesNotContain(store.Attempts, snapshot => snapshot.ModelInputReceipt is not null || snapshot.ModelState?.Count > 0);
    }

    [Fact]
    public async Task NoInputCompletionUpdatesOnlyStateWithoutInventingAReceipt()
    {
        var store = new RecordingStore();
        var model = new ScriptModel(Text("discarded"));
        await using var agent = new Agent(new()
        {
            Model = model,
            SessionId = "receipt-session",
            SessionStore = store,
            ModelState = ProviderState("seed"),
            StructuredOutput = Format()
        });

        var result = await agent.InvokeAsync(null, new() { MaxTurns = 1 });

        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Null(result.Message);
        Assert.Empty(agent.Messages);
        Assert.Equal("state-1", agent.ModelState.GetAll()["responseId"].GetString());
        Assert.DoesNotContain(store.Attempts, snapshot => snapshot.ModelInputReceipt is not null || snapshot.Messages.Count > 0);
        Assert.Contains(store.Saved, snapshot => snapshot.ModelState!["responseId"].GetString() == "state-1");
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task AssistantEndedConsumptionUsesOnlyTheAlreadyRetainedLegacyBoundary()
    {
        var store = new RecordingStore();
        var model = new ScriptModel(Text("discarded"));
        var prior = new Message(Role.Assistant, [new TextBlock("prior answer")], "prior-a");
        await using var agent = new Agent(new()
        {
            Model = model,
            SessionId = "receipt-session",
            SessionStore = store,
            Messages = [User("prior-u"), prior],
            ModelState = ProviderState("seed"),
            StructuredOutput = Format()
        });

        var result = await agent.InvokeAsync(null, new() { MaxTurns = 1 });

        Assert.Equal(StopReasons.MaxTurns, result.StopReason);
        Assert.Equal("prior-a", result.Message!.TrackingId);
        var accepted = Assert.Single(store.Attempts, snapshot => snapshot.ModelHistoryCount == 2);
        Assert.Equal(new[] { "prior-u", "prior-a" }, accepted.Messages.Select(message => message.TrackingId));
        Assert.Equal("state-1", accepted.ModelState!["responseId"].GetString());
        Assert.Null(accepted.ModelInputReceipt);
        Assert.DoesNotContain(store.Attempts, snapshot => snapshot.ModelInputReceipt is not null || snapshot.Messages.Any(message => message.Text == "discarded"));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task HardCallerCancellationAfterReceiptCommitPreventsTheNextProviderCall()
    {
        using var caller = new CancellationTokenSource();
        var store = new RecordingStore();
        store.AfterSave = snapshot => { if (snapshot.ModelInputReceipt is not null) caller.Cancel(); };
        var model = new ScriptModel(Text("discarded"));
        await using var agent = Create(model, store);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeStructuredAsync("question", Format(), cancellationToken: caller.Token));

        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.Single(model.Requests);
        Assert.Equal(1, model.IteratorDisposals);
        var accepted = store.Saved.Last(snapshot => snapshot.ModelInputReceipt is not null);
        Assert.Equal(1, accepted.ModelInputReceipt!.ConsumedMessageCount);
        Assert.Equal("state-1", accepted.ModelState!["responseId"].GetString());
        Assert.Equal("question", Assert.Single(accepted.Messages).Text);
    }

    [Fact]
    public async Task FailedReceiptSaveRemainsPrimaryAndPersistenceRetryDoesNotReplayInput()
    {
        var primary = new IOException("receipt save sentinel");
        var store = new RecordingStore() { RejectReceiptOnce = primary };
        var model = new ScriptModel(Text("discarded"), Call("answer", 7));
        await using var agent = Create(model, store);

        var failure = await Assert.ThrowsAsync<IOException>(() => agent.InvokeAsync("question"));

        Assert.Same(primary, failure);
        Assert.Single(model.Requests);
        Assert.Equal("state-1", agent.ModelState.GetAll()["responseId"].GetString());
        Assert.Contains(store.Attempts, snapshot => snapshot.ModelInputReceipt is not null);
        var result = await agent.InvokeAsync(null);
        Assert.Equal(7, Value(result));
        Assert.Equal(2, model.Requests.Count);
        Assert.Empty(model.Requests[1].Messages);
        Assert.Equal("state-1", model.Requests[1].ModelState!["responseId"].GetString());
        Assert.Null(store.Saved.Last().ModelInputReceipt);
        Assert.Equal(1, store.Saved.Last().SchemaVersion);
        Assert.Equal(2, model.IteratorDisposals);
    }

    [Fact]
    public async Task PreCancelledCallerDoesNotLoadAReceiptOrOpenAProvider()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var store = new RecordingStore() { Loaded = Received() };
        var model = new ScriptModel(Call("unused", 7));
        await using var agent = Create(model, store);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.InvokeStructuredAsync(null, Format(), cancellationToken: caller.Token));

        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.Equal(0, store.Loads);
        Assert.Empty(store.Attempts);
        Assert.Empty(model.Requests);
    }

    [Theory]
    [InlineData("wrong-boundary-id")]
    [InlineData("missing-provider-state")]
    [InlineData("missing-legacy-cursor")]
    [InlineData("missing-receipt")]
    [InlineData("version-one-receipt")]
    public async Task MalformedReceiptRejectsBeforePluginOrLiveMutation(string kind)
    {
        var valid = Received() with
        {
            State = new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("changed") },
            HasSystemPrompt = true,
            SystemPrompt = "changed",
            AgentId = "changed-agent"
        };
        var malformed = kind switch
        {
            "wrong-boundary-id" => valid with { ModelInputReceipt = valid.ModelInputReceipt! with { LastConsumedTrackingId = "wrong" } },
            "missing-provider-state" => valid with { ModelState = null },
            "missing-legacy-cursor" => valid with { ModelHistoryCount = null },
            "missing-receipt" => valid with { ModelInputReceipt = null },
            "version-one-receipt" => valid with { SchemaVersion = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var plugin = new ProbePlugin();
        var store = new RecordingStore() { Loaded = malformed };
        var model = new ScriptModel(Call("unexpected", 7));
        await using var agent = new Agent(new()
        {
            Id = "seed-agent",
            Model = model,
            SystemPrompt = "seed",
            SessionId = "receipt-session",
            SessionStore = store,
            State = new Dictionary<string, JsonElement> { ["label"] = JsonSerializer.SerializeToElement("seed") },
            ModelState = ProviderState("seed-provider"),
            Messages = [User("seed-u")],
            Plugins = [plugin],
            StructuredOutput = Format()
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => agent.InitializeAsync());

        Assert.Equal("seed-agent", agent.Id);
        Assert.Equal("seed", agent.SystemPrompt);
        Assert.Equal("seed", agent.State.GetAll()["label"].GetString());
        Assert.Equal("seed-provider", agent.ModelState.GetAll()["responseId"].GetString());
        Assert.Equal("seed-u", Assert.Single(agent.Messages).TrackingId);
        Assert.Equal(0, plugin.Initializations);
        Assert.Empty(model.Requests);
        Assert.Empty(store.Attempts);
    }

    private static Agent Create(ScriptModel model, ISessionStore store, HookRegistry? hooks = null) => new(new()
    {
        Model = model,
        SessionId = "receipt-session",
        SessionStore = store,
        StructuredOutput = Format(),
        Hooks = hooks ?? new()
    });

    private static StructuredOutputOptions<JsonElement> Format() => new()
    {
        Behavior = StructuredOutputBehavior.TypeScript,
        Schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { value = new { type = "integer" } }, required = new[] { "value" }, additionalProperties = false })
    };

    private static int Value(AgentResult result) => result.StructuredOutput!.Value.GetProperty("value").GetInt32();
    private static Message User(string id) => new(Role.User, [new TextBlock("question")], id);
    private static Dictionary<string, JsonElement> ProviderState(string id) => new() { ["responseId"] = JsonSerializer.SerializeToElement(id) };
    private static SessionSnapshot Received() => new(2, [User("consumed-u")])
    {
        ModelHistoryCount = 0,
        ModelState = ProviderState("state-1"),
        ModelInputReceipt = new(1, "consumed-u", "discarded")
    };

    private static Turn Text(string text, string reason = StopReasons.EndTurn) => new(reason, [new TextBlock(text)]);
    private static Turn Call(string id, int value) => new(StopReasons.ToolUse, [new ToolUseBlock(id, OutputName, JsonSerializer.SerializeToElement(new { value }))]);
    private sealed record Turn(string Reason, ContentBlock[] Blocks);

    private sealed class ScriptModel(params Turn[] turns) : IStatefulModel
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        internal Action<int>? BeforeCall { get; set; }
        internal Exception? CleanupFailure { get; init; }
        internal int FirstStateNumber { get; init; } = 1;
        internal int IteratorDisposals { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with
            {
                Messages = request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray(),
                Tools = request.Tools?.ToArray(),
                ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            });
            var index = Requests.Count;
            BeforeCall?.Invoke(index);
            if (index > turns.Length) throw new InvalidOperationException("The fake provider must not be reopened for an already consumed input.");
            var turn = turns[index - 1];
            try
            {
                yield return new ModelMessageStartEvent();
                for (var i = 0; i < turn.Blocks.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (turn.Blocks[i] is ToolUseBlock tool)
                    {
                        yield return new ModelContentBlockStartEvent(i, tool);
                        yield return new ModelContentBlockStopEvent(i);
                    }
                    else
                    {
                        yield return new ModelContentBlockStartEvent(i) { ContentBlock = turn.Blocks[i] };
                        yield return new ModelContentBlockStopEvent(i) { ContentBlock = turn.Blocks[i] };
                    }
                }
                yield return new ModelMessageStopEvent(turn.Reason) { ModelState = ProviderState("state-" + (FirstStateNumber + index - 1)) };
                await Task.CompletedTask;
            }
            finally
            {
                IteratorDisposals++;
                if (CleanupFailure is { } failure) throw failure;
            }
        }
    }

    private sealed class RecordingStore : ISessionStore
    {
        internal SessionSnapshot? Loaded { get; init; }
        internal List<SessionSnapshot> Attempts { get; } = [];
        internal List<SessionSnapshot> Saved { get; } = [];
        internal Exception? RejectReceiptOnce { get; set; }
        internal Action<SessionSnapshot>? AfterSave { get; set; }
        internal int Loads { get; private set; }

        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Loads++;
            return new(Loaded);
        }

        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.SerializeToUtf8Bytes(snapshot, Web), Web)!;
            Attempts.Add(copy);
            if (copy.ModelInputReceipt is not null && RejectReceiptOnce is { } failure)
            {
                RejectReceiptOnce = null;
                throw failure;
            }
            Saved.Add(copy);
            AfterSave?.Invoke(copy);
            return default;
        }
    }

    private sealed class ProbePlugin : IPlugin
    {
        public string Name => "test:receipt-init";
        internal int Initializations { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default) { Initializations++; return default; }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class ProbeStorage : IStorage, IStorageCharacteristics
    {
        public bool IsEphemeral => true;
        public bool IsNamespaced => true;
        internal int Calls { get; private set; }
        public ValueTask<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) { Calls++; return new((byte[]?)null); }
        public ValueTask WriteAsync(string key, byte[] data, CancellationToken cancellationToken = default) { Calls++; return default; }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) { Calls++; return default; }
        public ValueTask<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default) { Calls++; return new((IReadOnlyList<string>)Array.Empty<string>()); }
    }

    private sealed class FileFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "strands-receipt-agent-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.GetFiles(Root)) File.Delete(file);
            Directory.Delete(Root);
        }
    }
}
