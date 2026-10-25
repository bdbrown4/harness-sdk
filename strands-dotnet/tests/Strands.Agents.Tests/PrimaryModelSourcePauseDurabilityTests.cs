using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelSourcePauseDurabilityTests
{
    private const string OwnerId = "source-pause-recovery-owner";
    private const string SessionId = "source-pause-recovery-session";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task GenuineSourcePairedPauseRetainsItsOwnershipAcrossANewAgent(ModelHistoryBehavior profile)
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var producer = owned.Create(profile, stateful: false);
            var paused = await CaptureFirstPairedPause(producer, expectAfterFailure: false);
            var paired = Assert.IsType<SessionSnapshot>(await producer.Store.ReadFreshAsync());
            Assert.Equal(profile, paired.HistoryProfile);
            AssertSamePending(paired.InterruptState, paused, independent: false);
            await owned.DisposeAgentAndAssertBorrowedAsync(producer);

            var store = owned.ReopenStore(producer.Store);
            var receiver = owned.Create(profile, stateful: true, store: store);
            receiver.Probe.DenyAt = 1;

            var denied = await receiver.Agent.InvokeAsync(null);

            Assert.Equal(StopReasons.EndTurn, denied.StopReason);
            AssertNoReceiverWork(receiver);
            Assert.Equal("S1", receiver.Agent.ModelState.Get<string>("phase"));
            Assert.Equal("approval", Assert.Single(receiver.Agent.InterruptState!.Interrupts).Name);
            Assert.Empty(receiver.Agent.Messages);
            AssertSamePending(receiver.Agent.InterruptState, paused, independent: true);
            var saved = Assert.IsType<SessionSnapshot>(await store.ReadFreshAsync());
            Assert.Equal(profile, saved.HistoryProfile);
            Assert.Empty(saved.Messages);
            AssertSamePending(saved.InterruptState, paused, independent: true);
            Assert.Equal("S1", saved.ModelState!["phase"].GetString());
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            Assert.NotEmpty(store.Saved);
            AssertNoAdditionalBodiesOrModel(producer, expectedApprovals: 1);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task SourceResaveCannotGiveAnImportedNativePairSourceOwnership(ModelHistoryBehavior profile)
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var producer = owned.Create(ModelHistoryBehavior.Native, stateful: false);
            var paused = await CaptureFirstPairedPause(producer, expectAfterFailure: false);
            var native = Assert.IsType<SessionSnapshot>(await producer.Store.ReadFreshAsync());
            Assert.Equal(ModelHistoryBehavior.Native, native.HistoryProfile);
            await owned.DisposeAgentAndAssertBorrowedAsync(producer);

            var proxyStore = owned.ReopenStore(producer.Store);
            var proxy = owned.Create(profile, stateful: true, store: proxyStore);
            await proxy.Agent.InitializeAsync();
            AssertPairedHistory(proxy.Agent, native, paused);
            proxy.Agent.State.Set("savedBySourceProxy", true);
            proxy.Probe.DenyAt = 1;

            var proxyDenied = await proxy.Agent.InvokeAsync(null);

            Assert.Equal(StopReasons.EndTurn, proxyDenied.StopReason);
            AssertNoReceiverWork(proxy);
            AssertPairedHistory(proxy.Agent, native, paused);
            Assert.NotEmpty(proxyStore.Saved);
            var restamped = Assert.IsType<SessionSnapshot>(await proxyStore.ReadFreshAsync());
            Assert.Equal(profile, restamped.HistoryProfile);
            AssertSamePending(restamped.InterruptState, paused, independent: false);
            Assert.True(restamped.State!["savedBySourceProxy"].GetBoolean());
            Assert.Equal(native.Messages.Select(message => message.TrackingId), restamped.Messages.Select(message => message.TrackingId));
            Assert.Equal("S1", restamped.ModelState!["phase"].GetString());
            Assert.Null(restamped.ModelHistoryCount);
            Assert.Null(restamped.ModelInputReceipt);
            await owned.DisposeAgentAndAssertBorrowedAsync(proxy);

            var receiverStore = owned.ReopenStore(proxyStore);
            var receiver = owned.Create(profile, stateful: true, store: receiverStore);
            receiver.Probe.DenyAt = 1;

            var denied = await receiver.Agent.InvokeAsync(null);

            Assert.Equal(StopReasons.EndTurn, denied.StopReason);
            AssertNoReceiverWork(receiver);
            AssertPairedHistory(receiver.Agent, native, paused);
            Assert.True(receiver.Agent.State.Get<bool>("savedBySourceProxy"));
            Assert.Equal("S1", receiver.Agent.ModelState.Get<string>("phase"));
            var saved = Assert.IsType<SessionSnapshot>(await receiverStore.ReadFreshAsync());
            Assert.Equal(profile, saved.HistoryProfile);
            AssertSamePending(saved.InterruptState, paused, independent: false);
            Assert.Equal(native.Messages.Select(message => message.TrackingId), saved.Messages.Select(message => message.TrackingId));
            Assert.True(saved.State!["savedBySourceProxy"].GetBoolean());
            Assert.Equal("S1", saved.ModelState!["phase"].GetString());
            AssertNoAdditionalBodiesOrModel(producer, expectedApprovals: 1);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task ExplicitInitializationClearsItsSourcePauseBeforeADeniedPassPersistsIt(ModelHistoryBehavior profile)
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var producer = owned.Create(profile, stateful: false);
            var paused = await CaptureFirstPairedPause(producer, expectAfterFailure: false);
            var paired = Assert.IsType<SessionSnapshot>(await producer.Store.ReadFreshAsync());
            var pairedBytes = producer.Store.ReadOnlyRecordBytes();
            await owned.DisposeAgentAndAssertBorrowedAsync(producer);

            var store = owned.ReopenStore(producer.Store);
            var receiver = owned.Create(profile, stateful: true, store: store);
            var initialized = 0;
            receiver.Agent.Hooks.Register<InitializedEvent>((value, _) =>
            {
                initialized++;
                Assert.Empty(value.Agent.Messages);
                AssertSamePending(value.Agent.InterruptState, paused, independent: true);
                Assert.Equal("S1", value.Agent.ModelState.Get<string>("phase"));
                return default;
            });

            await receiver.Agent.InitializeAsync();

            Assert.Equal(1, initialized);
            Assert.Equal(1, store.Loads);
            Assert.Empty(receiver.Agent.Messages);
            AssertSamePending(receiver.Agent.InterruptState, paused, independent: true);
            Assert.Equal("S1", receiver.Agent.ModelState.Get<string>("phase"));
            Assert.True(JsonElement.DeepEquals(Element(paired.State), Element(receiver.Agent.State.GetAll())));
            Assert.Equal(0, receiver.Probe.BeforeInvocation);
            Assert.Equal(0, receiver.After.Calls);
            Assert.Equal(0, receiver.Model.StreamCalls);
            Assert.Equal(0, receiver.Model.CountCalls);
            Assert.Equal(0, receiver.Model.IteratorDisposals);
            Assert.Empty(receiver.Model.Requests);
            Assert.Equal(0, receiver.Effects.Done);
            Assert.Equal(0, receiver.Effects.Approve);
            Assert.Empty(store.Saved);
            Assert.Equal(pairedBytes, store.ReadOnlyRecordBytes());
            var unchanged = Assert.IsType<SessionSnapshot>(await store.ReadFreshAsync());
            Assert.True(JsonElement.DeepEquals(Element(paired), Element(unchanged)));
            AssertSamePending(unchanged.InterruptState, paused, independent: false);

            receiver.Probe.DenyAt = 1;
            var denied = await receiver.Agent.InvokeAsync(null);

            Assert.Equal(StopReasons.EndTurn, denied.StopReason);
            AssertNoReceiverWork(receiver);
            Assert.Equal(1, initialized);
            Assert.Equal(1, store.Loads);
            Assert.Empty(receiver.Agent.Messages);
            AssertSamePending(receiver.Agent.InterruptState, paused, independent: true);
            Assert.Equal("S1", receiver.Agent.ModelState.Get<string>("phase"));
            Assert.True(JsonElement.DeepEquals(Element(paired.State), Element(receiver.Agent.State.GetAll())));
            var saved = Assert.Single(store.Saved);
            Assert.Empty(saved.Messages);
            AssertSamePending(saved.InterruptState, paused, independent: true);
            Assert.Equal(profile, saved.HistoryProfile);
            Assert.True(JsonElement.DeepEquals(Element(paired.State), Element(saved.State)));
            Assert.True(JsonElement.DeepEquals(Element(paired.ModelState), Element(saved.ModelState)));
            await AssertClearedPauseRoundTrip(receiver, paused);
            AssertNoAdditionalBodiesOrModel(producer, expectedApprovals: 1);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task NativeRejectsAGenuineSourcePendingOnlySnapshotBeforeInitialization(ModelHistoryBehavior profile)
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var producer = owned.Create(profile, stateful: false);
            var paused = await CaptureFirstPairedPause(producer, expectAfterFailure: false);
            var snapshot = await producer.Agent.TakeSnapshotAsync(new() { Include = SnapshotFields.InterruptState });
            var field = Assert.Single(snapshot.Data);
            Assert.Equal("interruptState", field.Key);
            Assert.True(field.Value.GetProperty("sourceHistoryOwned").GetBoolean());
            Assert.False(field.Value.TryGetProperty("assistantMessageIsIndependent", out _));
            AssertSamePending(producer.Agent.InterruptState, paused, independent: false);
            await owned.DisposeAgentAndAssertBorrowedAsync(producer);

            var receiver = owned.Create(ModelHistoryBehavior.Native, stateful: true);
            var initialized = 0;
            receiver.Agent.Hooks.Register<InitializedEvent>((_, _) => { initialized++; return default; });
            var originalState = Element(receiver.Agent.State.GetAll());
            var originalModelState = Element(receiver.Agent.ModelState.GetAll());

            await Assert.ThrowsAsync<InvalidDataException>(() => receiver.Agent.LoadSnapshotAsync(snapshot).AsTask());

            Assert.Equal(0, initialized);
            Assert.Equal(0, receiver.Store.Loads);
            Assert.Empty(receiver.Store.Saved);
            Assert.Empty(receiver.Agent.Messages);
            Assert.Null(receiver.Agent.InterruptState);
            Assert.Equal(OwnerId, receiver.Agent.Id);
            Assert.Equal("source-recovery-system", receiver.Agent.SystemPrompt);
            Assert.True(JsonElement.DeepEquals(originalState, Element(receiver.Agent.State.GetAll())));
            Assert.True(JsonElement.DeepEquals(originalModelState, Element(receiver.Agent.ModelState.GetAll())));
            Assert.Equal(0, receiver.Probe.BeforeInvocation);
            Assert.Equal(0, receiver.After.Calls);
            Assert.Equal(0, receiver.Model.StreamCalls);
            Assert.Equal(0, receiver.Model.CountCalls);
            Assert.Equal(0, receiver.Model.IteratorDisposals);
            Assert.Empty(receiver.Model.Requests);
            Assert.Equal(0, receiver.Effects.Done);
            Assert.Equal(0, receiver.Effects.Approve);
            AssertNoAdditionalBodiesOrModel(producer, expectedApprovals: 1);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    private static void AssertNoReceiverWork(Run run)
    {
        Assert.Equal(1, run.Probe.BeforeInvocation);
        Assert.Equal(1, run.After.Calls);
        Assert.Equal(0, run.Model.StreamCalls);
        Assert.Equal(0, run.Model.CountCalls);
        Assert.Equal(0, run.Model.IteratorDisposals);
        Assert.Empty(run.Model.Requests);
        Assert.Equal(0, run.Effects.Done);
        Assert.Equal(0, run.Effects.Approve);
    }

    private static void AssertPairedHistory(Agent agent, SessionSnapshot native, InterruptState original)
    {
        Assert.Equal(native.Messages.Select(message => message.TrackingId), agent.Messages.Select(message => message.TrackingId));
        Assert.True(JsonElement.DeepEquals(Element(native.Messages), Element(agent.Messages)));
        AssertSamePending(agent.InterruptState, original, independent: false);
        Assert.Equal(original.AssistantMessage.TrackingId, agent.Messages.Last().TrackingId);
    }

    private static async Task<InterruptState> CaptureFirstPairedPause(Run run, bool expectAfterFailure)
    {
        if (expectAfterFailure)
        {
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await run.Agent.InvokeAsync("start"); });
            Assert.Same(run.After.Failure, actual);
        }
        else
        {
            var result = await run.Agent.InvokeAsync("start");
            Assert.Equal(StopReasons.Interrupt, result.StopReason);
            Assert.Equal("approval", Assert.Single(result.Interrupts).Name);
        }
        Assert.Equal(1, run.Probe.BeforeInvocation);
        Assert.Equal(1, run.After.Calls);
        AssertNoAdditionalBodiesOrModel(run, expectedApprovals: 1);
        Assert.Equal(2, run.Agent.Messages.Count);
        var paused = Assert.IsType<InterruptState>(run.Agent.InterruptState);
        Assert.False(paused.AssistantMessageIsIndependent);
        Assert.Equal("approval", Assert.Single(paused.Interrupts).Name);
        Assert.Equal("call-1", Assert.Single(paused.Interrupts).ToolUseId);
        Assert.Empty(paused.Responses);
        Assert.Equal(paused.AssistantMessage.TrackingId, run.Agent.Messages[1].TrackingId);
        AssertCompletedSibling(paused);
        Assert.Equal("S1", run.Agent.ModelState.Get<string>("phase"));
        var saved = Assert.IsType<SessionSnapshot>(await run.Store.ReadFreshAsync());
        Assert.Equal(run.Profile, saved.HistoryProfile);
        Assert.Equal(2, saved.Messages.Count);
        AssertSamePending(saved.InterruptState, paused, independent: false);
        Assert.Null(saved.ModelHistoryCount);
        Assert.Null(saved.ModelInputReceipt);
        Assert.Equal("S1", saved.ModelState!["phase"].GetString());
        Assert.NotEmpty(run.Store.Saved);
        foreach (var write in run.Store.Saved.Where(write => write.InterruptState is not null))
        {
            Assert.False(write.InterruptState!.AssistantMessageIsIndependent);
            Assert.Equal(write.InterruptState.AssistantMessage.TrackingId, write.Messages.Last().TrackingId);
        }
        return paused;
    }

    private static void AssertNoAdditionalBodiesOrModel(Run run, int expectedApprovals)
    {
        Assert.Equal(1, run.Effects.Done);
        Assert.Equal(expectedApprovals, run.Effects.Approve);
        Assert.Equal(1, run.Model.StreamCalls);
        Assert.Equal(1, run.Model.IteratorDisposals);
        Assert.Equal(1, run.Model.CountCalls);
        var request = Assert.Single(run.Model.Requests);
        Assert.Equal("S0", request.Phase);
        Assert.Equal("start", Assert.Single(request.Messages).Text);
    }

    private static async Task AssertClearedPauseRoundTrip(Run run, InterruptState original)
    {
        var saved = Assert.IsType<SessionSnapshot>(await run.Store.ReadFreshAsync());
        Assert.Equal(run.Profile, saved.HistoryProfile);
        Assert.Equal(1, saved.SchemaVersion);
        Assert.Empty(saved.Messages);
        AssertSamePending(saved.InterruptState, original, independent: true);
        Assert.Null(saved.ModelHistoryCount);
        Assert.Null(saved.ModelInputReceipt);
        Assert.Equal(OwnerId, saved.AgentId);
        Assert.Equal("source-recovery-system", saved.SystemPrompt);
        Assert.True(saved.State!["seedOnly"].GetBoolean());
        Assert.Equal("S1", saved.ModelState!["phase"].GetString());
        var firstIndependent = run.Store.Saved.FindIndex(snapshot => snapshot.InterruptState?.AssistantMessageIsIndependent == true);
        Assert.True(firstIndependent >= 0);
        foreach (var write in run.Store.Saved.Skip(firstIndependent))
        {
            Assert.Empty(write.Messages);
            AssertSamePending(write.InterruptState, original, independent: true);
        }
    }

    private static void AssertSamePending(InterruptState? value, InterruptState original, bool independent)
    {
        var actual = Assert.IsType<InterruptState>(value);
        Assert.Equal(independent, actual.AssistantMessageIsIndependent);
        Assert.Equal(original.AssistantMessage.TrackingId, actual.AssistantMessage.TrackingId);
        Assert.True(JsonElement.DeepEquals(Element(original.AssistantMessage), Element(actual.AssistantMessage)));
        Assert.Equal(original.Interrupts.Select(question => question.Id), actual.Interrupts.Select(question => question.Id));
        Assert.Equal(original.Responses.Select(response => response.InterruptId), actual.Responses.Select(response => response.InterruptId));
        AssertCompletedSibling(actual);
    }

    private static void AssertCompletedSibling(InterruptState pending)
    {
        Assert.Equal(new[] { "call-0", "call-1" }, pending.AssistantMessage.Content.OfType<ToolUseBlock>().Select(call => call.ToolUseId));
        var completed = Assert.Single(pending.CompletedToolResults);
        Assert.Equal("call-0", completed.ToolUseId);
        Assert.Equal("committed-once", completed.Content);
        Assert.Equal(ToolResultStatus.Success, completed.Status);
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Web);
    private static JsonElement Json(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static Dictionary<string, JsonElement> ProviderState(string phase) => new() { ["phase"] = Element(phase) };
    private static FunctionTool Tool(string name, Func<ToolContext, string> body)
        => new(new(name, name, Json("{\"type\":\"object\"}")), (_, context, _) => new ValueTask<string>(body(context)));

    private sealed record Run(Agent Agent, ScriptedModel Model, RecordingFileStore Store, Effects Effects,
        InvocationProbe Probe, AfterHook After, ModelHistoryBehavior Profile);
    private sealed record RequestCopy(IReadOnlyList<Message> Messages, string? Phase);
    private sealed class Effects { internal int Done; internal int Approve; }
    private sealed class InvocationProbe { internal int BeforeInvocation; internal int DenyAt; }

    private sealed class AfterHook(bool throwFirst) : IPlugin
    {
        private bool _throwNext = throwFirst;
        public string Name => "source-recovery-after-observer";
        internal InvalidOperationException Failure { get; } = new("one-shot source recovery after-hook failure");
        internal int Calls { get; private set; }
        public ValueTask InitializeAsync(Agent agent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            agent.Hooks.Register<AfterInvocationEvent>((_, _) =>
            {
                Calls++;
                if (_throwNext) { _throwNext = false; throw Failure; }
                return default;
            });
            return default;
        }
        public IReadOnlyList<ITool> GetTools() => [];
    }

    private sealed class OwnedCase
    {
        private readonly string _temporaryRoot = Strands.Tests.TestPaths.GetPhysicalTempPath();
        private readonly List<Agent> _agents = [];
        private readonly List<ScriptedModel> _models = [];
        private readonly List<RecordingFileStore> _stores = [];
        private readonly string _directory;
        internal OwnedCase()
            => _directory = Path.GetFullPath(Path.Combine(_temporaryRoot, "strands-source-pause-recovery-" + Guid.NewGuid().ToString("N")));
        internal RecordingFileStore NewStore()
        {
            var store = new RecordingFileStore(Path.Combine(_directory, "session-" + _stores.Count));
            _stores.Add(store); return store;
        }
        internal RecordingFileStore ReopenStore(RecordingFileStore original)
        {
            var store = original.Reopen();
            _stores.Add(store); return store;
        }
        internal async ValueTask DisposeAgentAndAssertBorrowedAsync(Run run)
        {
            await run.Agent.DisposeAsync();
            Assert.True(_agents.Remove(run.Agent));
            Assert.Equal(0, run.Model.Disposals);
        }
        internal Run Create(ModelHistoryBehavior profile, bool stateful, bool throwFirstAfter = false,
            bool secondQuestion = false, RecordingFileStore? store = null)
        {
            var model = new ScriptedModel { Stateful = stateful };
            _models.Add(model);
            store ??= NewStore();
            var effects = new Effects();
            var probe = new InvocationProbe();
            var after = new AfterHook(throwFirstAfter);
            var hooks = new HookRegistry();
            hooks.Register<BeforeInvocationEvent>((value, _) =>
            {
                probe.BeforeInvocation++;
                if (probe.DenyAt == probe.BeforeInvocation) value.CancelReason = "denied before resumed tools";
                return default;
            });
            var agent = new Agent(new()
            {
                Id = OwnerId,
                Model = model,
                ModelHistoryBehavior = profile,
                Hooks = hooks,
                SystemPrompt = "source-recovery-system",
                State = new Dictionary<string, JsonElement> { ["seedOnly"] = Json("true") },
                ModelState = ProviderState("S0"),
                SessionStore = store,
                SessionId = SessionId,
                ToolExecution = ToolExecutionStrategy.Sequential,
                Plugins = [after],
                Tools =
                [
                    Tool("done", _ => { effects.Done++; Assert.Equal(1, effects.Done); return "committed-once"; }),
                    Tool("approve", context =>
                    {
                        effects.Approve++;
                        Assert.Equal(1, model.StreamCalls);
                        Assert.True(context.Interrupt("approval").GetBoolean());
                        if (!secondQuestion) return "approved";
                        Assert.True(context.Interrupt("second-approval").GetBoolean());
                        return "both approved";
                    })
                ]
            });
            _agents.Add(agent);
            return new(agent, model, store, effects, probe, after, profile);
        }
        internal async ValueTask DisposeAgentsAndAssertBorrowedAsync()
        {
            for (var index = _agents.Count - 1; index >= 0; index--) await _agents[index].DisposeAsync();
            _agents.Clear();
            foreach (var model in _models) Assert.Equal(0, model.Disposals);
        }
        internal async ValueTask DisposeAsync(Exception? primary)
        {
            Exception? failure = primary;
            for (var index = _agents.Count - 1; index >= 0; index--)
                try { await _agents[index].DisposeAsync(); }
                catch (Exception error) { failure = Attach(failure, error, "agent." + index); }
            _agents.Clear();
            for (var index = _models.Count - 1; index >= 0; index--)
                try { await _models[index].DisposeAsync(); }
                catch (Exception error) { failure = Attach(failure, error, "model." + index); }
            try { DeleteOwnedDirectory(); }
            catch (Exception error) { failure = Attach(failure, error, "directory"); }
            if (primary is null && failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private void DeleteOwnedDirectory()
        {
            var resolved = Path.GetFullPath(_directory);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(resolved), _temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ||
                !Path.GetFileName(resolved).StartsWith("strands-source-pause-recovery-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup requires its original unique direct temporary child.");
            if (!Directory.Exists(resolved)) return;
            for (var current = new DirectoryInfo(resolved); current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses reparse ancestry.");
            var pending = new Stack<string>();
            pending.Push(resolved);
            while (pending.Count > 0)
                foreach (var entry in Directory.GetFileSystemEntries(pending.Pop(), "*", SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture cleanup refuses reparse entries.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            Directory.Delete(resolved, recursive: true);
        }
        private static Exception Attach(Exception? primary, Exception cleanup, string key)
        {
            if (primary is null) return cleanup;
            if (!ReferenceEquals(primary, cleanup)) primary.Data["SourcePauseRecovery.Cleanup." + key] = cleanup;
            return primary;
        }
    }

    private sealed class RecordingFileStore : ISessionStore
    {
        private readonly string _directory;
        private readonly FileSessionStore _inner;
        internal RecordingFileStore(string directory) { _directory = directory; _inner = new(directory); }
        internal int Loads { get; private set; }
        internal List<SessionSnapshot> Saved { get; } = [];
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken token = default)
        { Loads++; return _inner.LoadAsync(id, token); }
        public async ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken token = default)
        {
            var detached = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot, Web), Web)!;
            await _inner.SaveAsync(id, snapshot, token);
            Saved.Add(detached);
        }
        internal RecordingFileStore Reopen() => new(_directory);
        internal ValueTask<SessionSnapshot?> ReadFreshAsync() => new FileSessionStore(_directory).LoadAsync(SessionId);
        internal byte[] ReadOnlyRecordBytes() => File.ReadAllBytes(Assert.Single(Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)));
    }

    private sealed class ScriptedModel : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful { get; set; }
        internal int StreamCalls { get; private set; }
        internal int CountCalls { get; private set; }
        internal int IteratorDisposals { get; private set; }
        internal int Disposals { get; private set; }
        internal List<RequestCopy> Requests { get; } = [];
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            var turn = ++StreamCalls;
            if (turn > 2) throw new InvalidOperationException("The finite model permits one pause and one completed continuation.");
            Requests.Add(new(request.Messages.Select(message => new Message(message.Role, message.Content, message.TrackingId, message.Metadata)).ToArray(),
                request.ModelState is { } state && state.TryGetValue("phase", out var phase) ? phase.GetString() : null));
            return Produce(turn, cancellationToken);
        }
        private async IAsyncEnumerable<ModelStreamEvent> Produce(int turn, [EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await Task.CompletedTask;
                token.ThrowIfCancellationRequested();
                yield return new ModelMessageStartEvent();
                if (turn == 1)
                {
                    var names = new[] { "done", "approve" };
                    for (var index = 0; index < names.Length; index++)
                    {
                        yield return new ModelContentBlockStartEvent(index, new ToolUseBlock("call-" + index, names[index], Json("{}")));
                        yield return new ModelContentBlockDeltaEvent(index, ToolInput: "{}");
                        yield return new ModelContentBlockStopEvent(index);
                    }
                }
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "finished");
                    yield return new ModelContentBlockStopEvent(0);
                }
                yield return new ModelMessageStopEvent(turn == 1 ? StopReasons.ToolUse : StopReasons.EndTurn)
                { ModelState = ProviderState(turn == 1 ? "S1" : "S2") };
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); CountCalls++; return new ValueTask<long>(9); }
        public ValueTask DisposeAsync() { Disposals++; return default; }
    }
}
