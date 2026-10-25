using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Strands.Agents.Tests;

public sealed class PrimaryModelSourceRestoreFreshCheckpointTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string SessionId = "source-profile-checkpoint";
    private const string CallId = "checkpoint-effect-1";

    [Theory]
    [InlineData(ModelHistoryBehavior.TypeScript)]
    [InlineData(ModelHistoryBehavior.Python)]
    public async Task ASeparateAfterToolsMarkerOnAFreshAgentRetainsUnacknowledgedToolResults(ModelHistoryBehavior profile)
    {
        var effects = 0;
        var calls = new List<string>();
        var store = new JsonMemoryStore();
        var producerModel = new StatefulProbe(true, () => effects);
        var resumedModel = new StatefulProbe(false, () => effects);
        Agent? producer = null;
        Agent? resumed = null;
        Exception? primary = null;
        try
        {
            producer = Create(producerModel, profile, store, () => effects++, calls);
            var modelPause = await producer.InvokeAsync("question");
            Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterModel), modelPause.Checkpoint);
            Assert.Equal(0, effects);
            Assert.Single(producerModel.Requests);

            var toolPause = await producer.InvokeAsync(null, new() { CheckpointResume = modelPause.Checkpoint });

            Assert.Equal(StopReasons.Checkpoint, toolPause.StopReason);
            Assert.Equal(new AgentCheckpoint(CheckpointPosition.AfterTools), toolPause.Checkpoint);
            Assert.Equal(1, effects);
            Assert.Equal(CallId, Assert.Single(calls));
            Assert.Single(producerModel.Requests);
            AssertAcceptedHistory(producer.Messages);
            var saved = store.ReadSaved();
            AssertAcceptedHistory(saved.Messages);
            AssertState(saved.ModelState, "S1");
            Assert.Null(saved.ModelHistoryCount);
            Assert.Null(saved.ModelInputReceipt);
            Assert.Null(saved.InterruptState);
            var marker = JsonSerializer.Deserialize<AgentCheckpoint>(JsonSerializer.Serialize(toolPause.Checkpoint, Web), Web);
            Assert.Equal(toolPause.Checkpoint, marker);
            Assert.NotNull(marker);
            await producer.DisposeAsync();
            producer = null;
            Assert.Equal(0, producerModel.DisposeCalls);

            resumed = Create(resumedModel, profile, store, () => effects++, calls);
            var completed = await resumed.InvokeAsync(null, new() { CheckpointResume = marker });

            Assert.Equal(StopReasons.EndTurn, completed.StopReason);
            Assert.Equal("done", completed.Message!.Text);
            Assert.Null(completed.Checkpoint);
            Assert.Equal(2, store.Loads);
            Assert.Equal(1, effects);
            Assert.Equal(CallId, Assert.Single(calls));
            var request = Assert.Single(resumedModel.Requests);
            AssertAcceptedHistory(request.Messages);
            AssertState(request.ModelState, "S1");
            AssertState(resumed.ModelState.GetAll(), "S2");
            var finished = store.ReadSaved();
            Assert.Empty(finished.Messages);
            AssertState(finished.ModelState, "S2");
            Assert.Null(finished.ModelHistoryCount);
            Assert.Null(finished.ModelInputReceipt);
            Assert.Null(finished.InterruptState);
            Assert.Empty(resumed.Messages);
            Assert.Equal(1, resumedModel.IteratorDisposals);
            Assert.Equal(1, producerModel.IteratorDisposals);
            Assert.Equal(0, producerModel.DisposeCalls);
            Assert.Equal(0, resumedModel.DisposeCalls);
        }
        catch (Exception error) { primary = error; throw; }
        finally { await DisposeFixture(resumed, producer, resumedModel, producerModel, primary); }
    }

    private static Agent Create(IModel model, ModelHistoryBehavior profile, ISessionStore store, Action effect, List<string> calls)
        => new(new()
        {
            Model = model,
            ModelHistoryBehavior = profile,
            CheckpointBehavior = CheckpointBehavior.PythonReplay,
            SessionStore = store,
            SessionId = SessionId,
            Tools = [new FunctionTool(new("effect", "Record one effect", JsonSerializer.SerializeToElement(new { type = "object" })),
                (input, context, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Assert.Equal(CallId, context.ToolUse.ToolUseId);
                    Assert.Equal(7, input.GetProperty("value").GetInt32());
                    calls.Add(context.ToolUse.ToolUseId);
                    effect();
                    return new ValueTask<string>("accepted");
                })]
        });

    private static void AssertPendingCall(Message message)
    {
        Assert.Equal(Role.Assistant, message.Role);
        var call = Assert.IsType<ToolUseBlock>(Assert.Single(message.Content));
        Assert.Equal(CallId, call.ToolUseId);
        Assert.Equal("effect", call.Name);
        Assert.Equal(7, call.Input.GetProperty("value").GetInt32());
    }

    private static void AssertAcceptedHistory(IReadOnlyList<Message> messages)
        => Assert.Collection(messages,
            message => { Assert.Equal(Role.User, message.Role); Assert.Equal("question", message.Text); },
            message => AssertPendingCall(message),
            message =>
            {
                Assert.Equal(Role.User, message.Role);
                var result = Assert.IsType<ToolResultBlock>(Assert.Single(message.Content));
                Assert.Equal(CallId, result.ToolUseId);
                Assert.Equal(ToolResultStatus.Success, result.Status);
                Assert.Equal("accepted", result.Content);
            });

    private static void AssertState(IReadOnlyDictionary<string, JsonElement>? state, string expected)
    {
        Assert.NotNull(state);
        Assert.Equal(expected, state["responseId"].GetString());
        Assert.Equal(23, state["nested"].GetProperty("sequence").GetInt32());
    }

    private static async ValueTask DisposeFixture(Agent? resumed, Agent? producer, StatefulProbe resumedModel, StatefulProbe producerModel, Exception? primary)
    {
        Exception? cleanupFailure = null;
        foreach (var agent in new[] { resumed, producer })
        {
            if (agent is null) continue;
            try { await agent.DisposeAsync(); }
            catch (Exception error)
            {
                if (cleanupFailure is null) cleanupFailure = error;
                else cleanupFailure.Data["CheckpointSourceRestore.OtherAgentCleanup"] = error;
            }
        }
        try
        {
            Assert.Equal(0, resumedModel.DisposeCalls);
            Assert.Equal(0, producerModel.DisposeCalls);
        }
        catch (Exception error)
        {
            if (cleanupFailure is null) cleanupFailure = error;
            else cleanupFailure.Data["CheckpointSourceRestore.BorrowedOwnership"] = error;
        }
        await resumedModel.DisposeAsync();
        await producerModel.DisposeAsync();
        if (cleanupFailure is null) return;
        if (primary is not null) primary.Data["CheckpointSourceRestore.Cleanup"] = cleanupFailure;
        else ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private sealed class JsonMemoryStore : ISessionStore
    {
        private byte[]? _saved;
        internal int Loads { get; private set; }
        internal int Saves { get; private set; }
        internal SessionSnapshot ReadSaved()
            => JsonSerializer.Deserialize<SessionSnapshot>(_saved ?? throw new InvalidOperationException("No saved session."), Web)!;
        public ValueTask<SessionSnapshot?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(SessionId, sessionId);
            Loads++;
            return new ValueTask<SessionSnapshot?>(_saved is null ? null : ReadSaved());
        }
        public ValueTask SaveAsync(string sessionId, SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(SessionId, sessionId);
            _saved = JsonSerializer.SerializeToUtf8Bytes(snapshot, Web);
            Saves++;
            return default;
        }
    }

    private sealed class StatefulProbe(bool producer, Func<int> effects) : IStatefulModel, IModelTokenCounter, IAsyncDisposable
    {
        public bool Stateful => true;
        internal List<ModelRequest> Requests { get; } = [];
        internal int IteratorDisposals { get; private set; }
        internal int DisposeCalls { get; private set; }
        public ValueTask<long> CountTokensAsync(ModelRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new ValueTask<long>(3); }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with
            {
                Messages = request.Messages.ToArray(),
                ModelState = request.ModelState?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone())
            });
            try
            {
                Assert.Equal(producer ? 0 : 1, effects());
                yield return new ModelMessageStartEvent();
                if (producer)
                    yield return new ModelContentBlockStartEvent(0, new ToolUseBlock(CallId, "effect", JsonSerializer.SerializeToElement(new { value = 7 })));
                else
                {
                    yield return new ModelContentBlockStartEvent(0);
                    yield return new ModelContentBlockDeltaEvent(0, Text: "done");
                }
                yield return new ModelContentBlockStopEvent(0);
                yield return new ModelMessageStopEvent(producer ? StopReasons.ToolUse : StopReasons.EndTurn)
                {
                    ModelState = new Dictionary<string, JsonElement>
                    {
                        ["responseId"] = JsonSerializer.SerializeToElement(producer ? "S1" : "S2"),
                        ["nested"] = JsonSerializer.SerializeToElement(new { sequence = 23 })
                    }
                };
                await Task.CompletedTask;
            }
            finally { IteratorDisposals++; }
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return default; }
    }
}
