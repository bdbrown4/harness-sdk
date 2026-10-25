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

public sealed class PrimaryModelSourcePauseRecoveryTests
{
    private const string OwnerId = "source-pause-recovery-owner";
    private const string SessionId = "source-pause-recovery-session";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PythonAfterHookFailureAllowsALaterDeniedPassToClearItsOwnPause()
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var run = owned.Create(ModelHistoryBehavior.Python, stateful: true, throwFirstAfter: true);
            var paused = await CaptureFirstPairedPause(run, expectAfterFailure: true);
            var writesBefore = run.Store.Saved.Count;
            run.Probe.DenyAt = 2;

            var denied = await run.Agent.InvokeAsync(null);

            Assert.Equal(StopReasons.EndTurn, denied.StopReason);
            Assert.Equal(2, run.Probe.BeforeInvocation);
            Assert.Equal(2, run.After.Calls);
            AssertNoAdditionalBodiesOrModel(run, expectedApprovals: 1);
            Assert.Empty(run.Agent.Messages);
            AssertSamePending(run.Agent.InterruptState, paused, independent: true);
            Assert.Equal("S1", run.Agent.ModelState.Get<string>("phase"));
            Assert.True(run.Store.Saved.Count > writesBefore);
            await AssertClearedPauseRoundTrip(run, paused);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task MutableSourceAuthorityCanClearItsOwnOldPauseOnALaterDeniedPass(ModelHistoryBehavior profile)
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var run = owned.Create(profile, stateful: false);
            var paused = await CaptureFirstPairedPause(run, expectAfterFailure: false);
            var writesBefore = run.Store.Saved.Count;
            run.Model.Stateful = true;
            run.Probe.DenyAt = 2;

            var denied = await run.Agent.InvokeAsync(null);

            Assert.Equal(StopReasons.EndTurn, denied.StopReason);
            Assert.Equal(2, run.Probe.BeforeInvocation);
            Assert.Equal(2, run.After.Calls);
            AssertNoAdditionalBodiesOrModel(run, expectedApprovals: 1);
            Assert.Empty(run.Agent.Messages);
            AssertSamePending(run.Agent.InterruptState, paused, independent: true);
            Assert.Equal("S1", run.Agent.ModelState.Get<string>("phase"));
            Assert.True(run.Store.Saved.Count > writesBefore);
            await AssertClearedPauseRoundTrip(run, paused);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task ImportedNativePairedPauseSurvivesASourceDeniedPass(ModelHistoryBehavior profile)
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var producer = owned.Create(ModelHistoryBehavior.Native, stateful: false);
            var paused = await CaptureFirstPairedPause(producer, expectAfterFailure: false);
            var native = Assert.IsType<SessionSnapshot>(await producer.Store.ReadFreshAsync());
            Assert.Equal(ModelHistoryBehavior.Native, native.HistoryProfile);
            var store = owned.NewStore();
            await store.SaveAsync(SessionId, native);
            var rawBefore = store.ReadOnlyRecordBytes();
            var receiver = owned.Create(profile, stateful: true, store: store);
            await receiver.Agent.InitializeAsync();
            AssertSamePending(receiver.Agent.InterruptState, paused, independent: false);
            Assert.Equal(native.Messages.Select(message => message.TrackingId), receiver.Agent.Messages.Select(message => message.TrackingId));
            receiver.Probe.DenyAt = 1;

            var denied = await receiver.Agent.InvokeAsync(null);

            Assert.Equal(StopReasons.EndTurn, denied.StopReason);
            Assert.Equal(1, receiver.Probe.BeforeInvocation);
            Assert.Equal(1, receiver.After.Calls);
            Assert.Equal(0, receiver.Model.StreamCalls);
            Assert.Equal(0, receiver.Model.CountCalls);
            Assert.Equal(0, receiver.Model.IteratorDisposals);
            Assert.Empty(receiver.Model.Requests);
            Assert.Equal(0, receiver.Effects.Done);
            Assert.Equal(0, receiver.Effects.Approve);
            Assert.Equal(native.Messages.Select(message => message.TrackingId), receiver.Agent.Messages.Select(message => message.TrackingId));
            AssertSamePending(receiver.Agent.InterruptState, paused, independent: false);
            var saved = Assert.IsType<SessionSnapshot>(await store.ReadFreshAsync());
            Assert.Equal(ModelHistoryBehavior.Native, saved.HistoryProfile);
            AssertSamePending(saved.InterruptState, paused, independent: false);
            Assert.Equal(native.Messages.Select(message => message.TrackingId), saved.Messages.Select(message => message.TrackingId));
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            Assert.Equal(rawBefore, store.ReadOnlyRecordBytes());
            Assert.Single(store.Saved);
            AssertNoAdditionalBodiesOrModel(producer, expectedApprovals: 1);
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
    }

    [Fact]
    public async Task PythonOwnPauseSurvivesAcceptedAnswerAndSecondQuestionAfterAfterHookFailure()
    {
        var owned = new OwnedCase();
        Exception? primary = null;
        try
        {
            var run = owned.Create(ModelHistoryBehavior.Python, stateful: true, throwFirstAfter: true, secondQuestion: true);
            var first = await CaptureFirstPairedPause(run, expectAfterFailure: true);
            var firstQuestion = Assert.Single(first.Interrupts);

            var repaused = await run.Agent.InvokeAsync(null, new InvocationOptions
            {
                InterruptResponses = [new(firstQuestion.Id, Json("true"))]
            });

            Assert.Equal(StopReasons.Interrupt, repaused.StopReason);
            var secondQuestion = Assert.Single(repaused.Interrupts);
            Assert.Equal("second-approval", secondQuestion.Name);
            Assert.Equal("call-1", secondQuestion.ToolUseId);
            Assert.NotEqual(firstQuestion.Id, secondQuestion.Id);
            AssertNoAdditionalBodiesOrModel(run, expectedApprovals: 2);
            Assert.Equal(2, run.Probe.BeforeInvocation);
            Assert.Equal(2, run.After.Calls);
            Assert.Empty(run.Agent.Messages);
            var second = Assert.IsType<InterruptState>(run.Agent.InterruptState);
            Assert.True(second.AssistantMessageIsIndependent);
            Assert.Equal(first.AssistantMessage.TrackingId, second.AssistantMessage.TrackingId);
            AssertCompletedSibling(second);
            Assert.Equal(2, second.Interrupts.Count);
            Assert.Contains(second.Interrupts, question => question.Id == firstQuestion.Id);
            Assert.Contains(second.Interrupts, question => question.Id == secondQuestion.Id);
            var accepted = Assert.Single(second.Responses);
            Assert.Equal(firstQuestion.Id, accepted.InterruptId);
            Assert.True(accepted.Response.GetBoolean());
            var savedPause = Assert.IsType<SessionSnapshot>(await run.Store.ReadFreshAsync());
            Assert.Equal(ModelHistoryBehavior.Python, savedPause.HistoryProfile);
            Assert.Empty(savedPause.Messages);
            Assert.True(Assert.IsType<InterruptState>(savedPause.InterruptState).AssistantMessageIsIndependent);
            Assert.True(JsonElement.DeepEquals(Element(second), Element(savedPause.InterruptState)));
            Assert.Equal("S1", savedPause.ModelState!["phase"].GetString());
            Assert.Null(savedPause.ModelHistoryCount);
            Assert.Null(savedPause.ModelInputReceipt);

            var finished = await run.Agent.InvokeAsync(null, new InvocationOptions
            {
                InterruptResponses = [new(secondQuestion.Id, Json("true"))]
            });

            Assert.Equal(StopReasons.EndTurn, finished.StopReason);
            Assert.Equal("finished", finished.ToString());
            Assert.Equal(1, run.Effects.Done);
            Assert.Equal(3, run.Effects.Approve);
            Assert.Equal(2, run.Model.StreamCalls);
            Assert.Equal(2, run.Model.IteratorDisposals);
            Assert.Equal(2, run.Model.CountCalls);
            Assert.Equal(3, run.Probe.BeforeInvocation);
            Assert.Equal(3, run.After.Calls);
            var continuation = run.Model.Requests[1];
            Assert.Equal("S1", continuation.Phase);
            var resultMessage = Assert.Single(continuation.Messages);
            Assert.Equal(Role.User, resultMessage.Role);
            Assert.Collection(resultMessage.Content.OfType<ToolResultBlock>(),
                result =>
                {
                    Assert.Equal("call-0", result.ToolUseId);
                    Assert.Equal("committed-once", result.Content);
                    Assert.Equal(ToolResultStatus.Success, result.Status);
                },
                result =>
                {
                    Assert.Equal("call-1", result.ToolUseId);
                    Assert.Equal("both approved", result.Content);
                    Assert.Equal(ToolResultStatus.Success, result.Status);
                });
            Assert.Null(run.Agent.InterruptState);
            Assert.Empty(run.Agent.Messages);
            Assert.Equal("S2", run.Agent.ModelState.Get<string>("phase"));
            var completed = Assert.IsType<SessionSnapshot>(await run.Store.ReadFreshAsync());
            Assert.Equal(ModelHistoryBehavior.Python, completed.HistoryProfile);
            Assert.Empty(completed.Messages);
            Assert.Null(completed.InterruptState);
            Assert.Null(completed.ModelHistoryCount);
            Assert.Null(completed.ModelInputReceipt);
            Assert.Equal("S2", completed.ModelState!["phase"].GetString());
            await owned.DisposeAgentsAndAssertBorrowedAsync();
        }
        catch (Exception error) { primary = error; throw; }
        finally { await owned.DisposeAsync(primary); }
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
        internal List<SessionSnapshot> Saved { get; } = [];
        public ValueTask<SessionSnapshot?> LoadAsync(string id, CancellationToken token = default) => _inner.LoadAsync(id, token);
        public async ValueTask SaveAsync(string id, SessionSnapshot snapshot, CancellationToken token = default)
        {
            var detached = JsonSerializer.Deserialize<SessionSnapshot>(JsonSerializer.Serialize(snapshot, Web), Web)!;
            await _inner.SaveAsync(id, snapshot, token);
            Saved.Add(detached);
        }
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
