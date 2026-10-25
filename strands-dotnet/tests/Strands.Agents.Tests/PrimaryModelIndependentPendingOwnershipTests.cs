using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Strands.Agents.Goals;
using Strands.Agents.SessionRepositories;
using Strands.Agents.Storage;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelIndependentPendingOwnershipTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MarkerDefaultsRecordCopiesAndPublicJsonRetainTheirExplicitOwnership()
    {
        var paired = Pending(false);
        var independent = paired with { AssistantMessageIsIndependent = true };
        var copy = independent with { };
        Assert.False(paired.AssistantMessageIsIndependent);
        Assert.Equal(independent, copy);
        Assert.Equal(independent.GetHashCode(), copy.GetHashCode());
        Assert.NotEqual(paired, independent);
        Assert.Equal(paired, independent with { AssistantMessageIsIndependent = false });
        Assert.Contains("AssistantMessageIsIndependent = True", independent.ToString());
        foreach (var options in new[] { new JsonSerializerOptions(), Web })
        {
            var field = options.PropertyNamingPolicy?.ConvertName(nameof(InterruptState.AssistantMessageIsIndependent))
                ?? nameof(InterruptState.AssistantMessageIsIndependent);
            var ordinary = JsonSerializer.SerializeToElement(paired, options);
            Assert.False(ordinary.TryGetProperty(field, out _));
            var wire = JsonSerializer.Serialize(independent, options);
            InterruptState restored;
            using (var document = JsonDocument.Parse(wire))
            {
                Assert.True(document.RootElement.GetProperty(field).GetBoolean());
                restored = Assert.IsType<InterruptState>(document.RootElement.Deserialize<InterruptState>(options));
            }
            Assert.True(restored.AssistantMessageIsIndependent);
            Assert.Equal("saved-question", Assert.Single(restored.Interrupts).Id);
            Assert.Equal("committed", Assert.Single(restored.CompletedToolResults).Content);
            Assert.Equal(7, restored.AssistantMessage.Content.OfType<ToolUseBlock>().First().Input.GetProperty("value").GetInt32());
        }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task ManualSnapshotLoadAdmitsExplicitIndependentOwnershipWithoutRunningWork(ModelHistoryBehavior profile)
    {
        var model = new ScriptedModel();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = new Agent(new() { Id = "manual-target", Model = model, ModelHistoryBehavior = profile });
            var snapshot = ManualSnapshot(Pending(true));
            await target.LoadSnapshotAsync(snapshot);
            Assert.Empty(target.Messages);
            var first = Assert.IsType<InterruptState>(target.InterruptState);
            var second = Assert.IsType<InterruptState>(target.InterruptState);
            Assert.NotSame(first, second);
            Assert.True(first.AssistantMessageIsIndependent);
            Assert.Equal("saved-question", Assert.Single(first.Interrupts).Id);
            Assert.Equal("committed", Assert.Single(first.CompletedToolResults).Content);
            Assert.True(target.State.Get<bool>("imported"));
            AssertState(target.ModelState.GetAll());
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            Assert.Equal("manual-target", target.Id);
            await target.DisposeAsync(); target = null;
            Assert.Equal(0, model.Disposals);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Fact]
    public async Task NativeManualAdmissionRejectsTheMarkerEvenWhenAssistantHistoryMatches()
    {
        var model = new ScriptedModel();
        var initialized = 0;
        var hooks = new HookRegistry();
        hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = new Agent(new()
            {
                Id = "native-local",
                Model = model,
                Hooks = hooks,
                State = new Dictionary<string, JsonElement> { ["local"] = Json("true") },
                ModelState = new Dictionary<string, JsonElement> { ["localProvider"] = Json("17") }
            });
            var pending = Pending(true);
            var snapshot = ManualSnapshot(pending, [pending.AssistantMessage]);
            await Assert.ThrowsAsync<InvalidDataException>(() => target.LoadSnapshotAsync(snapshot).AsTask());
            Assert.Equal(0, initialized);
            Assert.Equal("native-local", target.Id);
            Assert.True(target.State.Get<bool>("local"));
            Assert.False(target.State.GetAll().ContainsKey("imported"));
            Assert.Equal(17, Assert.Single(target.ModelState.GetAll()).Value.GetInt32());
            Assert.Null(target.InterruptState);
            Assert.Empty(target.Messages);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Fact]
    public async Task FileStoreRoundTripsTheExplicitPendingDomainWithoutChoosingAnAgentProfile()
    {
        var files = new OwnedFiles();
        Exception? primary = null;
        try
        {
            var snapshot = Session(Pending(true));
            var writer = new FileSessionStore(files.Path);
            await writer.SaveAsync("independent", snapshot);
            var restored = Assert.IsType<SessionSnapshot>(await new FileSessionStore(files.Path).LoadAsync("independent"));
            Assert.Empty(restored.Messages);
            Assert.True(Assert.IsType<InterruptState>(restored.InterruptState).AssistantMessageIsIndependent);
            Assert.Equal("saved-question", Assert.Single(restored.InterruptState!.Interrupts).Id);
            AssertState(restored.ModelState!);
            Assert.Null(restored.ModelHistoryCount);
            Assert.Null(restored.ModelInputReceipt);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(snapshot, Web), JsonSerializer.SerializeToElement(restored, Web)));
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, files); }
    }

    [Fact]
    public async Task CatalogReadsExplicitPendingOwnershipFromItsExistingEnvelopeWithoutCursorMigration()
    {
        var storage = new InMemoryStorage();
        var producerModel = new ScriptedModel(stateful: false, pause: true);
        var targetModel = new ScriptedModel(stateful: false);
        var effects = new Effects();
        Agent? producer = null, target = null;
        Exception? primary = null;
        try
        {
            var manager = new SnapshotSessionManager(storage, "pending-catalog");
            producer = new Agent(new()
            {
                Id = "catalog-agent",
                Model = producerModel,
                Plugins = [manager],
                Tools = Tools(effects),
                ToolExecution = ToolExecutionStrategy.Sequential
            });
            Assert.Equal(StopReasons.Interrupt, (await producer.InvokeAsync("task")).StopReason);
            Assert.Null(await manager.SaveSnapshotAsync(producer, true));
            await producer.DisposeAsync(); producer = null;
            var scoped = storage.Namespace("session");
            const string key = "pending-catalog/scopes/agent/catalog-agent/snapshots/snapshot_latest.json";
            var bytes = Assert.IsType<byte[]>(await scoped.ReadAsync(key));
            var envelope = Assert.IsType<Dictionary<string, JsonElement>>(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bytes, Web));
            var saved = Assert.IsType<SessionSnapshot>(envelope["snapshot"].Deserialize<SessionSnapshot>(Web));
            Assert.False(envelope["stateful"].GetBoolean());
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            var paired = Assert.IsType<InterruptState>(saved.InterruptState);
            Assert.Equal(paired.AssistantMessage.TrackingId, saved.Messages.Last().TrackingId);
            envelope["snapshot"] = JsonSerializer.SerializeToElement(saved with
            {
                Messages = Array.Empty<Message>(),
                InterruptState = paired with { AssistantMessageIsIndependent = true }
            }, Web);
            await scoped.WriteAsync(key, JsonSerializer.SerializeToUtf8Bytes(envelope, Web));
            target = new Agent(new()
            {
                Id = "catalog-agent",
                Model = targetModel,
                ModelHistoryBehavior = ModelHistoryBehavior.Python,
                Plugins = [new SnapshotSessionManager(storage, "pending-catalog")]
            });
            await target.InitializeAsync();
            Assert.Empty(target.Messages);
            Assert.True(Assert.IsType<InterruptState>(target.InterruptState).AssistantMessageIsIndependent);
            Assert.Equal(Assert.Single(paired.Interrupts).Id, Assert.Single(target.InterruptState!.Interrupts).Id);
            Assert.Equal(0, targetModel.StreamCalls);
            Assert.Equal(0, targetModel.CountCalls);
            Assert.Equal(1, effects.Done);
            Assert.Equal(1, effects.Approve);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, producer, targetModel, producerModel); }
    }

    [Fact]
    public async Task PythonCompletedResultOnlySnapshotHasAnExplicitPersistenceDecisionBoundary()
    {
        var model = new ScriptedModel();
        var store = new CaptureStore(Session(Pending(true)));
        var effects = new Effects();
        var files = new OwnedFiles();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = CreateSource(ModelHistoryBehavior.Python, model, store, effects);
            var result = await target.InvokeAsync(null, Answer());
            Assert.Equal(StopReasons.EndTurn, result.StopReason);
            var completed = Assert.IsType<SessionSnapshot>(store.Writes.LastOrDefault(snapshot => snapshot.InterruptState is null && snapshot.Messages.Count == 1 && snapshot.Messages[0].Content.OfType<ToolResultBlock>().Any()));
            AssertResults(Assert.Single(completed.Messages), "approved", ToolResultStatus.Success);
            Assert.Null(completed.ModelHistoryCount);
            Assert.Null(completed.ModelInputReceipt);
            AssertState(completed.ModelState!);
            Assert.Equal(0, effects.Done);
            Assert.Equal(1, effects.Approve);
            Assert.Equal(1, model.StreamCalls);
            var request = Assert.Single(model.Requests);
            AssertResults(Assert.Single(request), "approved", ToolResultStatus.Success);
            await new FileSessionStore(files.Path).SaveAsync("python-result-projection", completed);
            var reopened = Assert.IsType<SessionSnapshot>(await new FileSessionStore(files.Path).LoadAsync("python-result-projection"));
            Assert.Null(reopened.InterruptState);
            AssertResults(Assert.Single(reopened.Messages), "approved", ToolResultStatus.Success);
            AssertState(reopened.ModelState!);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model, files); }
    }

    [Fact]
    public async Task AcceptedIndependentAnswerSurvivesCallerCancellationBeforeTheToolBody()
    {
        var store = new CaptureStore(Session(Pending(true)));
        var firstModel = new ScriptedModel();
        var secondModel = new ScriptedModel();
        var effects = new Effects();
        using var caller = new CancellationTokenSource();
        var hooks = new HookRegistry();
        hooks.Register<BeforeInvocationEvent>((_, _) => { caller.Cancel(); return default; });
        Agent? first = null, second = null;
        Exception? primary = null;
        try
        {
            first = CreateSource(ModelHistoryBehavior.Python, firstModel, store, effects, hooks);
            var cancelled = await first.InvokeAsync(null, Answer(), caller.Token);
            Assert.Equal(StopReasons.Cancelled, cancelled.StopReason);
            Assert.True(caller.IsCancellationRequested);
            Assert.Equal(0, firstModel.StreamCalls);
            Assert.Equal(0, effects.Done);
            Assert.Equal(0, effects.Approve);
            var saved = Assert.IsType<InterruptState>(store.Saved!.InterruptState);
            Assert.True(saved.AssistantMessageIsIndependent);
            Assert.Equal("saved-question", Assert.Single(saved.Responses).InterruptId);
            Assert.True(saved.Responses[0].Response.GetBoolean());
            await first.DisposeAsync(); first = null;
            second = CreateSource(ModelHistoryBehavior.Python, secondModel, store, effects);
            var completed = await second.InvokeAsync(null);
            Assert.Equal(StopReasons.EndTurn, completed.StopReason);
            Assert.Equal(0, effects.Done);
            Assert.Equal(1, effects.Approve);
            Assert.Equal(1, secondModel.StreamCalls);
            Assert.Null(second.InterruptState);
            AssertState(second.ModelState.GetAll());
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, second, first, secondModel, firstModel); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript, true)]
    [InlineData(ModelHistoryBehavior.Python, false)]
    public async Task IndependentAbortSettlesWithoutBodiesOrProviderAndUsesTheOwnersProjection(ModelHistoryBehavior profile, bool appendAssistant)
    {
        var model = new ScriptedModel();
        var store = new CaptureStore(Session(Pending(true)));
        var effects = new Effects();
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = CreateSource(profile, model, store, effects);
            await target.InitializeAsync();
            Assert.True(await target.AbortInterruptsAsync("declined"));
            Assert.Null(target.InterruptState);
            Assert.Equal(0, effects.Done);
            Assert.Equal(0, effects.Approve);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            AssertState(target.ModelState.GetAll());
            var history = target.Messages;
            Assert.Equal(appendAssistant ? 2 : 1, history.Count);
            if (appendAssistant) Assert.Equal("saved-assistant", history[0].TrackingId);
            AssertResults(history.Last(), "declined", ToolResultStatus.Error);
            var persisted = Assert.IsType<SessionSnapshot>(store.Saved);
            Assert.Null(persisted.InterruptState);
            Assert.Equal(history.Count, persisted.Messages.Count);
            AssertResults(persisted.Messages.Last(), "declined", ToolResultStatus.Error);
            Assert.False(await target.AbortInterruptsAsync("already settled"));
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Fact]
    public async Task GoalLoopDoesNotJudgeOrRewindAnUnansweredIndependentPause()
    {
        var model = new ScriptedModel();
        var store = new CaptureStore(Session(Pending(true)));
        var effects = new Effects();
        var judged = 0;
        var goal = new GoalLoop((_, _, _) => { judged++; return new ValueTask<ValidationOutcome>(new ValidationOutcome(false)); }, new() { MaxAttempts = 2, PreserveContext = false });
        Agent? target = null;
        Exception? primary = null;
        try
        {
            target = CreateSource(ModelHistoryBehavior.Python, model, store, effects, plugins: [goal]);
            var paused = await target.InvokeAsync(null);
            Assert.Equal(StopReasons.Interrupt, paused.StopReason);
            Assert.Equal("saved-question", Assert.Single(paused.Interrupts).Id);
            Assert.True(Assert.IsType<InterruptState>(target.InterruptState).AssistantMessageIsIndependent);
            Assert.Equal(0, judged);
            Assert.Null(goal.LastResult(target));
            Assert.Equal(0, effects.Done);
            Assert.Equal(1, effects.Approve);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            AssertState(target.ModelState.GetAll());
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeOwned(primary, target, model); }
    }

    [Fact]
    public async Task SourceJournalAdmissionStaysBeforeBindingOrRepositoryCallbacks()
    {
        var model = new ScriptedModel();
        var repository = new RejectingRepository();
        try
        {
            Assert.Throws<ArgumentException>(() => new Agent(new()
            {
                Model = model,
                ModelHistoryBehavior = ModelHistoryBehavior.Python,
                Plugins = [new RepositorySessionManager("source-journal", repository)]
            }));
            Assert.Equal(0, repository.Calls);
            Assert.Equal(0, model.StreamCalls);
            Assert.Equal(0, model.CountCalls);
            Assert.Equal(0, model.Disposals);
        }
        finally { await model.DisposeAsync(); }
    }

    private static InterruptState Pending(bool independent)
        => new(new Message(Role.Assistant,
            [new ToolUseBlock("call-0", "done", Json("{\"value\":7}")), new ToolUseBlock("call-1", "approve", Json("{}"))], "saved-assistant"),
            [new("call-0", "committed")], [new("saved-question", "approval", "call-1", source: InterruptSource.Tool)], [])
        { AssistantMessageIsIndependent = independent };
    private static SessionSnapshot Session(InterruptState pending) => new(1, Array.Empty<Message>())
    {
        AgentId = "stored-agent",
        State = new Dictionary<string, JsonElement> { ["imported"] = Json("true") },
        ModelState = ProviderState(),
        HasSystemPrompt = true,
        SystemPrompt = "stored prompt",
        InterruptState = pending
    };
    private static AgentSnapshot ManualSnapshot(InterruptState pending, IReadOnlyList<Message>? messages = null)
        => new(new Dictionary<string, JsonElement>
        {
            ["messages"] = JsonSerializer.SerializeToElement(messages ?? Array.Empty<Message>(), Web),
            ["interruptState"] = JsonSerializer.SerializeToElement(pending, Web),
            ["state"] = JsonSerializer.SerializeToElement(new { imported = true }, Web),
            ["modelState"] = JsonSerializer.SerializeToElement(ProviderState(), Web)
        });
    private static Dictionary<string, JsonElement> ProviderState() => new() { ["responseId"] = Json("\"S1\"") };
    private static void AssertState(IReadOnlyDictionary<string, JsonElement> state) => Assert.Equal("S1", Assert.Single(state).Value.GetString());
    private static InvocationOptions Answer() => new() { InterruptResponses = [new("saved-question", Json("true"))] };
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static IReadOnlyList<ITool> Tools(Effects effects) =>
    [
        new FunctionTool(new("done", "completed sibling", Json("{\"type\":\"object\"}")), (_, _, _) => { effects.Done++; return new ValueTask<string>("committed"); }),
        new FunctionTool(new("approve", "requires approval", Json("{\"type\":\"object\"}")), (_, context, _) =>
        {
            effects.Approve++;
            return new ValueTask<string>(context.Interrupt("approval").GetBoolean() ? "approved" : "declined");
        })
    ];
    private static Agent CreateSource(ModelHistoryBehavior profile, ScriptedModel model, CaptureStore store, Effects effects,
        HookRegistry? hooks = null, IReadOnlyList<IPlugin>? plugins = null)
        => new(new()
        {
            Model = model,
            ModelHistoryBehavior = profile,
            SessionStore = store,
            SessionId = "source-independent",
            Tools = Tools(effects),
            ToolExecution = ToolExecutionStrategy.Sequential,
            Hooks = hooks ?? new HookRegistry(),
            Plugins = plugins ?? Array.Empty<IPlugin>()
        });
    private static void AssertResults(Message message, string approval, ToolResultStatus status)
    {
        Assert.Equal(Role.User, message.Role);
        Assert.Collection(message.Content.OfType<ToolResultBlock>(),
            done => { Assert.Equal("call-0", done.ToolUseId); Assert.Equal("committed", done.Content); Assert.Equal(ToolResultStatus.Success, done.Status); },
            result => { Assert.Equal("call-1", result.ToolUseId); Assert.Equal(approval, result.Content); Assert.Equal(status, result.Status); });
    }
    private static async ValueTask DisposeOwned(Exception? primary, params IAsyncDisposable?[] resources)
    {
        Exception? failure = primary;
        var next = 0;
        foreach (var resource in resources)
        {
            if (resource is null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception error)
            {
                if (failure is null) failure = error;
                else if (!ReferenceEquals(failure, error)) failure.Data["PendingFollowOn.Cleanup." + next++] = error;
            }
        }
        if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Effects { internal int Done; internal int Approve; }
    private sealed class CaptureStore(SessionSnapshot initial) : ISessionStore
    {
        internal SessionSnapshot? Saved { get; private set; } = Copy(initial);
        internal List<SessionSnapshot> Writes { get; } = [];
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(Saved is null ? null : Copy(Saved)); }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Saved = Copy(snapshot); Writes.Add(Saved); return default; }
        private static SessionSnapshot Copy(SessionSnapshot snapshot) => JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot, Web), Web)!;
    }
    private sealed class ScriptedModel(bool stateful = true, bool pause = false) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => stateful;
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int Disposals { get; private set; }
        internal List<IReadOnlyList<Message>> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (StreamCalls != 1) throw new InvalidOperationException("Only one finite provider request is expected.");
            Requests.Add(request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray());
            return Produce(cancellationToken);
        }
        private async IAsyncEnumerable<ModelStreamEvent> Produce([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            token.ThrowIfCancellationRequested();
            yield return new ModelMessageStartEvent();
            if (pause)
            {
                var names = new[] { "done", "approve" };
                for (var i = 0; i < names.Length; i++)
                {
                    yield return new ModelContentBlockStartEvent(i, new ToolUseBlock("call-" + i, names[i], Json("{}")));
                    yield return new ModelContentBlockDeltaEvent(i, ToolInput: "{}");
                    yield return new ModelContentBlockStopEvent(i);
                }
            }
            else
            {
                yield return new ModelContentBlockStartEvent(0);
                yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
                yield return new ModelContentBlockStopEvent(0);
            }
            yield return new ModelMessageStopEvent(pause ? StopReasons.ToolUse : StopReasons.EndTurn) { ModelState = ProviderState() };
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountCalls++; return new(9); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
    private sealed class OwnedFiles : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "primary-pending-followons", Guid.NewGuid().ToString("N"));
        public ValueTask DisposeAsync()
        {
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Owned fixture files escaped task TEMP.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            return default;
        }
    }
    private sealed class RejectingRepository : ISessionRepository
    {
        internal int Calls { get; private set; }
        private Exception Touched() { Calls++; return new InvalidOperationException("Source profile must reject before repository access."); }
        public ValueTask<SessionRecord> CreateSessionAsync(SessionRecord value, CancellationToken token = default) => throw Touched();
        public ValueTask<SessionRecord?> ReadSessionAsync(string id, CancellationToken token = default) => throw Touched();
        public ValueTask CreateAgentAsync(string session, SessionAgentRecord value, CancellationToken token = default) => throw Touched();
        public ValueTask<SessionAgentRecord?> ReadAgentAsync(string session, string id, CancellationToken token = default) => throw Touched();
        public ValueTask UpdateAgentAsync(string session, SessionAgentRecord value, CancellationToken token = default) => throw Touched();
        public ValueTask CreateMessageAsync(string session, string agent, SessionMessageRecord value, CancellationToken token = default) => throw Touched();
        public ValueTask<SessionMessageRecord?> ReadMessageAsync(string session, string agent, long id, CancellationToken token = default) => throw Touched();
        public ValueTask UpdateMessageAsync(string session, string agent, SessionMessageRecord value, CancellationToken token = default) => throw Touched();
        public ValueTask<IReadOnlyList<SessionMessageRecord>> ListMessagesAsync(string session, string agent, int? limit = null, int offset = 0, CancellationToken cancellationToken = default) => throw Touched();
    }
}
